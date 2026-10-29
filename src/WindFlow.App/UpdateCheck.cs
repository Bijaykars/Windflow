using Avalonia.Interactivity;
using Avalonia.Threading;
using Velopack;
using Velopack.Sources;

namespace WindFlow.App;

/// <summary>Updates for an installed copy (Velopack): GitHub Releases is the feed. Never runs in an automated run, never
/// when the app is a dev build or the portable zip (UpdateManager.IsInstalled is false), and a failure (offline) is only logged.
/// WINDFLOW_UPDATE_SOURCE=&lt;folder&gt; replaces GitHub with a local feed folder (testing); --update-now checks, downloads and
/// applies once without asking (testing, installed copy only).</summary>
public partial class MainWindow
{
    const string UpdateRepoUrl = "https://github.com/Bijaykars/Windflow";
    static readonly TimeSpan UpdateCheckTimeout = TimeSpan.FromSeconds(15);
    UpdateManager? updater;
    UpdateInfo? pendingUpdate;
    bool updateBusy;

    UpdateManager Updater => updater ??= new UpdateManager(
        Environment.GetEnvironmentVariable("WINDFLOW_UPDATE_SOURCE") is { Length: > 0 } dir && Directory.Exists(dir)
            ? new SimpleFileSource(new DirectoryInfo(dir))
            : new GithubSource(UpdateRepoUrl, null, false));

    /// <summary>Window open: log the state, then check quietly if allowed.</summary>
    void InitUpdates()
    {
        UpdatesToggle.IsChecked = settings.CheckUpdates;
        if (Automated) return;
        bool installed = Updater.IsInstalled;
        Log.Info(installed ? $"version {Updater.CurrentVersion} (installed)" : "not installed, update check skipped");
        if (!installed) return;
        if (Options.UpdateNow) { _ = RunUpdateNow(); return; }
        if (settings.CheckUpdates) _ = CheckForUpdate(userAsked: false);
    }

    /// <summary>One check. Quiet runs say nothing on "latest" or on failure; a user-asked run reports both in the status line.</summary>
    async Task CheckForUpdate(bool userAsked)
    {
        if (updateBusy) return;
        if (!Updater.IsInstalled)
        {
            if (userAsked) StatusText.Text = "Updates work in the installed version (use the Setup from GitHub Releases)";
            return;
        }
        updateBusy = true;
        try
        {
            var info = await Updater.CheckForUpdatesAsync().WaitAsync(UpdateCheckTimeout);
            if (info == null)
            {
                Log.Info($"update check: {Updater.CurrentVersion} is the latest");
                if (userAsked) StatusText.Text = $"You have the latest version ({Updater.CurrentVersion})";
                return;
            }
            Log.Info($"update check: {info.TargetFullRelease.Version} is available (running {Updater.CurrentVersion})");
            pendingUpdate = info;
            UpdateText.Text = $"WindFlow {info.TargetFullRelease.Version} is available";
            UpdateButton.Content = "Update and restart";
            UpdateButton.IsEnabled = true;
            UpdateBar.IsVisible = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"update check failed: {ex.GetType().Name}: {ex.Message}");
            if (userAsked) StatusText.Text = "Could not check for updates (offline?)";
        }
        finally { updateBusy = false; }
    }

    async Task<bool> DownloadUpdate(UpdateInfo info)
    {
        try
        {
            await Updater.DownloadUpdatesAsync(info, pct => Dispatcher.UIThread.Post(() => UpdateText.Text = $"Downloading WindFlow {info.TargetFullRelease.Version}... {pct} %"));
            Log.Info($"update {info.TargetFullRelease.Version} downloaded");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"update download failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    async Task RunUpdateNow()
    {
        try
        {
            var info = await Updater.CheckForUpdatesAsync().WaitAsync(UpdateCheckTimeout);
            if (info == null) { Log.Info($"update-now: {Updater.CurrentVersion} is the latest"); return; }
            Log.Info($"update-now: {info.TargetFullRelease.Version} found, applying");
            if (!await DownloadUpdate(info)) return;
            Updater.ApplyUpdatesAndRestart(info.TargetFullRelease);
        }
        catch (Exception ex) { Log.Warn($"update-now failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    async void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (pendingUpdate is not { } info || updateBusy) return;
        updateBusy = true;
        UpdateButton.IsEnabled = false;
        try
        {
            if (!await DownloadUpdate(info))
            {
                UpdateText.Text = "Could not download the update";
                UpdateButton.Content = "Try again";
                return;
            }
            if (!await ConfirmDiscard("restarting to update"))
            {
                UpdateText.Text = $"WindFlow {info.TargetFullRelease.Version} is ready";
                UpdateButton.Content = "Update and restart";
                return;
            }
            Log.Info($"applying update {info.TargetFullRelease.Version} and restarting");
            closeConfirmed = true;
            Updater.ApplyUpdatesAndRestart(info.TargetFullRelease);
        }
        catch (Exception ex)
        {
            Log.Warn($"update apply failed: {ex.GetType().Name}: {ex.Message}");
            UpdateText.Text = "Could not apply the update";
        }
        finally { updateBusy = false; UpdateButton.IsEnabled = true; }
    }

    void OnUpdateDismiss(object? sender, RoutedEventArgs e) => UpdateBar.IsVisible = false;

    void OnHelpUpdate(object? sender, RoutedEventArgs e) => _ = CheckForUpdate(userAsked: true);

    void OnUpdatesToggle(object? sender, RoutedEventArgs e)
    {
        settings.CheckUpdates = UpdatesToggle.IsChecked == true;
        settings.Save();
    }
}
