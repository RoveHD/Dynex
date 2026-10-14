using System.Windows;

namespace Dynex.Desktop;

public partial class App : System.Windows.Application
{
    private DynexRuntime? _runtime;
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
            await _runtime.StartAsync();
            var window = new MainWindow(_runtime);
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
        DesktopLifecycle.Cleanup();
        base.OnExit(e);
    }
}
