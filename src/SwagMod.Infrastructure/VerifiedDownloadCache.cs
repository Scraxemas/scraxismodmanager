using System.Buffers;
using System.Security.Cryptography;
using SwagMod.Domain;

namespace SwagMod.Infrastructure;

public sealed class VerifiedDownloadCache(HttpClient http, string cacheRoot)
{
    private const long MaxBytes = 512L * 1024 * 1024;
    public static bool IsAllowed(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host.ToLowerInvariant();
        return host is "thunderstore.io" or "github.com" or "raw.githubusercontent.com"
            or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"
            || host.EndsWith(".thunderstore.io", StringComparison.Ordinal)
            || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
    }

    public async Task<string> EnsureAsync(LockedPackage pkg, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cacheRoot);
        var digest = pkg.Sha256.ToLowerInvariant();
        var dest = Path.Combine(cacheRoot, digest + ".zip");
        if (File.Exists(dest))
        {
            if (await MatchesHashAsync(dest, digest, ct)) return dest;
            File.Delete(dest);
        }

        var tmp = dest + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            var uri = new Uri(pkg.Url);
            // Disable HttpClient auto redirects in the caller's handler: we validate
            // every redirect target, not merely the initial host.
            for (var redirects = 0; ; redirects++)
            {
                if (redirects > 8 || !IsAllowed(uri))
                    throw new InvalidDataException("Untrusted package download URL: " + uri);
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var next = response.Headers.Location ??
                        throw new InvalidDataException("Redirect without Location.");
                    uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxBytes)
                    throw new InvalidDataException("Package exceeds download size limit.");
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    long received = 0;
                    int n;
                    while ((n = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        received += n;
                        if (received > MaxBytes) throw new InvalidDataException("Download exceeds size limit.");
                        sha.AppendData(buffer, 0, n);
                        await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                await output.FlushAsync(ct);
                if (!Convert.ToHexString(sha.GetHashAndReset()).Equals(digest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 mismatch: " + pkg.Id);
                break;
            }
            File.Move(tmp, dest, true);
            return dest;
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    public static async Task<bool> MatchesHashAsync(string path, string digest, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        var computed = await SHA256.HashDataAsync(file, ct);
        return Convert.ToHexString(computed).Equals(digest, StringComparison.OrdinalIgnoreCase);
    }
}
