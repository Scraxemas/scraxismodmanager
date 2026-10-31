using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SwagMod.Application;
using SwagMod.Domain;

namespace SwagMod.Desktop;

public sealed class MainWindow : Window
{
    private readonly SwagModCoordinator manager = new();
    private readonly TextBox repository = new() { Text = "Scraxemas/scraxismodmanager", Watermark = "owner/repo" };
    private readonly TextBox folder = new() { Text = "examples/swagheim", Watermark = "Optional pack folder" };
    private readonly TextBox branch = new() { Text = "main", Watermark = "Branch or tag" };
    private readonly TextBox gamePath = new() { Watermark = @"C:\...\Valheim" };
    private readonly ComboBox deploymentMode = new() { ItemsSource = new[] { "Isolated (junction)", "Direct injection" }, SelectedIndex = 0 };
    private readonly TextBlock status = new() { Text = "Ready. Find Valheim, then load a GitHub pack.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock packDetails = new() { Text = "No pack loaded", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock installDetails = new() { Text = "No active installation detected", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox activity = new() { Text = "SwagMod Alpha 0.1 started.\n", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 180 };
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 100, Value = 0, Height = 8 };
    private readonly Button installButton = new() { Content = "Install / Update", IsEnabled = false };
    private GitHubPack? activePack;
    private bool busy;

    public MainWindow()
    {
        Title = "SwagMod Manager — Alpha 0.1";
        Width = 1020;
        Height = 780;
        MinWidth = 720;
        MinHeight = 620;
        Background = new SolidColorBrush(Color.Parse("#11151F"));

        var content = new StackPanel { Spacing = 18, Margin = new Thickness(26) };
        content.Children.Add(new TextBlock {
            Text = "SWAGMOD", FontSize = 30, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#A4B7FF"))
        });
        content.Children.Add(new TextBlock {
            Text = "Valheim modpack deployment · GitHub-controlled · Hybrid profiles",
            Foreground = Brushes.LightGray, FontSize = 14
        });

        var game = Card("1  GAME INSTALLATION");
        game.Children.Add(Label("Valheim directory (Steam install, editable)"));
        game.Children.Add(gamePath);
        var detect = ActionButton("Detect Valheim");
        detect.Click += (_, _) => DetectGame();
        var launch = ActionButton("Launch through Steam");
        launch.Click += (_, _) => RunAction(() => { manager.LaunchValheim(gamePath.Text ?? ""); return Task.CompletedTask; });
        game.Children.Add(Horizontal(detect, launch));
        game.Children.Add(installDetails);
        content.Children.Add(game);

        var pack = Card("2  GITHUB PACK");
        pack.Children.Add(Label("Repository (owner/repo or github.com URL)"));
        pack.Children.Add(repository);
        pack.Children.Add(Label("Pack folder within repository"));
        pack.Children.Add(folder);
        pack.Children.Add(Label("Branch or tag"));
        pack.Children.Add(branch);
        var load = ActionButton("Load manifest + lockfile");
        load.Click += async (_, _) => await LoadPackAsync();
        pack.Children.Add(load);
        pack.Children.Add(packDetails);
        content.Children.Add(pack);

        var install = Card("3  DEPLOYMENT");
        install.Children.Add(Label("Deployment mode"));
        install.Children.Add(deploymentMode);
        var plan = ActionButton("Preview installation plan");
        plan.Click += (_, _) => RunAction(() =>
        {
            if (activePack is null) throw new InvalidOperationException("Load a pack first.");
            var preview = manager.Plan(activePack, gamePath.Text ?? "", SelectedMode());
            Write(preview.Summary);
            status.Text = preview.Summary;
            return Task.CompletedTask;
        });
        installButton.Click += async (_, _) => await InstallAsync();
        install.Children.Add(Horizontal(plan, installButton));
        install.Children.Add(progress);
        install.Children.Add(status);
        content.Children.Add(install);

        var logs = Card("ACTIVITY");
        logs.Children.Add(activity);
        content.Children.Add(logs);

        Content = new ScrollViewer {
            Content = content,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        Closed += (_, _) => manager.Dispose();
        DetectGame();
    }

    private void DetectGame()
    {
        try
        {
            var games = manager.FindValheim();
            if (games.Count == 0) { Write("Steam Valheim not detected automatically; you can enter the path."); return; }
            gamePath.Text = games[0].Directory;
            RefreshInstalled();
            Write("Detected Valheim at " + games[0].Directory);
        }
        catch (Exception ex) { Write("Game detection: " + ex.Message); }
    }

    private void RefreshInstalled()
    {
        try
        {
            var installed = manager.Installed(gamePath.Text ?? "");
            installDetails.Text = installed is null
                ? "No SwagMod-managed installation here"
                : $"Active: {installed.PackId} {installed.PackVersion} — {installed.Mode}";
        }
        catch (Exception ex) { installDetails.Text = "Installed state unavailable: " + ex.Message; }
    }

    private async Task LoadPackAsync()
    {
        await RunAction(async () =>
        {
            activePack = await manager.LoadPackAsync(repository.Text ?? "", folder.Text ?? "",
                branch.Text ?? "main");
            packDetails.Text = $"{activePack.Manifest.Pack.Name} v{activePack.Manifest.Pack.Version}\n" +
                $"{activePack.Lock.Packages.Count} pinned packages\n" +
                string.Join("\n", activePack.Lock.Packages.Select(x => $"• {x.Id}  {x.Version}  ({x.Kind})"));
            installButton.IsEnabled = activePack.Lock.Packages.Any(x => x.Kind == "loader");
            Write("Loaded pack " + activePack.Manifest.Pack.Id + "@" + activePack.Manifest.Pack.Version);
            if (!installButton.IsEnabled) Write("This example pack has no pinned loader yet. Installation is disabled.");
        });
    }

    private async Task InstallAsync()
    {
        await RunAction(async () =>
        {
            if (activePack is null) throw new InvalidOperationException("Load a pack first.");
            var plan = manager.Plan(activePack, gamePath.Text ?? "", SelectedMode());
            Write("Beginning deployment: " + plan.Summary);
            var reporter = new Progress<InstallProgress>(p =>
                Dispatcher.UIThread.Post(() => {
                    status.Text = p.Phase + ": " + p.Detail;
                    progress.Value = Math.Clamp(p.Fraction * 100, 0, 100);
                    Write(p.Phase + ": " + p.Detail);
                }));
            await manager.InstallAsync(activePack, plan, reporter);
            RefreshInstalled();
            Write("Deployment complete.");
        });
    }

    private DeploymentMode SelectedMode() =>
        deploymentMode.SelectedIndex == 1 ? DeploymentMode.Direct : DeploymentMode.Isolated;

    private async Task RunAction(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        try { await action(); }
        catch (Exception ex) { status.Text = "Error: " + ex.Message; Write("ERROR: " + ex); }
        finally { busy = false; }
    }

    private void Write(string message)
    {
        activity.Text += "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + "\n";
        activity.CaretIndex = activity.Text?.Length ?? 0;
    }

    private static TextBlock Label(string text) =>
        new() { Text = text, FontSize = 12, Foreground = Brushes.LightGray };

    private static Button ActionButton(string text) =>
        new() { Content = text, Padding = new Thickness(14, 9) };

    private static StackPanel Horizontal(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var control in controls) row.Children.Add(control);
        return row;
    }

    private static StackPanel Card(string title)
    {
        var card = new StackPanel {
            Spacing = 10, Margin = new Thickness(0, 0, 0, 3)
        };
        card.Children.Add(new TextBlock {
            Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#A4B7FF"))
        });
        return card;
    }
}
