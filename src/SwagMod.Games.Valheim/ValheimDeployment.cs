using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SwagMod.Domain;
using SwagMod.Infrastructure;

namespace SwagMod.Games.Valheim;

/// <summary>
/// Conservative game-root deployment. Does not take ownership of a pre-existing
/// unmanaged BepInEx folder. Replacements require all managed files to match
/// the previous ownership ledger. Every package is staged before touching the game.
/// </summary>
public sealed class ValheimDeployment(VerifiedDownloadCache cache, string storageRoot)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] RootDirectories = ["doorstop_libs", "unstripped_corlib"];

    private string LedgerPath(string game)
    {
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            Path.GetFullPath(game).ToLowerInvariant()))).ToLowerInvariant()[..20];
        return Path.Combine(storageRoot, "installations", hash + ".json");
    }

    public InstalledState? GetState(string game)
    {
        var path = LedgerPath(game);
        return File.Exists(path) ? JsonSerializer.Deserialize<InstalledState>(File.ReadAllText(path), Json) : null;
    }

    public InstallPlan Plan(GitHubPack pack, string game, DeploymentMode mode)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Valheim deployment is Windows-only in Alpha 0.1.");
        if (!ValheimDiscovery.IsValid(game)) throw new DirectoryNotFoundException("Valheim executable was not found.");
        PackValidation.Validate(pack);
        var directory = Path.Combine(storageRoot, "profiles", "valheim", pack.Manifest.Pack.Id);
        return new InstallPlan(directory, Path.GetFullPath(game), mode, pack.Lock.Packages,
            $"{pack.Manifest.Pack.Name} {pack.Manifest.Pack.Version}: {pack.Lock.Packages.Count} pinned packages ({mode}).");
    }

    public async Task InstallAsync(GitHubPack pack, InstallPlan plan,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows is required.");
        if (Process.GetProcessesByName("valheim").Length > 0)
            throw new IOException("Close Valheim before installing or updating.");
        if (pack.Lock.Packages.Count(x => x.Kind == "loader") != 1)
            throw new InvalidDataException("Install requires one pinned BepInEx loader package.");
        var game = plan.GameDirectory;
        var bepinex = Path.Combine(game, "BepInEx");
        var previous = GetState(game);
        if (previous is null && (Directory.Exists(bepinex) || File.Exists(Path.Combine(game, "winhttp.dll")) ||
                                  File.Exists(Path.Combine(game, "doorstop_config.ini")) ||
                                  RootDirectories.Any(d => Directory.Exists(Path.Combine(game, d)))))
            throw new IOException("Unmanaged BepInEx files detected. Move them aside or import them before installing.");
        if (previous is not null)
        {
            if (!string.Equals(previous.GameDirectory, game, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(previous.PackId, pack.Manifest.Pack.Id, StringComparison.OrdinalIgnoreCase) ||
                previous.Mode != plan.Mode)
                throw new IOException("An existing managed profile is active. Switching packs/modes is not implemented in Alpha 0.1.");
            VerifyOwnedFiles(previous);
        }

        // Download and stage fully before modifying the game.
        var profile = plan.ProfileDirectory;
        Directory.CreateDirectory(profile);
        var candidate = Path.Combine(profile, "candidate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(candidate);
        try
        {
            var packages = pack.Lock.Packages.OrderBy(x => x.Kind == "loader" ? 0 : 1).ToArray();
            for (var i = 0; i < packages.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var pkg = packages[i];
                progress?.Report(new("Download", pkg.Id, (double)i / packages.Length));
                var zip = await cache.EnsureAsync(pkg, ct);
                SafeArchive.Extract(zip, candidate, pkg.Kind, pkg.Id);
            }
            if (!Directory.Exists(Path.Combine(candidate, "BepInEx", "core")) ||
                !File.Exists(Path.Combine(candidate, "winhttp.dll")) ||
                !File.Exists(Path.Combine(candidate, "doorstop_config.ini")))
                throw new InvalidDataException("BepInEx loader archive did not contain expected Windows bootstrap files.");

            // Record the exact staged files, allowing the next update to reject
            // unowned additions and manual edits without deleting them.
            var stagedHashes = HashTree(Path.Combine(candidate, "BepInEx"));
            var stagedRootHashes = RootDirectories.Where(d => Directory.Exists(Path.Combine(candidate, d)))
                .ToDictionary(d => d, d => HashTree(Path.Combine(candidate, d)), StringComparer.OrdinalIgnoreCase);
            if (stagedHashes.Count == 0) throw new InvalidDataException("Empty BepInEx payload.");
            ct.ThrowIfCancellationRequested();
            progress?.Report(new("Commit", "Activating verified profile", 0.9));
            var backupRoot = Path.Combine(storageRoot, "backups", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backupRoot);
            var oldLive = Path.Combine(profile, "live");
            var previousLive = Path.Combine(backupRoot, "previous-live");
            var gameBackupRoot = Path.Combine(game, ".swagmod-rollback-" + Guid.NewGuid().ToString("N"));
            var oldPhysical = Path.Combine(gameBackupRoot, "old-game-BepInEx");
            var oldBootstrap = Path.Combine(backupRoot, "bootstrap");
            Directory.CreateDirectory(oldBootstrap);
            string? newTempBep = null;
            bool changedGame = false;
            try
            {
                if (previous is not null)
                {
                    Directory.CreateDirectory(gameBackupRoot);
                    // Save bootstrap files for rollback.
                    foreach (var filename in new[] { "winhttp.dll", "doorstop_config.ini" })
                        if (File.Exists(Path.Combine(game, filename)))
                            File.Copy(Path.Combine(game, filename), Path.Combine(oldBootstrap, filename), true);
                    foreach (var folder in RootDirectories)
                    {
                        var activeRoot = Path.Combine(game, folder);
                        if (Directory.Exists(activeRoot)) Directory.Move(activeRoot, Path.Combine(gameBackupRoot, folder));
                    }
                    if (Directory.Exists(bepinex))
                    {
                        if (previous.Mode == DeploymentMode.Isolated)
                        {
                            if (!new DirectoryInfo(bepinex).Attributes.HasFlag(FileAttributes.ReparsePoint))
                                throw new IOException("Managed BepInEx junction was replaced externally.");
                            Directory.Delete(bepinex); // Junction only; never recursive.
                        }
                        else Directory.Move(bepinex, oldPhysical);
                    }
                }
                if (Directory.Exists(oldLive)) Directory.Move(oldLive, previousLive);
                Directory.Move(candidate, oldLive);
                changedGame = true;

                if (plan.Mode == DeploymentMode.Isolated)
                    CreateJunction(bepinex, Path.Combine(oldLive, "BepInEx"));
                else
                {
                    // Build on the game's drive before an atomic directory rename.
                    newTempBep = Path.Combine(game, ".swagmod-" + Guid.NewGuid().ToString("N"));
                    CopyTree(Path.Combine(oldLive, "BepInEx"), newTempBep);
                    Directory.Move(newTempBep, bepinex);
                    newTempBep = null;
                }
                foreach (var folder in RootDirectories)
                    if (Directory.Exists(Path.Combine(oldLive, folder)))
                        CopyTree(Path.Combine(oldLive, folder), Path.Combine(game, folder));
                foreach (var filename in new[] { "winhttp.dll", "doorstop_config.ini" })
                    File.Copy(Path.Combine(oldLive, filename), Path.Combine(game, filename), true);

                var state = new InstalledState {
                    PackId = pack.Manifest.Pack.Id,
                    PackVersion = pack.Manifest.Pack.Version,
                    Mode = plan.Mode,
                    GameDirectory = game,
                    ProfileDirectory = profile,
                    BepInExHashes = stagedHashes,
                    RootDirectoryHashes = stagedRootHashes,
                    BootstrapHashes = new Dictionary<string, string> {
                        ["winhttp.dll"] = HashFile(Path.Combine(game, "winhttp.dll")),
                        ["doorstop_config.ini"] = HashFile(Path.Combine(game, "doorstop_config.ini"))
                    }
                };
                var ledger = LedgerPath(game);
                Directory.CreateDirectory(Path.GetDirectoryName(ledger)!);
                var stateTemp = ledger + ".tmp";
                await File.WriteAllTextAsync(stateTemp, JsonSerializer.Serialize(state, Json), ct);
                File.Move(stateTemp, ledger, true);
                progress?.Report(new("Complete", "Installation verified", 1));
            }
            catch
            {
                if (changedGame)
                {
                    if (Directory.Exists(bepinex))
                    {
                        if (plan.Mode == DeploymentMode.Isolated)
                        {
                            if (new DirectoryInfo(bepinex).Attributes.HasFlag(FileAttributes.ReparsePoint))
                                Directory.Delete(bepinex);
                        }
                        else Directory.Delete(bepinex, true);
                    }
                    if (Directory.Exists(oldLive)) Directory.Delete(oldLive, true);
                    if (Directory.Exists(previousLive)) Directory.Move(previousLive, oldLive);
                }
                foreach (var folder in RootDirectories)
                {
                    var activeRoot = Path.Combine(game, folder);
                    if (changedGame && Directory.Exists(activeRoot)) Directory.Delete(activeRoot, true);
                    var oldRoot = Path.Combine(gameBackupRoot, folder);
                    if (Directory.Exists(oldRoot)) Directory.Move(oldRoot, activeRoot);
                }
                if (Directory.Exists(oldPhysical)) Directory.Move(oldPhysical, bepinex);
                else if (previous is not null && previous.Mode == DeploymentMode.Isolated &&
                         !Directory.Exists(bepinex) && Directory.Exists(Path.Combine(oldLive, "BepInEx")))
                    CreateJunction(bepinex, Path.Combine(oldLive, "BepInEx"));
                foreach (var filename in new[] { "winhttp.dll", "doorstop_config.ini" })
                {
                    var backup = Path.Combine(oldBootstrap, filename);
                    if (File.Exists(backup)) File.Copy(backup, Path.Combine(game, filename), true);
                    else if (previous is null && File.Exists(Path.Combine(game, filename)))
                        File.Delete(Path.Combine(game, filename));
                }
                throw;
            }
            finally
            {
                if (newTempBep is not null && Directory.Exists(newTempBep)) Directory.Delete(newTempBep, true);
                // Backups retained for forensic recovery, not silently discarded.
            }
        }
        finally { if (Directory.Exists(candidate)) Directory.Delete(candidate, true); }
    }

    private static void VerifyOwnedFiles(InstalledState state)
    {
        var path = Path.Combine(state.GameDirectory, "BepInEx");
        if (!Directory.Exists(path)) throw new IOException("Managed BepInEx directory is missing; do not overwrite until recovered.");
        var actual = HashTree(path);
        if (actual.Count != state.BepInExHashes.Count ||
            actual.Any(kv => !state.BepInExHashes.TryGetValue(kv.Key, out var hash) ||
                             !hash.Equals(kv.Value, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("BepInEx files have changed outside SwagMod; backup or reconcile them before updating.");
        foreach (var folder in RootDirectories)
        {
            var location = Path.Combine(state.GameDirectory, folder);
            var owned = state.RootDirectoryHashes.TryGetValue(folder, out var expected);
            if (Directory.Exists(location) != owned)
                throw new IOException("Unmanaged or missing loader directory: " + folder);
            if (owned)
            {
                if (new DirectoryInfo(location).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Loader directory has an unmanaged reparse point: " + folder);
                var actualRoot = HashTree(location);
                if (actualRoot.Count != expected!.Count || actualRoot.Any(kv => !expected.TryGetValue(kv.Key, out var hash) || hash != kv.Value))
                    throw new IOException("Loader support files have changed: " + folder);
            }
        }
        foreach (var (file, hash) in state.BootstrapHashes)
            if (!File.Exists(Path.Combine(state.GameDirectory, file)) ||
                !HashFile(Path.Combine(state.GameDirectory, file)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Managed loader bootstrap changed outside SwagMod: " + file);
    }

    private static Dictionary<string, string> HashTree(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(x => Path.GetRelativePath(root, x).Replace('\\', '/'),
                HashFile, StringComparer.OrdinalIgnoreCase);

    private static string HashFile(string file) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

    private static void CopyTree(string source, string target)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
    }

    private static void CreateJunction(string junction, string target)
    {
        if (Directory.Exists(junction) || File.Exists(junction))
            throw new IOException("Refusing to replace existing BepInEx path.");
        var start = new ProcessStartInfo("cmd.exe") {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        // cmd.exe requires the /c command as a single command line, with
        // native cmd quoting rather than ProcessStartInfo.ArgumentList quoting.
        start.Arguments = $"/d /c mklink /J \"{junction}\" \"{target}\"";
        using var process = Process.Start(start) ?? throw new IOException("Unable to start junction creator.");
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(junction))
            throw new IOException("Creating BepInEx junction failed: " + process.StandardError.ReadToEnd());
    }
}

public sealed class InstalledState
{
    public string PackId { get; set; } = "";
    public string PackVersion { get; set; } = "";
    public DeploymentMode Mode { get; set; }
    public string GameDirectory { get; set; } = "";
    public string ProfileDirectory { get; set; } = "";
    public Dictionary<string, string> BepInExHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, string>> RootDirectoryHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> BootstrapHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
