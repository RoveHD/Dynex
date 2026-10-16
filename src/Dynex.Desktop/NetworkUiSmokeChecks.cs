using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

namespace Dynex.Desktop;

internal static class NetworkUiSmokeChecks
{
    internal static async Task RunAsync(MainWindow window, DynexRuntime runtime, string directory)
    {
        window.ShowSettings("network");
        var check = (CheckBox)window.FindName("AutoConnectCheck");
        var controls = (StackPanel)window.FindName("ConnectionModeControls");
        var peers = (TextBox)window.FindName("PeersBox");
        var router = (TextBox)window.FindName("RouterBox");
        var button = (Button)window.FindName("CheckConnectionButton");
        var status = (TextBlock)window.FindName("NetworkCheckStatusText");
        var saved = runtime.Settings;
        var originalRouter = router.Text;
        router.Text = "pending-router-draft";
        foreach (var enabled in new[] { false, true })
        {
            check.IsChecked = enabled;
            check.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            await Until(() => controls.IsEnabled);
            if (runtime.Settings.AutoConnectLan != enabled || check.IsChecked != enabled ||
                runtime.Settings != saved with { AutoConnectLan = enabled })
                throw new Exception("Automatik-Checkbox stimmt nicht mit gespeichertem Modus überein oder verändert andere Einstellungen.");
        }
        if (router.Text != "pending-router-draft") throw new Exception("Moduswechsel verwirft ungespeicherte Eingaben.");
        peers.Text = "10.0.2.2";
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => button.IsEnabled);
        if (runtime.Settings.DirectPeers != "10.0.2.2" || !status.Text.StartsWith("Geprüft um", StringComparison.Ordinal) ||
            runtime.Settings.RouterTarget != saved.RouterTarget || router.Text != "pending-router-draft")
            throw new Exception($"Verbindungsprüfung übernimmt direkte Adresse nicht oder speichert fremde Eingaben. Peer={runtime.Settings.DirectPeers}; Status={status.Text}; Router={runtime.Settings.RouterTarget}; Draft={router.Text}; Fehler={window.ErrorText.Text}");
        peers.Text = "1.1.1.1";
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => button.IsEnabled);
        if (!status.Text.StartsWith("Verbindungsprüfung fehlgeschlagen", StringComparison.Ordinal) || runtime.Settings.DirectPeers != "10.0.2.2")
            throw new Exception("Ungültige direkte Adresse wird nicht sichtbar und ohne Konfigurationsverlust abgewiesen.");
        peers.Text = "10.0.2.2";
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => button.IsEnabled);
        router.Text = originalRouter;
        foreach (var (width, height) in new[] { (900, 650), (1440, 940), (1920, 1080) })
        {
            window.Width = width; window.Height = height;
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var page = (FrameworkElement)window.FindName("SettingsPage");
            foreach (var name in new[] { "AutoConnectCheck", "PairingBox", "CheckConnectionButton", "PeersBox", "RouterBox", "ExternalBox", "SettingsSaveButton" })
            {
                var element = (FrameworkElement)window.FindName(name);
                var bounds = element.TransformToAncestor(page).TransformBounds(new Rect(element.RenderSize));
                if (!element.IsVisible || bounds.Bottom > page.ActualHeight + 1 || bounds.Right > page.ActualWidth + 1 || bounds.Left < -1)
                    throw new Exception($"Netzwerksteuerung {name} liegt außerhalb des Fensters bei {width}x{height}.");
                for (var parent = VisualTreeHelper.GetParent(element); parent is FrameworkElement ancestor && ancestor != page;
                    parent = VisualTreeHelper.GetParent(parent))
                {
                    if (!ancestor.ClipToBounds && ancestor is not ScrollContentPresenter) continue;
                    var visible = element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
                    if (visible.Bottom > ancestor.ActualHeight + 1 || visible.Right > ancestor.ActualWidth + 1)
                        throw new Exception($"Netzwerksteuerung {name} wird bei {width}x{height} von {ancestor.GetType().Name} abgeschnitten.");
                }
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, $"network-settings-{width}.png"));
            encoder.Save(stream);
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "network-ui-result.txt"),
            "PASS: actual checkbox clicks immediately persist the connection mode and retain other drafts; check saves remote address only; invalid address feedback preserves config; network controls fit900x650/1440x940/1920x1080. Isolated profile; no physical LAN/firewall mutation.\n");
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
