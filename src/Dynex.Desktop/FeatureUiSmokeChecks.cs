using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dynex.Core;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ListView = System.Windows.Controls.ListView;
using TextBox = System.Windows.Controls.TextBox;

namespace Dynex.Desktop;

internal static class FeatureUiSmokeChecks
{
    private const string RuleName = "featureuismoke";
    internal static async Task RunAsync(MainWindow owner, DynexRuntime runtime, string directory)
    {
        if (DesktopLifecycle.ProfilePath is null || runtime.Settings.DriverEnabled)
            throw new InvalidOperationException("Feature-UI-Checks benötigen ein isoliertes Profil mit ausgeschaltetem Treiber.");
        Directory.CreateDirectory(directory);
        await VerifyApplicationRulesAsync(owner, runtime, directory);
        await VerifyDevicesAndConnectionTestAsync(owner, runtime, directory);
        await VerifyNetworkProfilesAsync(owner, runtime, directory);
        await VerifyUsageHistoryAsync(owner, runtime, directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "features-result.txt"),
            "PASS: Programmregeln add/save/reopen/remove; Gerätealias und Sperre über UI; Verbindungstest in isoliertem Profil ohne UDP-Probe; Netzwerkprofil-Umbenennung; Verbrauch heute/7 Tage mit unbekannten Richtungen; alle fünf Featurefenster normal/minimum gerendert, Buttons geprüft. Keine Treiber- oder Produktionsdaten.\n");
    }

    private static async Task VerifyApplicationRulesAsync(MainWindow owner, DynexRuntime runtime, string directory)
    {
        var rules = CreateRulesWindow(owner, runtime);
        rules.Show(); await ReadyAsync(rules);
        var process = Named<ComboBox>(rules, "ProcessBox");
        var down = Named<TextBox>(rules, "DownloadBox");
        var upload = Named<TextBox>(rules, "UploadBox");
        process.ApplyTemplate();
        if (process.Template.FindName("PART_EditableTextBox", process) is not TextBox editor || !editor.IsVisible)
            throw new InvalidOperationException("Programmname lässt sich im echten Auswahlfeld nicht eingeben.");
        editor.Text = "FeatureUiSmoke.exe"; down.Text = "17.5"; upload.Text = "";
        Click(Named<Button>(rules, "ApplyButton"));
        if (runtime.Settings.ApplicationRules.Any(r => r.ProcessName == RuleName))
            throw new InvalidOperationException("Programmregel wurde bereits vor dem ausdrücklichen Speichern persistent.");
        Click(Named<Button>(rules, "SaveButton"));
        await UntilAsync(() => runtime.Settings.ApplicationRules.Any(r => r.ProcessName == RuleName));
        rules.Close();

        rules = CreateRulesWindow(owner, runtime);
        rules.Show(); await ReadyAsync(rules);
        var list = Named<DataGrid>(rules, "RulesList");
        var persisted = list.Items.Cast<ApplicationRule>().Single(r => r.ProcessName == RuleName);
        if (persisted.DownloadMbps != 17.5 || persisted.UploadMbps is not null)
            throw new InvalidOperationException("Programmregel wurde nach erneutem Öffnen verändert.");
        list.SelectedItem = persisted;
        var remove = Named<Button>(rules, "RemoveButton");
        if (!remove.IsEnabled) throw new InvalidOperationException("Entfernen wird nach Auswahl einer Regel nicht aktiviert.");
        await CaptureSizesAsync(rules, directory, "application-rules");
        Click(remove);
        if (!runtime.Settings.ApplicationRules.Any(r => r.ProcessName == RuleName))
            throw new InvalidOperationException("Entfernen speichert ohne ausdrücklichen Speicherklick.");
        Click(Named<Button>(rules, "SaveButton"));
        await UntilAsync(() => runtime.Settings.ApplicationRules.All(r => r.ProcessName != RuleName));
        rules.Close();
    }

    private static ApplicationRulesWindow CreateRulesWindow(MainWindow owner, DynexRuntime runtime) => new(
        () => runtime.Settings.ApplicationRules,
        values => runtime.SaveSettingsAsync(runtime.Settings with { ApplicationRules = values })) { Owner = owner };

    private static async Task VerifyDevicesAndConnectionTestAsync(MainWindow owner, DynexRuntime runtime, string directory)
    {
        var registry = typeof(DynexRuntime).GetField("deviceRegistry", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runtime) as DeviceRegistry
            ?? throw new InvalidOperationException("Isolierte Geräteliste konnte für den UI-Fixture nicht geladen werden.");
        var id = Guid.NewGuid();
        var peer = new PeerAdvertisement(id, "Feature QA PC", false, 8, null, DateTimeOffset.UtcNow,
            System.Net.IPAddress.Loopback.ToString(), RemotePort: runtime.PeerPort);
        registry.Observe([peer]);
        registry.Flush();
        if (runtime.Devices.All(d => d.DeviceId != id)) throw new InvalidOperationException("Seed für authentifizierte Geräte-Registry fehlt.");

        var devices = new DeviceManagerWindow(runtime) { Owner = owner };
        devices.Show(); await ReadyAsync(devices);
        var grid = Named<DataGrid>(devices, "DeviceGrid");
        grid.SelectedItem = runtime.Devices.Single(d => d.DeviceId == id);
        Named<TextBox>(devices, "AliasBox").Text = "QA Wohnzimmer-PC";
        Named<ComboBox>(devices, "DispositionBox").SelectedIndex = (int)DeviceDisposition.Blocked;
        Click(Named<Button>(devices, "SaveButton"));
        await UntilAsync(() => runtime.Devices.Single(d => d.DeviceId == id).Device is
            { Alias: "QA Wohnzimmer-PC", Disposition: DeviceDisposition.Blocked });
        await CaptureSizesAsync(devices, directory, "device-manager");

        var connection = new ConnectionTestWindow(runtime, id) { Owner = owner };
        connection.Show(); await ReadyAsync(connection);
        if (!Named<Button>(connection, "StartButton").IsEnabled || Named<ComboBox>(connection, "TargetBox").SelectedItem is null)
            throw new InvalidOperationException("Verbindungstest zeigt das authentifizierte Gerät nicht als Ziel.");
        // The seeded device is blocked, so this real UI action must stop locally before any datagram can be sent.
        Click(Named<Button>(connection, "StartButton"));
        await UntilAsync(() => Named<Button>(connection, "StartButton").IsEnabled);
        if (!Named<TextBlock>(connection, "Feedback").Text.StartsWith("Ergebnis je Stufe", StringComparison.Ordinal))
            throw new InvalidOperationException("Verbindungstest zeigt nach dem tatsächlichen Button-Klick kein Ergebnis.");
        await CaptureSizesAsync(connection, directory, "connection-test");
        connection.Close(); devices.Close();
    }

    private static async Task VerifyNetworkProfilesAsync(MainWindow owner, DynexRuntime runtime, string directory)
    {
        var id = Guid.NewGuid().ToString("D");
        var applyNetwork = typeof(DynexRuntime).GetMethod("ApplyNetworkIdentity", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Netzwerkprofil-Fixture konnte nicht initialisiert werden.");
        applyNetwork.Invoke(runtime, [new NetworkIdentity(id, "Feature QA Netzwerk")]);
        var window = new NetworkProfilesWindow(runtime) { Owner = owner };
        window.Show(); await ReadyAsync(window);
        Named<DataGrid>(window, "ProfilesGrid").SelectedIndex = 0;
        Named<TextBox>(window, "NameBox").Text = "QA Netzwerk umbenannt";
        Click(FindButton(window, "Name speichern"));
        await UntilAsync(() => runtime.Settings.NetworkProfiles.Single(p => p.Id == id).Name == "QA Netzwerk umbenannt");
        if (!Named<TextBlock>(window, "StatusText").Text.Contains("gespeichert", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Netzwerkprofil-Umbenennung zeigt keine gespeicherte Rückmeldung.");
        await CaptureSizesAsync(window, directory, "network-profiles");
        window.Close();
    }

    private static async Task VerifyUsageHistoryAsync(MainWindow owner, DynexRuntime runtime, string directory)
    {
        var store = typeof(DynexRuntime).GetField("usageHistory", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runtime) as UsageHistoryStore
            ?? throw new InvalidOperationException("Persistenter Verbrauchsverlauf konnte im isolierten Profil nicht geladen werden.");
        var now = DateTimeOffset.UtcNow;
        store.Add(new("feature-ui-down-up", "QA Mess-PC", now.AddSeconds(-2), now, 0.012, 0.003));
        store.Add(new("feature-ui-unknown", "QA Messlücke", now.AddSeconds(-2), now, null, null));
        store.Flush();
        var window = new UsageHistoryWindow(store) { Owner = owner };
        window.Show(); await ReadyAsync(window);
        var rows = Named<DataGrid>(window, "UsageGrid").ItemsSource!.Cast<UsageHistoryRow>().ToArray();
        var measured = rows.Single(r => r.PcName == "QA Mess-PC");
        var unknown = rows.Single(r => r.PcName == "QA Messlücke");
        if (measured.DownloadText == "—" || measured.UploadText == "—" ||
            double.Parse(measured.DownloadText, CultureInfo.CurrentCulture) < 0 ||
            double.Parse(measured.UploadText, CultureInfo.CurrentCulture) < 0 ||
            unknown.DownloadText != "—" || unknown.UploadText != "—")
            throw new InvalidOperationException("Verbrauchsverlauf zeigt gemessene und unbekannte Richtungen nicht getrennt.");
        Click(Named<Button>(window, "WeekButton"));
        var selected = DateOnly.FromDateTime(DateTime.Today);
        if (!Named<TextBlock>(window, "InfoText").Text.Contains(selected.AddDays(-6).ToString("dd.MM.yyyy"), StringComparison.Ordinal))
            throw new InvalidOperationException("7-Tage-Schaltfläche wählt nicht den erwarteten Zeitraum.");
        Click(Named<Button>(window, "TodayButton"));
        var datePicker = Named<DatePicker>(window, "DatePicker");
        datePicker.SelectedDate = DateTime.Today.AddDays(-1);
        if (!Named<TextBlock>(window, "InfoText").Text.Contains(selected.AddDays(-1).ToString("dd.MM.yyyy"), StringComparison.Ordinal))
            throw new InvalidOperationException("Datumsauswahl aktualisiert den angezeigten Verbrauchszeitraum nicht.");
        Click(Named<Button>(window, "TodayButton"));
        if (datePicker.SelectedDate != DateTime.Today || !Named<TextBlock>(window, "InfoText").Text.StartsWith(selected.ToString("dd.MM.yyyy"), StringComparison.Ordinal))
            throw new InvalidOperationException("Heute kehrt nach einer vergangenen Datumsauswahl nicht zum heutigen Tag zurück.");
        await CaptureSizesAsync(window, directory, "usage-history");
        window.Close();
    }

    private static async Task CaptureSizesAsync(Window window, string directory, string stem)
    {
        foreach (var (label, width, height) in new[] { ("normal", window.Width, window.Height), ("minimum", window.MinWidth, window.MinHeight) })
        {
            window.Width = width; window.Height = height;
            await ReadyAsync(window);
            VerifyButtonsFit(window, label);
            Capture(window, Path.Combine(directory, $"{stem}-{label}.png"));
        }
    }

    private static void VerifyButtonsFit(Window window, string size)
    {
        if (window.Content is not FrameworkElement root) throw new InvalidOperationException("Fensterinhalt fehlt.");
        foreach (var button in Descendants<Button>(root).Where(b => b.IsVisible))
        {
            var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
            if (bounds.Left < -1 || bounds.Top < -1 || bounds.Right > root.ActualWidth + 1 || bounds.Bottom > root.ActualHeight + 1)
                throw new InvalidOperationException($"Schaltfläche „{button.Content}“ wird in {window.Title} ({size}) am Fensterrand abgeschnitten.");
            for (var parent = VisualTreeHelper.GetParent(button); parent is FrameworkElement ancestor && ancestor != root;
                 parent = VisualTreeHelper.GetParent(parent))
            {
                if (!ancestor.ClipToBounds && ancestor is not ScrollContentPresenter) continue;
                var visible = button.TransformToAncestor(ancestor).TransformBounds(new Rect(button.RenderSize));
                if (visible.Left < -1 || visible.Top < -1 || visible.Right > ancestor.ActualWidth + 1 || visible.Bottom > ancestor.ActualHeight + 1)
                    throw new InvalidOperationException($"Schaltfläche „{button.Content}“ wird in {window.Title} ({size}) von {ancestor.GetType().Name} abgeschnitten.");
            }
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static T Named<T>(FrameworkElement window, string name) where T : FrameworkElement =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"{window.GetType().Name}.{name} fehlt.");

    private static Button FindButton(Window window, string content) =>
        Descendants<Button>((DependencyObject)window.Content).FirstOrDefault(b => Equals(b.Content, content))
        ?? throw new InvalidOperationException($"Schaltfläche „{content}“ fehlt.");

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static async Task ReadyAsync(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        if (!window.IsVisible || window.ActualWidth < window.MinWidth - 2 || window.ActualHeight < window.MinHeight - 2)
            throw new InvalidOperationException($"{window.Title} konnte im isolierten Desktop nicht gerendert werden.");
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var time = Stopwatch.StartNew();
        while (!condition())
        {
            if (time.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Feature-UI-Aktion wurde nicht abgeschlossen.");
            await Task.Delay(25);
        }
    }

    private static void Capture(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
