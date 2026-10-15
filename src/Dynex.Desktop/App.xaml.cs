using System.Windows;
using System.Diagnostics;
using System.IO;
using Dynex.Desktop.Updates;

namespace Dynex.Desktop;

public partial class App : System.Windows.Application
{
    private DynexRuntime? _runtime;
    private GitHubUpdateService? _updates;
    protected override async void OnStartup(StartupEventArgs e)
    {
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
            if (e.Args.Length > 0 && e.Args[0] is "--setup-prerequisites" or "--setup-machine" or "--remove-setup-firewall" or "--remove-machine-firewall"
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
                    () => _runtime.IsSpeedTestRunning || _runtime.IsSpeedTestWaiting || _runtime.Snapshot is not { } snapshot ||
                          snapshot.IsGaming || snapshot.GamingCount > 0 || !string.IsNullOrEmpty(snapshot.Game) ||
                          snapshot.Devices.Any(device => !string.IsNullOrEmpty(device.Game)));
                _updates.SetBetaChannel(_runtime.Settings.BetaUpdates);
                _updates.SetEnabled(_runtime.Settings.AutoUpdate, startBackground: false);
                if (_updates.RolledBackVersion is { } failed)
                    startupWarning = $"Das Update auf Version {failed} ist fehlgeschlagen. DYNEX hat die vorherige Version {DynexRuntime.AppVersion} wiederhergestellt; Einstellungen bleiben erhalten.";
                if (!e.Args.Contains("--skip-update-once") && !e.Args.Contains("--verify-live"))
                {
                    var installer = await _updates.TakeReadyInstallerAsync();
                    if (installer is not null && StartUpdateInstaller(installer, e.Args.Contains("--tray")))
                    {
                        // No network worker/helper has started. The installer waits
                        // for this process to exit and then reopens the application.
                        _updates.Dispose();
                        Shutdown();
                        return;
                    }
                }
            }
            await _runtime.StartAsync();
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            // The first-run guide is for real installations; isolated test profiles keep their fixed layout.
            var window = new MainWindow(_runtime, _updates, showSetupGuide: DesktopLifecycle.ProfilePath is null);
            if (startupWarning is not null) window.ErrorText.Text = startupWarning;
            MainWindow = window;
            window.Show();
            await DesktopLifecycle.AfterWindowAsync(window, _runtime, e.Args);
        }
        catch (Exception ex)
        {
            if (_runtime is not null)
            {
                try { await _runtime.StopAsync(); await _runtime.DisposeAsync(); }
                catch { /* startup error is the visible failure */ }
            }
            System.Windows.MessageBox.Show($"DYNEX konnte nicht gestartet werden.\n\n{ex.Message}", "DYNEX", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(DesktopLifecycle.ExitCode);
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
        _updates?.Dispose();
        DesktopLifecycle.Cleanup();
        base.OnExit(e);
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
