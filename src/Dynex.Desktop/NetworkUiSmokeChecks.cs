using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Button = System.Windows.Controls.Button;

namespace Dynex.Desktop;

internal static class NetworkUiSmokeChecks
{
    internal static async Task RunAsync(MainWindow window, DynexRuntime runtime, string directory)
    {
        if (DesktopLifecycle.ProfilePath is null)
            throw new InvalidOperationException("Netzwerk-UI-Checks benötigen ein isoliertes Profil.");
        var original = runtime.Settings;
        var pairing = runtime.ExportPairingCode();
        var reload = typeof(MainWindow).GetMethod("LoadSettings", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var render = typeof(MainWindow).GetMethod("RenderSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var gateField = typeof(DynexRuntime).GetField("networkCheckGate", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var gate = (SemaphoreSlim)gateField.GetValue(runtime)!;
        window.ShowSettings("network"); window.UpdateLayout();
        if (!Named<FrameworkElement>(window, "DevicesPage").IsVisible || Named<FrameworkElement>(window, "SettingsPage").IsVisible)
            throw new Exception("Der bisherige Netzwerklink muss zur Geräteübersicht führen.");
        foreach (var name in new[] { "AutoConnectCheck", "ConnectionModeControls", "PairingBox", "PeersBox", "RouterBox", "ExternalBox", "NetworkSettingsSection", "NetworkHelpButton" })
            if (window.FindName(name) is not null) throw new Exception("Entfernte Netzwerksteuerung existiert weiter: " + name);
        var button = Named<Button>(window, "CheckConnectionButton");
        var enable = Named<Button>(window, "EnableAutomaticDiscoveryButton");
        var status = Named<TextBlock>(window, "NetworkCheckStatusText");
        if (status.Visibility != Visibility.Collapsed || !Equals(button.Content, "Gerätesuche aktualisieren"))
            throw new Exception("Gerätesuche zeigt ohne ausdrückliche Aktion technische Diagnose oder eine falsche Beschriftung.");
        if (enable.Visibility != (original.AutoConnectLan ? Visibility.Collapsed : Visibility.Visible))
            throw new Exception("Automatische Gerätesuche wird nicht nur bei bestehenden manuellen Gruppen angeboten.");

        try
        {
            var before = runtime.Snapshot?.History.LastOrDefault()?.Time;
            await gate.WaitAsync();
            try
            {
                Click(button);
                if (button.IsEnabled || !status.IsVisible || string.IsNullOrWhiteSpace(status.Text))
                    throw new Exception("Gerätesuche zeigt keinen laufenden Zustand oder erlaubt doppelte Klicks.");
                var busy = status.Text;
                Click(button); // Also exercise the handler's re-entry guard.
                if (status.Text != busy) throw new Exception("Ein zweiter Klick startet eine weitere Gerätesuche.");
                window.ShowOverview(); window.ShowDevices(); window.UpdateLayout();
                if (button.IsEnabled) throw new Exception("Navigation hebt die laufende Gerätesuche auf.");
            }
            finally { gate.Release(); }
            await Until(() => button.IsEnabled);
            if (!status.Text.StartsWith("Suche abgeschlossen.", StringComparison.Ordinal) ||
                before is not null && runtime.Snapshot!.History.Last().Time <= before)
                throw new Exception("Gerätesuche liefert keinen frischen Snapshot und sichtbaren isolierten Prüfbericht: " + status.Text);
            var completed = status.Text;
            render.Invoke(window, [runtime.Snapshot!]);
            if (status.Text != completed) throw new Exception("Laufende Messungen überschreiben das explizite Suchergebnis.");
            AssertUnchanged(runtime, original, pairing);

            // Exercise the actual failure handler without physical probes. Restore the isolated runtime immediately.
            using (var failedGate = new SemaphoreSlim(1, 1))
            {
                failedGate.Dispose();
                gateField.SetValue(runtime, failedGate);
                try
                {
                    Click(button);
                    await Until(() => button.IsEnabled);
                    if (!status.Text.StartsWith("Gerätesuche fehlgeschlagen", StringComparison.Ordinal) || !status.IsVisible)
                        throw new Exception("Eine fehlgeschlagene Gerätesuche stellt Bedienung und sichtbare Fehlermeldung nicht wieder her.");
                }
                finally { gateField.SetValue(runtime, gate); }
            }
            Click(button);
            await Until(() => button.IsEnabled);
            if (!status.Text.StartsWith("Suche abgeschlossen.", StringComparison.Ordinal))
                throw new Exception("Eine erneute Gerätesuche erholt sich nicht vom vorigen Fehler.");
            AssertUnchanged(runtime, original, pairing);

            // Navigation and refresh must not silently widen an existing private group's trust.
            var manual = original with { AutoConnectLan = false, DirectPeers = "10.0.2.2" };
            await runtime.SaveSettingsAsync(manual);
            reload.Invoke(window, [runtime.Settings]);
            render.Invoke(window, [runtime.Snapshot!]);
            window.ShowSettings("general"); window.ShowSettings("network"); window.UpdateLayout();
            Click(button); await Until(() => button.IsEnabled);
            AssertUnchanged(runtime, manual, pairing);
            if (!enable.IsVisible || !enable.IsEnabled)
                throw new Exception("Eine bestehende manuelle Gruppe bietet keinen ausdrücklichen Wechsel zur automatischen Suche an.");
            Click(enable);
            await Until(() => runtime.Settings.AutoConnectLan && enable.Visibility == Visibility.Collapsed);
            AssertUnchanged(runtime, manual with { AutoConnectLan = true }, pairing);

            foreach (var (width, height) in new[] { (900, 650), (1000, 720), (1366, 768), (1440, 940), (1920, 650), (1920, 1080), (2560, 1440) })
            {
                window.Width = width; window.Height = height;
                window.ShowDevices(); window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var page = Named<FrameworkElement>(window, "DevicesPage");
                foreach (var name in new[] { "CheckConnectionButton", "NetworkCheckStatusText", "DeviceGrid", "DeviceCount", "CoordinationText" })
                {
                    var element = Named<FrameworkElement>(window, name);
                    var bounds = element.TransformToAncestor(page).TransformBounds(new Rect(element.RenderSize));
                    if (!element.IsVisible || bounds.Bottom > page.ActualHeight + 1 || bounds.Right > page.ActualWidth + 1 || bounds.Left < -1 || bounds.Top < -1)
                        throw new Exception($"Gerätesteuerung {name} liegt außerhalb des Fensters bei {width}x{height}.");
                }
                Capture(window, Path.Combine(directory, $"network-devices-{width}x{height}.png"));
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "network-ui-result.txt"),
                "PASS: legacy network navigation routes to Devices; expert controls absent; real discovery clicks show busy, fresh completion, failure and recovery; re-entry prevented; settings and private pairing unchanged; legacy manual group retained until explicit AutoLAN opt-in preserving keys and direct addresses; rendered at seven sizes. Isolated profile; no physical LAN/firewall mutation.\n");
        }
        finally
        {
            gateField.SetValue(runtime, gate);
            await runtime.SaveSettingsAsync(original);
            reload.Invoke(window, [runtime.Settings]);
            window.ShowOverview();
        }
    }

    private static void AssertUnchanged(DynexRuntime runtime, UserSettings expected, string pairing)
    {
        if (runtime.Settings != expected || new SettingsStore(DesktopLifecycle.ProfilePath).Load() != expected || runtime.ExportPairingCode() != pairing)
            throw new Exception("Die Gerätesuche verändert gespeicherte Einstellungen oder den privaten Gruppenschlüssel.");
    }
    private static T Named<T>(MainWindow window, string name) where T : FrameworkElement => window.FindName(name) as T
        ?? throw new Exception("Steuerelement fehlt: " + name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Capture(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static async Task Until(Func<bool> condition)
    {
        var time = Stopwatch.StartNew();
        while (!condition())
        {
            if (time.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Netzwerk-UI-Aktion wurde nicht abgeschlossen.");
            await Task.Delay(25);
        }
    }
}
