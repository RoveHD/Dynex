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
        try
        {
            if (!await DesktopLifecycle.InitializeAsync(e.Args))
            {
                Shutdown(DesktopLifecycle.ExitCode);
                return;
            }
            _runtime = new DynexRuntime(DesktopLifecycle.ProfilePath);
            string? startupWarning = null;
            if (CanUseInstalledUpdates())
            {
                try { _runtime.ApplyAutostartPreference(); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                { startupWarning = "Windows-Autostart konnte nicht eingerichtet werden: " + ex.Message; }
                _updates = new GitHubUpdateService(
                    Path.Combine(new SettingsStore().DirectoryPath, "updates"),
                    typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0),
                    () => _runtime.IsSpeedTestRunning || _runtime.Snapshot is not { } snapshot ||
                          snapshot.IsGaming || snapshot.GamingCount > 0);
                _updates.SetEnabled(_runtime.Settings.AutoUpdate, startBackground: false);
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
            var window = new MainWindow(_runtime, _updates);
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

    protected override void OnExit(ExitEventArgs e)
    {
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
