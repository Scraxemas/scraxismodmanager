using SwagMod.Domain;
using SwagMod.Infrastructure;
using Xunit;

namespace SwagMod.UnitTests;

public sealed class PackValidationTests
{
    private static GitHubPack Pack() =>
        new(new PackManifest {
                SchemaVersion = 1,
                Pack = new PackInfo { Id = "swagheim", Name = "Swagheim", Version = "0.1.0" },
                Game = new GameInfo { Id = "valheim", AppId = "892970" }
            },
            new PackLock { SchemaVersion = 1, PackId = "swagheim", PackVersion = "0.1.0" },
            "Scraxemas/scraxismodmanager", "examples/swagheim", "main");

    [Fact]
    public void EmptyPackIsValidForPreview() => PackValidation.Validate(Pack());

    [Fact]
    public void MismatchedVersionsAreRejected()
    {
        var pack = Pack();
        pack.Lock.PackVersion = "0.2.0";
        Assert.Throws<InvalidDataException>(() => PackValidation.Validate(pack));
    }

    [Fact]
    public void MissingDependenciesAreRejected()
    {
        var pack = Pack();
        pack.Lock.Packages.Add(new LockedPackage {
            Id = "Example-Mod", Version = "1.0.0",
            Sha256 = new string('a', 64),
            Url = "https://thunderstore.io/package/download/Example/Mod/1.0.0/",
            Dependencies = new List<string> { "Missing-Mod" }
        });
        Assert.Throws<InvalidDataException>(() => PackValidation.Validate(pack));
    }

    [Theory]
    [InlineData("https://evil.test/mod.zip", false)]
    [InlineData("http://thunderstore.io/mod.zip", false)]
    [InlineData("https://thunderstore.io/package/download/A/B/1/", true)]
    [InlineData("https://github.com/owner/repo/releases/download/v1/mod.zip", true)]
    public void DownloadHostAllowlist(string url, bool allowed) =>
        Assert.Equal(allowed, VerifiedDownloadCache.IsAllowed(new Uri(url)));
}
