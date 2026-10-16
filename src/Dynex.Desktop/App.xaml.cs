using System.Windows;
using System.Diagnostics;
using System.IO;
using Dynex.Desktop.Updates;

namespace Dynex.Desktop;

public partial class App : System.Windows.Application
{
    private DynexRuntime? _runtime;
    private GitHubUpdateService? _updates;
    private UpdateInstallCoordinator? _updateInstall;
    private MainWindow? _updateWindow;
    protected override async void OnStartup(StartupEventArgs e)
    {
        var startupStep = "Windows-Startschutz und laufende DYNEX-Instanz prüfen";
        base.OnStartup(e);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { if (args.ExceptionObject is Exception error) CrashLog.Write(error, "AppDomain"); };
        DispatcherUnhandledException += (_, args) => CrashLog.Write(args.Exception, "UI");
        TaskScheduler.UnobservedTaskException += (_, args) => CrashLog.Write(args.Exception, "Task");
        try
        {
            if (e.Args.Length > 0 && e.Args[0] == "--self-test")
            {
                // Run by the updater right after copying files. A non-zero result makes
                // the installer restore the previous version instead of leaving a broken app.
                Shutdown(UpdateSelfTest.Run(e.Args.Skip(1).FirstOrDefault()));
                return;
            }
            if (e.Args.Length > 0 && e.Args[0] is "--setup-prerequisites" or "--setup-machine" or "--setup-machine-firewall" or "--remove-setup-firewall" or "--remove-machine-firewall"
                or "--uninstall-system" or "--uninstall-machine" or "--cleanup-removed-installation" or "--cleanup-machine" or "--remove-user-data")
            {
                // This process is launched by NSIS before the first normal app
                // start. Do not create mutexes, profiles or network workers.
                try { Shutdown(await InstallerPrerequisites.ExecuteAsync(e.Args)); }
                catch { Shutdown(1); } // NSIS reports a failed CLI instead of a successful normal-app exit.
                return;
            }
            if (!await DesktopLifecycle.InitializeAsync(e.Args))
            {
                Shutdown(DesktopLifecycle.ExitCode);
                return;
            }
            if (DesktopLifecycle.ProfilePath is null) Dynex.Native.ProcessEfficiency.UseBackgroundMode();
            startupStep = "Lokale Einstellungen und Geräteidentität laden";
            _runtime = new DynexRuntime(DesktopLifecycle.ProfilePath);
            string? startupWarning = DesktopLifecycle.ProfilePath is null && CrashLog.TakeUnseen() is not null
                ? "DYNEX wurde zuletzt unerwartet beendet. Ein lokaler Bericht liegt bei; „Diagnose exportieren“ enthält ihn." : null;
            if (CanUseInstalledUpdates())
            {
                try { _runtime.ApplyAutostartPreference(); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                { startupWarning = "Windows-Autostart konnte nicht eingerichtet werden: " + ex.Message; }
                _updates = new GitHubUpdateService(
                    Path.Combine(new SettingsStore().DirectoryPath, "updates"),
                    typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0),
                    () => _runtime.IsSpeedTestRunning);
                _updates.SetBetaChannel(_runtime.Settings.BetaUpdates);
                _updates.SetEnabled(_runtime.Settings.AutoUpdate, startBackground: false);
                if (_updates.RolledBackVersion is { } failed)
                    startupWarning = $"Das Update auf Version {failed} ist fehlgeschlagen. DYNEX hat die vorherige Version {DynexRuntime.AppVersion} wiederhergestellt; Einstellungen bleiben erhalten.";
            }
            startupStep = "Netzwerkdienste starten";
            await _runtime.StartAsync();
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            // The first-run guide is for real installations; isolated test profiles keep their fixed layout.
            startupStep = "Oberfläche öffnen";
            var window = new MainWindow(_runtime, _updates, showSetupGuide: DesktopLifecycle.ProfilePath is null);
            if (startupWarning is not null) window.ErrorText.Text = startupWarning;
            MainWindow = window;
            // A background update must not briefly activate a window over the game.
            if (!e.Args.Contains("--tray") || e.Args.Contains("--verify-live")) window.Show();
            var startupHandoff = true;
            if (_updates is not null && !e.Args.Contains("--verify-live"))
            {
                var updates = _updates;
                _updateWindow = window;
                _updateInstall = new UpdateInstallCoordinator(
                    () => _runtime.Settings.AutoUpdate && window.IsUpdateRestartSafe,
                    updates.TakeReadyInstallerAsync,
                    installer => window.RequestUpdateRestartAsync(() =>
                    {
                        // The channel or opt-in may have changed while verification or helper shutdown awaited.
                        if (!updates.CanStartInstaller(installer)) return false;
                        if (!StartUpdateInstaller(installer, !window.IsVisible || !window.IsActive ||
                            window.WindowState == WindowState.Minimized || startupHandoff && e.Args.Contains("--tray")))
                        {
                            updates.ReportInstallerLaunchFailure("Windows konnte den Update-Installer nicht starten.");
                            return false;
                        }
                        updates.ConfirmInstallerStarted(installer);
                        return true;
                    }),
                    updates.ReportInstallerLaunchFailure);
                // Remember an unconsumed cache before ready/safety events can run.
                // A different future release remains eligible for automatic installation.
                if (e.Args.Contains("--skip-update-once"))
                    await _updateInstall.SkipReadyInstallerAsync();
                updates.Changed += QueueReadyUpdateInstall;
                window.UpdateRestartSafetyChanged += QueueReadyUpdateInstall;
                if (!e.Args.Contains("--skip-update-once"))
                    await _updateInstall.TryInstallAsync();
            }
            if (Dispatcher.HasShutdownStarted) return;
            await DesktopLifecycle.AfterWindowAsync(window, _runtime, e.Args);
            startupHandoff = false;
        }
        catch (Exception ex)
        {
            var unattended = e.Args.Any(arg => arg is "--smoke-test" or "--test-instance" or "--verify-live");
            if (unattended) DesktopLifecycle.ReportStartupFailure(e.Args, ex, startupStep);
            else CrashLog.Write(ex, "Start: " + startupStep);
            if (_runtime is not null)
            {
                try { await _runtime.StopAsync(); await _runtime.DisposeAsync(); }
                catch { /* startup error is the visible failure */ }
            }
            if (!unattended)
                System.Windows.MessageBox.Show($"DYNEX konnte nicht gestartet werden.\n\nSchritt: {startupStep}\n{ex.Message}\n\nLokale Fehlerberichte: {CrashLog.DirectoryPath}", "DYNEX", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(DesktopLifecycle.ExitCode == 0 ? 1 : DesktopLifecycle.ExitCode);
        }
    }

    private async void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume || _runtime is null) return;
        try
        {
            // Give Windows a moment to bring the adapter back and classify the network profile.
            await Task.Delay(TimeSpan.FromSeconds(3));
            await _runtime.ReconnectNetworkAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* The next heartbeat reports the network state. */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _updateInstall?.Dispose();
        if (_updates is not null) _updates.Changed -= QueueReadyUpdateInstall;
        if (_updateWindow is not null) _updateWindow.UpdateRestartSafetyChanged -= QueueReadyUpdateInstall;
        _updates?.Dispose();
        DesktopLifecycle.Cleanup();
        base.OnExit(e);
    }

    private void QueueReadyUpdateInstall()
    {
        if (_updates?.Status.UpdateReady != true || _updateInstall is null || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_updateInstall is not null) await _updateInstall.TryInstallAsync();
        }));
    }

    internal void RetryReadyUpdateInstall()
    {
        _updateInstall?.AllowRetry();
        QueueReadyUpdateInstall();
    }

    private static bool CanUseInstalledUpdates()
    {
        if (DesktopLifecycle.ProfilePath is not null) return false;
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DYNEX");
        return string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), installed, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(installed, "Uninstall.exe"));
    }

    private static bool StartUpdateInstaller(string path, bool tray)
    {
        try
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("/S");
            start.ArgumentList.Add($"/DYNEXUPDATE={Environment.ProcessId}");
            if (tray) start.ArgumentList.Add("/DYNEXTRAY=1");
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // A failed update launch must not prevent normal gaming protection.
            return false;
        }
    }
}
