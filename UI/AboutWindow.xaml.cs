using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Services;

namespace HyperVManagerTray.UI;

public sealed partial class AboutWindow : Window
{
    private const string GitHubUrl = "https://github.com/0z00z0/HyperVManagerTray";
    private const string BmacUrl   = "https://buymeacoffee.com/ezpl";
    private const string AppName   = "Hyper-V Manager Tray";

    private readonly UpdateChecker _updateChecker;

    internal AboutWindow(UpdateChecker updateChecker)
    {
        _updateChecker = updateChecker;
        InitializeComponent();
        ConfigureChrome();

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = ver is not null ? $"v{ver.ToString(3)}" : "v—";

        CloseBtn.Click  += (_, _) => Close();
        GitHubBtn.Click += (_, _) => Open(GitHubUrl);
        UpdateBtn.Click += (_, _) => _ = CheckForUpdatesAsync();
        BmacBtn.Click   += (_, _) => Open(BmacUrl);
    }

    private void ConfigureChrome()
    {
        AppWindow.IsShownInSwitchers = false;

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        Root.Width = 320;
        Root.Measure(new Size(320, double.PositiveInfinity));

        var (work, scale) = NativeMethods.GetCursorMonitorMetrics();
        int w = (int)Math.Round(320 * scale);
        int h = (int)Math.Round((Root.DesiredSize.Height > 0 ? Root.DesiredSize.Height : 270) * scale);
        AppWindow.Resize(new SizeInt32(w, h));
        AppWindow.Move(new PointInt32(
            work.Left + (work.Right  - work.Left - w) / 2,
            work.Top  + (work.Bottom - work.Top  - h) / 2));
    }

    private async Task CheckForUpdatesAsync()
    {
        var result  = await _updateChecker.CheckAsync().ConfigureAwait(false);
        var running = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

        if (result.UpdateAvailable)
        {
            bool canDownload = !string.IsNullOrEmpty(result.InstallerUrl);
            var action = NativeMethods.ShowUpdateDialog(
                result.LatestVersion, running,
                result.ReleaseNotes,  AppName,
                canDownload);

            switch (action)
            {
                case NativeMethods.UpdateAction.Update:
                    NativeMethods.Info(
                        $"Downloading v{result.LatestVersion}...\n\nThe installer will launch automatically when ready.",
                        AppName);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var path = await _updateChecker
                                .DownloadInstallerAsync(result.InstallerUrl)
                                .ConfigureAwait(false);
                            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        }
                        catch (Exception ex)
                        {
                            NativeMethods.Warn(
                                $"Download failed:\n{ex.Message}\n\nTry updating from the releases page.",
                                AppName);
                            if (!string.IsNullOrEmpty(result.ReleasePageUrl))
                                Process.Start(new ProcessStartInfo(result.ReleasePageUrl) { UseShellExecute = true });
                        }
                    });
                    break;

                case NativeMethods.UpdateAction.ShowReleases:
                    if (!string.IsNullOrEmpty(result.ReleasePageUrl))
                        Process.Start(new ProcessStartInfo(result.ReleasePageUrl) { UseShellExecute = true });
                    break;
            }
        }
        else if (result.LatestVersion == "none")
            NativeMethods.Info("No releases have been published yet.", AppName);
        else if (!string.IsNullOrEmpty(result.LatestVersion))
            NativeMethods.Info($"You're on the latest version ({running}).", AppName);
        else
            NativeMethods.Warn("Could not check for updates. Check your internet connection.", AppName);
    }

    private static void Open(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
