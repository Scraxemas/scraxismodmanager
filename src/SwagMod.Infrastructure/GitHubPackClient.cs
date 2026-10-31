using System.Text.Json;
using System.Text.RegularExpressions;
using SwagMod.Domain;

namespace SwagMod.Infrastructure;

public sealed class GitHubPackClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public async Task<GitHubPack> LoadAsync(string repository, string folder = "",
        string gitRef = "main", CancellationToken ct = default)
    {
        repository = repository.Trim().TrimEnd('/');
        repository = repository.Replace("https://github.com/", "", StringComparison.OrdinalIgnoreCase);
        if (!Regex.IsMatch(repository, @"^[\w.-]+/[\w.-]+$") ||
            !Regex.IsMatch(gitRef, @"^[A-Za-z0-9._/-]{1,120}$") ||
            gitRef.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Expected a GitHub owner/repo and a valid branch or tag.");
        folder = folder.Trim().Trim('/');
        if (folder.Length > 0 && (folder.Contains("..", StringComparison.Ordinal) ||
            !Regex.IsMatch(folder, @"^[A-Za-z0-9_./-]+$")))
            throw new ArgumentException("Invalid pack folder.");

        var prefix = $"https://raw.githubusercontent.com/{repository}/{Uri.EscapeDataString(gitRef)}";
        if (folder.Length != 0) prefix += "/" + folder;
        using var m = await http.GetAsync(prefix + "/pack.json", ct);
        m.EnsureSuccessStatusCode();
        using var l = await http.GetAsync(prefix + "/pack.lock.json", ct);
        l.EnsureSuccessStatusCode();

        var manifest = await JsonSerializer.DeserializeAsync<PackManifest>(
            await m.Content.ReadAsStreamAsync(ct), JsonOptions, ct)
            ?? throw new InvalidDataException("Empty pack manifest.");
        var lockfile = await JsonSerializer.DeserializeAsync<PackLock>(
            await l.Content.ReadAsStreamAsync(ct), JsonOptions, ct)
            ?? throw new InvalidDataException("Empty pack lockfile.");
        var pack = new GitHubPack(manifest, lockfile, repository, folder, gitRef);
        PackValidation.Validate(pack);
        return pack;
    }
}
