using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using SwagMod.Domain;
using SwagMod.Games.Valheim;
using SwagMod.Infrastructure;
using Xunit;

namespace SwagMod.UnitTests;

public sealed class ValheimDeploymentTests
{
    [Theory]
    [InlineData(DeploymentMode.Isolated)]
    [InlineData(DeploymentMode.Direct)]
    public async Task CanInstallLoaderInCleanFakeGame(DeploymentMode mode)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewFixture(out var game);
        try
        {
            var zip = LoaderZip();
            using var http = new HttpClient(new FakeZipHandler(zip));
            var deploy = new ValheimDeployment(
                new VerifiedDownloadCache(http, Path.Combine(root, "cache")),
                Path.Combine(root, "state"));
            var pack = MakePack(zip);
            var plan = deploy.Plan(pack, game, mode);
            await deploy.InstallAsync(pack, plan);
            Assert.True(File.Exists(Path.Combine(game, "BepInEx", "core", "core.dll")));
            Assert.True(File.Exists(Path.Combine(game, "winhttp.dll")));
            Assert.True(File.Exists(Path.Combine(game, "doorstop_config.ini")));
            Assert.True(File.Exists(Path.Combine(game, "doorstop_libs", "helper.dll")));
            Assert.Equal(mode, deploy.GetState(game)!.Mode);
            Assert.Equal(mode == DeploymentMode.Isolated,
                new DirectoryInfo(Path.Combine(game, "BepInEx")).Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        finally { CleanFixture(root, game); }
    }

    [Fact]
    public async Task BadHashNeverTouchesGame()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewFixture(out var game);
        try
        {
            var zip = LoaderZip();
            using var http = new HttpClient(new FakeZipHandler(zip));
            var pack = MakePack(zip);
            pack.Lock.Packages[0].Sha256 = new string('a', 64);
            var deploy = new ValheimDeployment(
                new VerifiedDownloadCache(http, Path.Combine(root, "cache")),
                Path.Combine(root, "state"));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                deploy.InstallAsync(pack, deploy.Plan(pack, game, DeploymentMode.Direct)));
            Assert.False(Directory.Exists(Path.Combine(game, "BepInEx")));
            Assert.False(File.Exists(Path.Combine(game, "winhttp.dll")));
        }
        finally { CleanFixture(root, game); }
    }

    [Fact]
    public async Task UnmanagedBepInExIsNeverOverwritten()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewFixture(out var game);
        try
        {
            Directory.CreateDirectory(Path.Combine(game, "BepInEx", "plugins"));
            var existing = Path.Combine(game, "BepInEx", "plugins", "someone-elses-mod.dll");
            File.WriteAllText(existing, "preserve me");
            var zip = LoaderZip();
            using var http = new HttpClient(new FakeZipHandler(zip));
            var pack = MakePack(zip);
            var deploy = new ValheimDeployment(
                new VerifiedDownloadCache(http, Path.Combine(root, "cache")),
                Path.Combine(root, "state"));
            await Assert.ThrowsAsync<IOException>(() =>
                deploy.InstallAsync(pack, deploy.Plan(pack, game, DeploymentMode.Isolated)));
            Assert.Equal("preserve me", File.ReadAllText(existing));
        }
        finally { CleanFixture(root, game); }
    }

    private static string NewFixture(out string game)
    {
        var root = Path.Combine(Path.GetTempPath(), "SwagMod Tests " + Guid.NewGuid().ToString("N"));
        game = Path.Combine(root, "Steam Library", "Valheim");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "valheim.exe"), "fake test executable");
        return root;
    }

    private static void CleanFixture(string root, string game)
    {
        // Remove the junction itself first, never recursively follow it.
        var linked = Path.Combine(game, "BepInEx");
        if (Directory.Exists(linked) &&
            new DirectoryInfo(linked).Attributes.HasFlag(FileAttributes.ReparsePoint))
            Directory.Delete(linked);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static byte[] LoaderZip()
    {
        using var data = new MemoryStream();
        using (var archive = new ZipArchive(data, ZipArchiveMode.Create, true))
        {
            Add("BepInExPack_Valheim/BepInEx/core/core.dll", "fake-core");
            Add("BepInExPack_Valheim/winhttp.dll", "fake-doorstop");
            Add("BepInExPack_Valheim/doorstop_config.ini", "enabled=true");
            Add("BepInExPack_Valheim/doorstop_libs/helper.dll", "fake-helper");
            void Add(string path, string value)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(value);
            }
        }
        return data.ToArray();
    }

    private static GitHubPack MakePack(byte[] zip)
    {
        var manifest = new PackManifest {
            SchemaVersion = 1,
            Pack = new PackInfo { Id = "swagheim", Name = "Swagheim", Version = "0.1.0" },
            Game = new GameInfo { Id = "valheim", AppId = "892970" }
        };
        var lockfile = new PackLock { SchemaVersion = 1, PackId = "swagheim", PackVersion = "0.1.0" };
        lockfile.Packages.Add(new LockedPackage {
            Id = "denikson-BepInExPack_Valheim",
            Kind = "loader",
            Version = "test",
            Url = "https://thunderstore.io/package/download/denikson/BepInExPack_Valheim/test/",
            Sha256 = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant()
        });
        return new GitHubPack(manifest, lockfile, "example/test", "", "main");
    }

    private sealed class FakeZipHandler(byte[] zip) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new ByteArrayContent(zip)
            });
    }
}
