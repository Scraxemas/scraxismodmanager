using System.IO.Compression;

namespace SwagMod.Infrastructure;

public static class SafeArchive
{
    // All archive writes stay in an isolated, disposable staging root.
    public static void Extract(string zipPath, string target, string kind, string packageId)
    {
        Directory.CreateDirectory(target);
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > 10000) throw new InvalidDataException("Too many archive entries.");
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (entry.FullName.Contains('\\') || entry.FullName.StartsWith('/') ||
                entry.FullName.Contains(':'))
                throw new InvalidDataException("Invalid archive path: " + entry.FullName);
            // Unix symlinks in ZIP: mode 0120000.
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Symlinks are not allowed in packages.");
            expanded = checked(expanded + entry.Length);
            if (expanded > 1024L * 1024 * 1024)
                throw new InvalidDataException("Expanded package exceeds 1 GB.");

            string relative;
            if (kind == "loader")
            {
                // Find the loader's actual game-root payload even when nested
                // inside Thunderstore's package wrapper.
                var parts = entry.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var index = Array.FindIndex(parts, x => x.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) || x.Equals("doorstop_libs", StringComparison.OrdinalIgnoreCase) || x.Equals("unstripped_corlib", StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                    relative = Path.Combine(parts.Skip(index).ToArray());
                else
                {
                    var rootFile = parts.Last();
                    if (rootFile is not ("winhttp.dll" or "doorstop_config.ini"))
                        continue;
                    relative = rootFile;
                }
            }
            else
            {
                // Retain all mod assets but skip Thunderstore package metadata.
                if (entry.FullName is "manifest.json" or "icon.png" or "README.md" or "CHANGELOG.md")
                    continue;
                relative = Path.Combine("BepInEx", "plugins", packageId,
                    entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            }

            if (Path.IsPathRooted(relative) ||
                relative.Split(Path.DirectorySeparatorChar).Contains(".."))
                throw new InvalidDataException("Archive traversal rejected.");
            var full = Path.GetFullPath(Path.Combine(target, relative));
            var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Archive escapes staging directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            entry.ExtractToFile(full, true);
        }
    }
}
