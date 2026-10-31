using SwagMod.Domain;
using SwagMod.Games.Valheim;
using SwagMod.Infrastructure;

namespace SwagMod.Application;

public sealed class SwagModCoordinator : IDisposable
{
    private readonly HttpClient manifestHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient downloadHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMinutes(15) };
    private readonly string storageRoot;
    private readonly GitHubPackClient manifests;
    private readonly ValheimDeployment deployment;

    public SwagModCoordinator()
    {
        storageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwagMod");
        manifests = new GitHubPackClient(manifestHttp);
        deployment = new ValheimDeployment(
            new VerifiedDownloadCache(downloadHttp, Path.Combine(storageRoot, "cache", "sha256")), storageRoot);
    }

    public IReadOnlyList<GameInstallation> FindValheim() => ValheimDiscovery.Discover();
    public bool IsValidValheim(string path) => ValheimDiscovery.IsValid(path);

    public Task<GitHubPack> LoadPackAsync(string repository, string folder, string gitRef,
        CancellationToken ct = default) => manifests.LoadAsync(repository, folder, gitRef, ct);

    public InstallPlan Plan(GitHubPack pack, string game, DeploymentMode mode) =>
        deployment.Plan(pack, game, mode);

    public InstalledState? Installed(string game) => deployment.GetState(game);

    public Task InstallAsync(GitHubPack pack, InstallPlan plan,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default) =>
        deployment.InstallAsync(pack, plan, progress, ct);

    public void LaunchValheim(string game)
    {
        if (!ValheimDiscovery.IsValid(game)) throw new DirectoryNotFoundException("Valheim not found.");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
            FileName = "steam://rungameid/892970", UseShellExecute = true
        });
    }

    public void Dispose() { manifestHttp.Dispose(); downloadHttp.Dispose(); }
}
