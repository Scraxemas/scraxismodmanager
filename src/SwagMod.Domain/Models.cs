using System.Text.Json.Serialization;

namespace SwagMod.Domain;

public enum DeploymentMode { Isolated, Direct }

public sealed class PackManifest
{
    public int SchemaVersion { get; set; }
    public PackInfo Pack { get; set; } = new();
    public GameInfo Game { get; set; } = new();
    public string? Description { get; set; }
}

public sealed class PackInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

public sealed class GameInfo
{
    public string Id { get; set; } = "";
    public string Platform { get; set; } = "steam";
    public string AppId { get; set; } = "892970";
}

public sealed class PackLock
{
    public int SchemaVersion { get; set; }
    public string PackId { get; set; } = "";
    public string PackVersion { get; set; } = "";
    public List<LockedPackage> Packages { get; set; } = new();
}

public sealed class LockedPackage
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    // "loader" installs an approved BepInEx zip. "plugin" installs to plugins/<id>.
    public string Kind { get; set; } = "plugin";
    public List<string> Dependencies { get; set; } = new();
}

public sealed record GitHubPack(PackManifest Manifest, PackLock Lock, string Repository, string Folder, string Ref);
public sealed record GameInstallation(string GameId, string Directory, string Source);
public sealed record InstalledProfile(string PackId, string PackVersion, DeploymentMode Mode, string GameDirectory, string ProfileDirectory);
public sealed record InstallProgress(string Phase, string Detail, double Fraction);
public sealed record InstallPlan(string ProfileDirectory, string GameDirectory, DeploymentMode Mode, IReadOnlyList<LockedPackage> Packages, string Summary);

public static class PackValidation
{
    public static void Validate(GitHubPack pack)
    {
        if (pack.Manifest.SchemaVersion != 1 || pack.Lock.SchemaVersion != 1)
            throw new InvalidDataException("Unsupported pack or lockfile schema.");
        if (pack.Manifest.Game.Id != "valheim" || pack.Manifest.Game.AppId != "892970")
            throw new InvalidDataException("Alpha 0.1 supports Steam Valheim only.");
        if (string.IsNullOrWhiteSpace(pack.Manifest.Pack.Id) ||
            !System.Text.RegularExpressions.Regex.IsMatch(pack.Manifest.Pack.Id, @"^[a-z0-9][a-z0-9-]{0,63}$"))
            throw new InvalidDataException("Pack ID must be lowercase letters, digits or hyphens.");
        if (pack.Manifest.Pack.Id != pack.Lock.PackId ||
            pack.Manifest.Pack.Version != pack.Lock.PackVersion)
            throw new InvalidDataException("Manifest and lockfile versions do not match.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in pack.Lock.Packages)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(mod.Id, @"^[A-Za-z0-9_-]{1,120}$") ||
                !ids.Add(mod.Id))
                throw new InvalidDataException("Invalid or duplicate package ID: " + mod.Id);
            if (mod.Kind is not ("loader" or "plugin"))
                throw new InvalidDataException("Unsupported package kind: " + mod.Kind);
            if (!System.Text.RegularExpressions.Regex.IsMatch(mod.Sha256, @"^[0-9a-fA-F]{64}$"))
                throw new InvalidDataException("Invalid SHA-256 for package " + mod.Id);
            if (!Uri.TryCreate(mod.Url, UriKind.Absolute, out var u) ||
                u.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Packages require HTTPS download URLs.");
        }
        var set = ids;
        foreach (var mod in pack.Lock.Packages)
            foreach (var dependency in mod.Dependencies)
                if (!set.Contains(dependency))
                    throw new InvalidDataException($"Missing pinned dependency {dependency} for {mod.Id}.");
        if (pack.Lock.Packages.Count(x => x.Kind == "loader") > 1)
            throw new InvalidDataException("Only one loader package is allowed.");
    }
}
