using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Dynex.Desktop.Games;
using Dynex.Desktop.Updates;
using Forms = System.Windows.Forms;

namespace Dynex.Desktop;

public partial class MainWindow : Window
{
    private readonly DynexRuntime _runtime;
    private readonly GitHubUpdateService? _updates;
    private readonly Forms.NotifyIcon _tray;
    private readonly Icon _trayIcon;
    private readonly Stream? _trayIconStream;
    private readonly bool _ownsTrayIcon;
    private bool _allowClose;
    private bool _revealed;
    private bool _loading = true;
    private bool _settingUp;
    private bool _refreshingProcesses;
    private bool _savingAutoUpdate;
    private IReadOnlyList<HistoryPoint> _lastHistory = Array.Empty<HistoryPoint>();
    private double? _historyPointerRatio;
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 2;

    public MainWindow(DynexRuntime runtime, GitHubUpdateService? updates = null)
    {
        InitializeComponent();
        DeviceGrid.Language = System.Windows.Markup.XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
        _runtime = runtime;
        _updates = updates;
        Icon trayIcon;
        Stream? iconStream = null;
        var ownsTrayIcon = false;
        try
        {
            iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/dynex.ico", UriKind.Absolute))?.Stream
                ?? throw new InvalidOperationException("DYNEX-Tray-Symbol nicht gefunden.");
            trayIcon = new Icon(iconStream);
            ownsTrayIcon = true;
        }
        catch
        {
            iconStream?.Dispose();
            iconStream = null;
            trayIcon = SystemIcons.Application;
        }
        _trayIcon = trayIcon; _trayIconStream = iconStream; _ownsTrayIcon = ownsTrayIcon;
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = "DYNEX – Gaming-Netzwerkpriorisierung", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("DYNEX öffnen", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Beenden", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        _runtime.Updated += Runtime_Updated;
        if (_updates is not null) _updates.Changed += UpdateStatus_Changed;
        LoadSettings(_runtime.Settings);
        CheckUpdatesButton.IsEnabled = _updates is not null;
        RenderUpdateStatus();
        if (_runtime.Snapshot is { } current) RenderSnapshot(current);
        ApplyResponsiveScale();
    }

    private void Runtime_Updated(RuntimeSnapshot snapshot) => Dispatcher.BeginInvoke(new Action(() => RenderSnapshot(snapshot)));
    private void LoadSettings(UserSettings s)
    {
        _loading = true;
        RenderCapacity();
        AutoConnectCheck.IsChecked = s.AutoConnectLan; AutoStartCheck.IsChecked = s.AutoStart;
        AutoUpdateCheck.IsChecked = s.AutoUpdate;
        DriverCheck.IsChecked = s.DriverEnabled; ProcessesBox.Text = s.GameProcesses; PeersBox.Text = s.DirectPeers;
        RouterBox.Text = s.RouterTarget; ExternalBox.Text = s.ExternalTarget;
        ManualLimitBox.Text = s.ManualLimitMbps?.ToString(CultureInfo.CurrentCulture) ?? "";
        ModeCombo.SelectedIndex = s.Mode switch { GamingMode.Auto => 0, GamingMode.On => 1, _ => 2 };
        _loading = false;
        _updates?.SetEnabled(s.AutoUpdate);
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        await RunUiAction(async () =>
        {
            var current = _runtime.Settings;
            var next = current with
            {
                AutoConnectLan = AutoConnectCheck.IsChecked == true,
                AutoUpdate = AutoUpdateCheck.IsChecked == true,
                AutoStart = AutoStartCheck.IsChecked == true, DriverEnabled = DriverCheck.IsChecked == true,
                GameProcesses = ProcessesBox.Text.Trim(), DirectPeers = PeersBox.Text.Trim(),
                RouterTarget = RouterBox.Text.Trim(), ExternalTarget = ExternalBox.Text.Trim()
            };
            await _runtime.SaveSettingsAsync(next);
            _updates?.SetEnabled(next.AutoUpdate);
            ErrorText.Text = "Einstellungen gespeichert.";
        });
    }

    private async void AutoUpdateCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _savingAutoUpdate) return;
        var requested = AutoUpdateCheck.IsChecked == true;
        _savingAutoUpdate = true;
        AutoUpdateCheck.IsEnabled = false;
        try
        {
            await _runtime.SaveAutoUpdatePreferenceAsync(requested);
            _updates?.SetEnabled(requested);
            ErrorText.Text = requested ? "Automatische Updates eingeschaltet." : "Automatische Updates ausgeschaltet.";
        }
        catch (Exception ex)
        {
            _loading = true;
            AutoUpdateCheck.IsChecked = _runtime.Settings.AutoUpdate;
            _loading = false;
            ErrorText.Text = $"Update-Einstellung konnte nicht gespeichert werden: {ex.Message}";
        }
        finally
        {
            _savingAutoUpdate = false;
            AutoUpdateCheck.IsEnabled = true;
            RenderUpdateStatus();
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_updates is null) return;
        CheckUpdatesButton.IsEnabled = false;
        try { await _updates.CheckNowAsync(); }
        catch (Exception ex) { UpdateStatusText.Text = "Updateprüfung fehlgeschlagen: " + ex.Message; }
        finally { CheckUpdatesButton.IsEnabled = _updates is not null; RenderUpdateStatus(); }
    }

    private void UpdateStatus_Changed() => Dispatcher.BeginInvoke(new Action(RenderUpdateStatus));

    private void RenderUpdateStatus()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RenderUpdateStatus)); return; }
        if (_updates is null)
        {
            UpdateStatusText.Text = _runtime.Settings.AutoUpdate
                ? "Updates sind vorgemerkt; die Updateprüfung ist in dieser Entwicklungsansicht nicht verfügbar."
                : "Automatische Updates sind ausgeschaltet.";
            CheckUpdatesButton.IsEnabled = false;
            return;
        }
        UpdateStatusText.Text = _updates.Status.Message;
        CheckUpdatesButton.IsEnabled = !_updates.Status.IsBusy;
    }

    private async void RunningProcesses_DropDownOpened(object sender, EventArgs e) => await RefreshRunningProcessesAsync();
    private async void RefreshProcesses_Click(object sender, RoutedEventArgs e) => await RefreshRunningProcessesAsync();
    internal async Task RefreshRunningProcessesAsync()
    {
        if (_refreshingProcesses) return;
        _refreshingProcesses = true;
        AddProcessButton.IsEnabled = false;
        RefreshProcessesButton.IsEnabled = false;
        ProcessPickerHint.Text = "Laufende Programme werden geladen …";
        var selected = RunningProcessesBox.SelectedItem as string;
        try
        {
            var names = await Task.Run(GameDetector.GetRunningProcessNames);
            if (_allowClose) return;
            RunningProcessesBox.ItemsSource = names;
            RunningProcessesBox.SelectedItem = names.FirstOrDefault(name => string.Equals(name, selected, StringComparison.OrdinalIgnoreCase));
            ProcessPickerHint.Text = names.Count == 0
                ? "Keine auswählbaren Programme gefunden. Spiel starten und Liste aktualisieren oder den Namen unten eingeben."
                : $"{names.Count} laufende Programme · auswählen und hinzufügen. Bekannte Launcher und Hilfsprozesse sind ausgeblendet.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RunningProcessesBox.ItemsSource = Array.Empty<string>();
            ProcessPickerHint.Text = "Programmliste konnte nicht geladen werden: " + ex.Message;
        }
        finally
        {
            _refreshingProcesses = false;
            RefreshProcessesButton.IsEnabled = true;
            AddProcessButton.IsEnabled = RunningProcessesBox.SelectedItem is string;
        }
    }
    private void RunningProcesses_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AddProcessButton is not null)
            AddProcessButton.IsEnabled = !_refreshingProcesses && RunningProcessesBox.SelectedItem is string;
    }
    private void AddProcess_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshingProcesses || RunningProcessesBox.SelectedItem is not string selected) return;
        try
        {
            var updated = GameDetector.AppendConfiguredProcess(ProcessesBox.Text, selected);
            var added = updated != ProcessesBox.Text;
            ProcessesBox.Text = updated;
            ProcessPickerHint.Text = added
                ? $"{selected} hinzugefügt. Mit „Einstellungen speichern“ übernehmen."
                : $"{selected} ist bereits eingetragen.";
        }
        catch (ArgumentException ex) { ProcessPickerHint.Text = ex.Message; }
    }

    private async void SpeedTest_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        await _runtime.MeasureCapacityAsync();
        RenderCapacity();
    });

    private void RenderCapacity()
    {
        var settings = _runtime.Settings;
        var running = _runtime.IsSpeedTestRunning;
        CapacityBox.Text = settings.CapacityMbps.ToString("0.##", CultureInfo.CurrentCulture);
        CapacityBox.Opacity = settings.CapacityMeasuredAtUtc.HasValue ? 1 : 0.6;
        SpeedTestButton.IsEnabled = !running;
        SpeedTestButton.Content = running ? "Messung läuft …" : settings.CapacityMeasuredAtUtc.HasValue ? "Erneut messen" : "Speedtest starten";
        SpeedTestProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        var measured = settings.CapacityMeasuredAtUtc is { } at
            ? $"Gemessen am {at.ToLocalTime():dd.MM.yyyy HH:mm}. "
            : "Noch nicht gemessen · vorläufiger Wert. ";
        SpeedTestHint.Text = running ? _runtime.SpeedTestMessage : measured +
            (string.IsNullOrEmpty(_runtime.SpeedTestMessage)
                ? "DYNEX ermittelt den Download einmal automatisch. Für die Messung andere Downloads möglichst pausieren."
                : _runtime.SpeedTestMessage);
    }

    private async void ManualLimit_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        var value = Number(ManualLimitBox.Text, "Downloadlimit");
        await _runtime.SaveSettingsAsync(_runtime.Settings with { ManualLimitMbps = value });
        ErrorText.Text = "Manuelles Limit gespeichert.";
    });
    private async void ClearLimit_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        ManualLimitBox.Clear();
        await _runtime.SaveSettingsAsync(_runtime.Settings with { ManualLimitMbps = null });
        ErrorText.Text = "Manuelles Limit aufgehoben.";
    });
    private async void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModeCombo.SelectedItem is not ComboBoxItem item || Enum.TryParse<GamingMode>(item.Tag?.ToString(), out var mode) is false) return;
        await RunUiAction(async () => await _runtime.SaveSettingsAsync(_runtime.Settings with { Mode = mode }));
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () => await _runtime.SaveSettingsAsync(_runtime.Settings));
    private async void Setup_Click(object sender, RoutedEventArgs e)
    {
        if (_settingUp) return;
        _settingUp = true;
        SetupButton.IsEnabled = false;
        try
        {
            await RunUiAction(async () =>
            {
                var restart = await PacketFilterSetup.InstallAsync(message => SetupHint.Text = message);
                if (restart)
                {
                    ErrorText.Text = "Einrichtung abgeschlossen. Windows benötigt einen Neustart, bevor die Begrenzung eingeschaltet wird.";
                    return;
                }
                await _runtime.SaveSettingsAsync(_runtime.Settings with { DriverEnabled = true });
                DriverCheck.IsChecked = true;
                ErrorText.Text = "Einrichtung abgeschlossen. Die Downloadbegrenzung wird gestartet.";
            });
        }
        finally { _settingUp = false; SetupButton.IsEnabled = true; }
    }
    private async void RevealCode_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        if (!_revealed) PairingBox.Text = _runtime.ExportPairingCode();
        else PairingBox.Clear();
        _revealed = !_revealed; ((System.Windows.Controls.Button)sender).Content = _revealed ? "Verbergen" : "Anzeigen";
        await Task.CompletedTask;
    });
    private async void CopyCode_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        var code = _runtime.ExportPairingCode();
        Forms.Clipboard.SetText(code); ErrorText.Text = "Gruppencode in die Zwischenablage kopiert.";
        if (_revealed) PairingBox.Text = code;
        await Task.CompletedTask;
    });
    private async void PasteCode_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        if (!Forms.Clipboard.ContainsText()) throw new InvalidOperationException("Die Zwischenablage enthält keinen Text.");
        PairingBox.Text = Forms.Clipboard.GetText(); _revealed = true;
        await Task.CompletedTask;
    });
    private async void ImportCode_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        if (string.IsNullOrWhiteSpace(PairingBox.Text)) throw new InvalidOperationException("Bitte zuerst einen Gruppencode einfügen.");
        await _runtime.ImportPairingCodeAsync(PairingBox.Text.Trim());
        PairingBox.Clear(); _revealed = false;
        AutoConnectCheck.IsChecked = _runtime.Settings.AutoConnectLan;
        ErrorText.Text = "Gruppencode importiert.";
    });
    private async void Diagnostics_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        var report = await _runtime.ExportDiagnosticsAsync();
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "dynex-diagnose.json", Filter = "JSON-Datei (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true) File.Copy(report, dialog.FileName, true);
    });

    private async Task RunUiAction(Func<Task> action)
    {
        try { ErrorText.Text = ""; await action(); }
        catch (Exception ex) { ErrorText.Text = $"Aktion fehlgeschlagen: {ex.Message}"; }
    }

    private void RenderSnapshot(RuntimeSnapshot s)
    {
        RenderCapacity();
        HeaderState.Text = s.State.ToUpperInvariant(); DetailText.Text = s.Detail;
        GameText.Text = _runtime.Settings.Mode == GamingMode.Off ? "Automatische Erkennung ausgeschaltet" : s.IsGaming ? $"Gaming erkannt · {s.Game}" : "Kein Spiel erkannt";
        DownText.Text = s.DownloadMbps is { } down ? $"{down:0.0} Mbps" : "— Mbps";
        UpText.Text = s.UploadMbps is { } up ? $"{up:0.0} Mbps" : "— Mbps";
        LimitText.Text = s.LimitMbps is { } cap ? $"{cap:0.0} Mbps" : "Kein Limit";
        var installed = PacketFilterSetup.IsInstalled;
        DriverText.Text = s.DriverActive ? "LIMIT AKTIV" : s.QosActive ? "PRIORITÄT AKTIV" : !s.DriverAvailable ? "PAKET UNVOLLSTÄNDIG" : !installed ? "EINMAL EINRICHTEN" : _runtime.Settings.DriverEnabled ? "WARTET" : "AUS";
        DriverDetail.Text = s.DriverActive ? "Die Downloadbegrenzung wurde bestätigt." : s.QosActive ? "QoS bestätigt; das Downloadlimit ist nicht aktiv." : s.DriverAvailable ? s.DriverState : "DYNEX bitte vollständig installieren.";
        ProtectionStatusText.Text = (s.DriverActive, s.QosActive) switch
        {
            (true, true) => "NETZWERKSCHUTZ · PRIORISIERUNG UND DOWNLOADLIMIT AKTIV",
            (true, false) => "NETZWERKSCHUTZ · DOWNLOADLIMIT AKTIV",
            (false, true) => "NETZWERKSCHUTZ · PRIORISIERUNG AKTIV",
            _ => "NETZWERKSCHUTZ · NOCH NICHT AKTIV"
        };
        if (s.QosActive)
        {
            QosStatusText.Text = "Priorisierung aktiv";
            QosDetailText.Text = $"Spiele: {s.GamePriorityPackets:N0} · Sprachchat: {s.VoicePriorityPackets:N0} Pakete";
        }
        else if (s.GamePriorityPackets > 0 || s.VoicePriorityPackets > 0)
        {
            QosStatusText.Text = "QoS nicht bestätigt";
            QosDetailText.Text = $"Zuletzt: {s.GamePriorityPackets:N0} Spiel- und {s.VoicePriorityPackets:N0} Sprachpakete";
        }
        else
        {
            QosStatusText.Text = "Priorisierung noch nicht aktiv";
            QosDetailText.Text = "Spiele & Sprachchat werden beim Einschalten bevorzugt.";
        }
        QosDetailText.ToolTip = "Erkannte Pakete werden auf diesem PC bevorzugt weitergegeben; ausgehend zusätzlich für QoS markiert. Der Router muss diese Markierungen unterstützen. Zähler gelten für die aktuelle Filtersitzung und bestätigen keine Priorität im Router.";
        DriverCheck.IsEnabled = s.DriverAvailable && installed && !_settingUp;
        SetupButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        SetupButton.IsEnabled = s.DriverAvailable && !_settingUp;
        if (!_settingUp) SetupHint.Text = !s.DriverAvailable ? "Das Download-Modul fehlt. DYNEX bitte vollständig installieren." : installed
            ? "Spiele und Sprachchat bleiben frei. Windows bestätigt das Einschalten."
            : "Einmalige Einrichtung für private Nutzung. Windows fragt nach einer Bestätigung; die Netzwerkverbindung kann kurz unterbrochen werden.";
        SetPing(s.Router, RouterPing, RouterStats, RouterTarget); SetPing(s.External, ExternalPing, ExternalStats, ExternalTarget);
        DeviceGrid.ItemsSource = s.Devices;
        DeviceCount.Text = $"{s.Devices.Count} GERÄTE"; NetworkStatus.Text = s.NetworkStatus;
        SharedBudgetText.Text = s.SharedDownloadBudgetMbps is { } pool
            ? $"Gemeinsames Downloadbudget: {pool:0.0} Mbit/s für {s.ParticipantCount} {(s.ParticipantCount == 1 ? "PC" : "PCs")} · Dein aktives Limit: {(s.LimitMbps is { } own ? $"{own:0.0} Mbit/s" : "noch nicht aktiv") }"
            : "Beim Gaming teilen sich alle DYNEX-PCs ein gemeinsames Downloadbudget.";
        CoordinationText.Text = s.SharedDownloadBudgetMbps.HasValue
            ? $"{s.GamingCount} {(s.GamingCount == 1 ? "PC spielt" : "PCs spielen")} · Budget auf {s.ConfirmedParticipantCount}/{s.ParticipantCount} PCs bestätigt. Spiele und Sprachchat umgehen die Downloadwarteschlange." +
                (!s.SharedBudgetComplete ? " Gemeinsame Aufteilung noch unvollständig. DYNEX-Version und Anschlussmessung auf allen PCs prüfen." :
                    s.ConfirmedParticipantCount < s.ParticipantCount ? " Anteile werden noch übernommen; die Begrenzung muss auf jedem PC eingeschaltet sein." : "")
            : "Spiel- und Sprachchatpakete umgehen die Downloadwarteschlange. DYNEX muss auf jedem beteiligten PC laufen.";
        PairFingerprint.Text = string.IsNullOrWhiteSpace(s.PairingFingerprint) ? "Noch keine Gruppe gekoppelt" : $"Gruppe · {s.PairingFingerprint}";
        DrawHistory(s.History);
        HistoryCompare.Text = $"Ohne Begrenzung: {CompareHistory(s.History, false)}\nMit Begrenzung: {CompareHistory(s.History, true)}";
        HistoryCompare.ToolTip = "Vergleich gemessener Internet-Pings. Unterschiedliche Werte allein beweisen noch keine Verbesserung durch DYNEX.";
    }
    private static void SetPing(PingView ping, TextBlock current, TextBlock stats, TextBlock target)
    {
        current.Text = ping.Current is { } ms ? $"{ms:0.0} ms" : "— ms";
        stats.Text = $"Schwankung {Fmt(ping.Jitter)}\nFehlende Antworten {Fmt(ping.Loss, "%")}";
        target.Text = string.IsNullOrWhiteSpace(ping.Target) ? "Ziel nicht konfiguriert" : ping.Target;
    }
    private static string Fmt(double? v, string suffix = " ms") => v is { } n ? $"{n:0.0}{suffix}" : "—";
    private static string CompareHistory(IReadOnlyList<HistoryPoint> history, bool protection)
    {
        var samples = history.Where(p => p.Protection == protection).ToArray();
        var received = samples.Where(p => p.ExternalMs.HasValue).Select(p => p.ExternalMs!.Value).ToArray();
        var jitter = history.Zip(history.Skip(1))
            .Where(pair => pair.First.Protection == protection && pair.Second.Protection == protection &&
                           (pair.Second.Time - pair.First.Time).TotalSeconds < 5 &&
                           pair.First.ExternalMs.HasValue && pair.Second.ExternalMs.HasValue)
            .Select(pair => Math.Abs(pair.Second.ExternalMs!.Value - pair.First.ExternalMs!.Value)).ToArray();
        var mean = received.Length > 0 ? $"{received.Average():0.0} ms" : "—";
        var loss = samples.Length > 0 ? $"{(samples.Length - received.Length) * 100.0 / samples.Length:0.#}%" : "—";
        var variation = jitter.Length > 0 ? $"{jitter.Average():0.0} ms" : "—";
        return samples.Length == 0 ? "noch keine Messungen" : $"Ø {mean} · Schwankung {variation} · {loss} ohne Antwort";
    }
    private void DrawHistory(IReadOnlyList<HistoryPoint> history)
    {
        _lastHistory = history;
        HistoryCanvas.Children.Clear();
        HistoryHoverCanvas.Children.Clear();
        var valid = history.TakeLast(90).ToArray();
        RefreshHistoryHover(valid);
        if (valid.Length < 2) return;
        var series = new (Func<HistoryPoint, double?> Pick, System.Windows.Media.Brush Brush)[]
        {
            (p => p.RouterMs, (System.Windows.Media.Brush)FindResource("Accent")),
            (p => p.ExternalMs, new SolidColorBrush(System.Windows.Media.Color.FromRgb(226,160,167)))
        };
        var vals = valid.SelectMany(p => new[] { p.RouterMs, p.ExternalMs }).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        if (vals.Length == 0) return;
        var max = Math.Max(20, vals.Max() * 1.15); var w = Math.Max(1, HistoryCanvas.ActualWidth); var h = HistoryCanvas.ActualHeight;
        foreach (var (pick, brush) in series)
        {
            Polyline? line = null;
            for (var i = 0; i < valid.Length; i++)
            {
                if (pick(valid[i]) is { } value)
                {
                    line ??= new Polyline { Stroke = brush, StrokeThickness = 1.7, Opacity = .95 };
                    line.Points.Add(new System.Windows.Point(i * w / (valid.Length - 1), h - 5 - Math.Clamp(value / max, 0, 1) * (h - 12)));
                }
                else
                {
                    if (line is { Points.Count: > 1 }) HistoryCanvas.Children.Add(line);
                    line = null;
                }
            }
            if (line is { Points.Count: > 1 }) HistoryCanvas.Children.Add(line);
        }
        UpdateHistoryHoverVisuals(valid, max);
    }
    private void UpdateHistoryHoverVisuals(IReadOnlyList<HistoryPoint> valid, double? max = null)
    {
        HistoryHoverCanvas.Children.Clear();
        if (_historyPointerRatio is not { } ratio || valid.Count == 0 || HistoryCanvas.ActualWidth < 2 || HistoryCanvas.ActualHeight < 2) return;
        var vals = valid.SelectMany(p => new[] { p.RouterMs, p.ExternalMs }).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        if (vals.Length == 0) return;
        var scaleMax = max ?? Math.Max(20, vals.Max() * 1.15);
        var height = HistoryCanvas.ActualHeight;
        var width = HistoryCanvas.ActualWidth;
        var index = HistoryIndexForPointer(ratio, valid.Count);
        var point = valid[index];
        var x = valid.Count > 1 ? index * width / (valid.Count - 1) : width / 2;
        HistoryHoverCanvas.Children.Add(new Line
        {
            X1 = x, X2 = x, Y1 = 0, Y2 = height, Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(125, 129, 140)),
            StrokeThickness = 1, StrokeDashArray = new DoubleCollection([3, 3]), IsHitTestVisible = false
        });
        AddHistoryMarker(point.RouterMs, (System.Windows.Media.Brush)FindResource("Accent"), x, scaleMax, height);
        AddHistoryMarker(point.ExternalMs, new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 160, 167)), x, scaleMax, height);
    }
    private void AddHistoryMarker(double? value, System.Windows.Media.Brush brush, double x, double max, double height)
    {
        if (value is not { } measured) return;
        var y = height - 5 - Math.Clamp(measured / max, 0, 1) * (height - 12);
        var marker = new Ellipse { Width = 10, Height = 10, Fill = brush, Stroke = (System.Windows.Media.Brush)FindResource("Panel"), StrokeThickness = 2, IsHitTestVisible = false };
        Canvas.SetLeft(marker, x - marker.Width / 2);
        Canvas.SetTop(marker, y - marker.Height / 2);
        HistoryHoverCanvas.Children.Add(marker);
    }
    private static string FormatHoverPing(double? value) => value is { } ms ? $"{ms:0.0} ms" : "keine Messung";
    private void HistoryCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        SetHistoryHoverAt(e.GetPosition(HistoryCanvas).X / Math.Max(1, HistoryCanvas.ActualWidth));
    }
    internal void SetHistoryHoverAt(double ratio)
    {
        _historyPointerRatio = Math.Clamp(ratio, 0, 1);
        var visible = _lastHistory.TakeLast(90).ToArray();
        RefreshHistoryHover(visible);
        UpdateHistoryHoverVisuals(visible);
    }
    private void HistoryCanvas_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ClearHistoryHover();
    }
    internal void ClearHistoryHover()
    {
        _historyPointerRatio = null;
        HistoryHoverText.Text = "Zeige auf einen Punkt für Zeit und Messwerte";
        HistoryHoverCanvas.Children.Clear();
    }
    private void RefreshHistoryHover(IReadOnlyList<HistoryPoint> points)
    {
        if (_historyPointerRatio is not { } ratio)
        {
            HistoryHoverText.Text = "Zeige auf einen Punkt für Zeit und Messwerte";
            return;
        }
        if (points.Count == 0)
        {
            HistoryHoverText.Text = "Noch keine Ping-Messpunkte";
            return;
        }
        HistoryHoverText.Text = FormatHistoryHover(points[HistoryIndexForPointer(ratio, points.Count)]);
    }
    internal static int HistoryIndexForPointer(double ratio, int pointCount) =>
        pointCount <= 1 || !double.IsFinite(ratio) ? 0 : Math.Clamp((int)Math.Round(Math.Clamp(ratio, 0, 1) * (pointCount - 1)), 0, pointCount - 1);
    internal static string FormatHistoryHover(HistoryPoint point) =>
        $"{point.Time.ToLocalTime():dd.MM.yyyy HH:mm:ss} · Router {FormatHoverPing(point.RouterMs)} · Internet {FormatHoverPing(point.ExternalMs)}";
    private void HistoryCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawHistory(_lastHistory);

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveScale();
    private void ApplyResponsiveScale()
    {
        if (!IsInitialized) return;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var scale = Math.Clamp(1 + (width - 1240) / 1320 * .33, 1, 1.33);
        var resources = System.Windows.Application.Current.Resources;
        resources["UiSmallFontSize"] = 12 * scale;
        resources["UiBodyFontSize"] = 14 * scale;
        resources["UiSectionFontSize"] = 16 * scale;
        resources["UiMetricFontSize"] = 24 * scale;
        resources["UiHeroFontSize"] = 32 * scale;
        // Keep headers and live values readable as the rest of the dashboard grows.
        // The device-name column keeps the remaining space and its existing minimum.
        var columnWidths = new[] { 0d, 125, 105, 130, 125, 115 };
        for (var column = 1; column < DeviceGrid.Columns.Count && column < columnWidths.Length; column++)
            DeviceGrid.Columns[column].Width = new DataGridLength(columnWidths[column] * scale);
        var stacked = width < 1100;
        StatusGrid.Columns = stacked ? 2 : 4;
        PingLayout.ColumnDefinitions[0].Width = new GridLength(1.2, GridUnitType.Star);
        PingLayout.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : .8, GridUnitType.Star);
        PingLayout.RowDefinitions[0].Height = GridLength.Auto;
        PingLayout.RowDefinitions[1].Height = stacked ? GridLength.Auto : new GridLength(0);
        Grid.SetColumnSpan(ChartPanel, stacked ? 2 : 1);
        Grid.SetColumn(PingPanel, stacked ? 0 : 1);
        Grid.SetRow(PingPanel, stacked ? 1 : 0);
        Grid.SetColumnSpan(PingPanel, stacked ? 2 : 1);
        SettingsLayout.ColumnDefinitions[0].Width = new GridLength(1.05, GridUnitType.Star);
        SettingsLayout.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : .95, GridUnitType.Star);
        SettingsLayout.RowDefinitions[0].Height = GridLength.Auto;
        SettingsLayout.RowDefinitions[1].Height = stacked ? GridLength.Auto : new GridLength(0);
        Grid.SetColumnSpan(SettingsPanel, stacked ? 2 : 1);
        Grid.SetColumn(AutomationPanel, stacked ? 0 : 1);
        Grid.SetRow(AutomationPanel, stacked ? 1 : 0);
        Grid.SetColumnSpan(AutomationPanel, stacked ? 2 : 1);
        HistoryPlot.Height = HistoryCanvas.Height = HistoryHoverCanvas.Height = 150 * scale;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(WindowProc);
    }
    private static IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return IntPtr.Zero;
        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MaxPosition = new PointI(monitorInfo.Work.Left - monitorInfo.Monitor.Left, monitorInfo.Work.Top - monitorInfo.Monitor.Top);
        info.MaxSize = new PointI(monitorInfo.Work.Right - monitorInfo.Work.Left, monitorInfo.Work.Bottom - monitorInfo.Work.Top);
        Marshal.StructureToPtr(info, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PointI { public int X, Y; public PointI(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public PointI Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public RectI Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private static double Number(string value, string label, bool allowZero = false)
    {
        var parsed = double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var n) || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out n);
        if (parsed && double.IsFinite(n) && (allowZero ? n >= 0 : n > 0)) return n;
        throw new ArgumentException(allowZero ? $"{label} muss eine Zahl ab 0 sein." : $"{label} muss eine Zahl größer 0 sein.");
    }
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "❐" : "□";
        MaximizeButton.ToolTip = maximized ? "Wiederherstellen" : "Maximieren";
    }
    private void CloseToTray_Click(object sender, RoutedEventArgs e) => HideToTray();
    private void HideToTray()
    {
        Hide();
        _tray.ShowBalloonTip(2500, "DYNEX läuft weiter", "DYNEX wurde in den Infobereich minimiert. Zum Beenden im Tray-Menü „Beenden“ wählen.", Forms.ToolTipIcon.Info);
    }
    private async void ExitApplication() => await RequestExitAsync();
    public async Task RequestExitAsync()
    {
        if (_allowClose) return;
        _allowClose = true;
        _tray.Visible = false;
        _runtime.Updated -= Runtime_Updated;
        if (_updates is not null) _updates.Changed -= UpdateStatus_Changed;
        try { await _runtime.StopAsync(); await _runtime.DisposeAsync(); }
        catch (Exception ex) { ErrorText.Text = $"Beim sauberen Beenden: {ex.Message}"; }
        _tray.Dispose();
        if (_ownsTrayIcon) _trayIcon.Dispose();
        _trayIconStream?.Dispose();
        System.Windows.Application.Current.Shutdown(DesktopLifecycle.ExitCode);
    }
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true; HideToTray();
    }
}
