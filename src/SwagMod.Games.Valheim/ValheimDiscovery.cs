using Microsoft.Win32;
using SwagMod.Domain;

namespace SwagMod.Games.Valheim;

public static class ValheimDiscovery
{
    public static IReadOnlyList<GameInstallation> Discover()
    {
        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var path = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?
                    .GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(path)) steamRoots.Add(path);
            }
            catch { /* Registry access may be restricted. */ }
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            steamRoots.Add(Path.Combine(pf, "Steam"));
        }
        var found = new Dictionary<string, GameInstallation>(StringComparer.OrdinalIgnoreCase);
        foreach (var steamRoot in steamRoots)
        {
            if (!Directory.Exists(steamRoot)) continue;
            var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };
            var foldersFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(foldersFile))
                foreach (var line in File.ReadLines(foldersFile))
                {
                    var chunks = line.Split('"');
                    if (chunks.Length < 4 || chunks[1] != "path") continue;
                    var library = chunks[3].Replace(@"\\", @"\");
                    if (Directory.Exists(library)) libraries.Add(library);
                }
            foreach (var library in libraries)
            {
                var manifest = Path.Combine(library, "steamapps", "appmanifest_892970.acf");
                if (!File.Exists(manifest)) continue;
                var game = Path.Combine(library, "steamapps", "common", "Valheim");
                if (IsValid(game))
                    found[Path.GetFullPath(game)] = new GameInstallation("valheim", game, "Steam");
            }
        }
        return found.Values.ToArray();
    }

    public static bool IsValid(string gameDirectory) =>
        Directory.Exists(gameDirectory) &&
        File.Exists(Path.Combine(gameDirectory, OperatingSystem.IsWindows() ? "valheim.exe" : "valheim.x86_64"));
}
