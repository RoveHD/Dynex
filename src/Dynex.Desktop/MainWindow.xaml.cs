using System.Drawing;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
using Control = System.Windows.Controls.Control;

namespace Dynex.Desktop;

public partial class MainWindow : Window
{
    private readonly DynexRuntime _runtime;
    private readonly GitHubUpdateService? _updates;
    private readonly UiFeedback _feedback;
    private string _selectedSettingsSection = "general";
    private readonly Forms.NotifyIcon _tray;
    private readonly Icon _trayIcon;
    private readonly Stream? _trayIconStream;
    private readonly bool _ownsTrayIcon;
    private bool _allowClose;
    private bool _revealed;
    private bool _loading = true;
    private string? _settingsNetworkProfileId;
    private bool _settingUp;
    private bool _refreshingProcesses;
    private bool _processesLoaded;
    private string? _processLoadError;
    private IReadOnlyList<string> _runningProcessNames = Array.Empty<string>();
    private readonly ObservableCollection<string> _configuredProcesses = [];
    private bool _savingAutoUpdate;
    private bool _checkingNetwork;
    private bool _savingConnectionMode;
    private bool _refreshingStatus;
    private int _pendingUiActions;
    private bool _updateRestartInProgress;
    public event Action? UpdateRestartSafetyChanged;
    private IReadOnlyList<HistoryPoint> _lastHistory = Array.Empty<HistoryPoint>();
    private IReadOnlyList<DeviceView> _deviceSnapshot = Array.Empty<DeviceView>();
    private int _devicePage;
    private readonly Dynex.Core.StatusNotifications _notifications = new();
    private Dynex.Core.SessionSummary? _shownSession;
    private readonly bool _guideAllowed;
    private double? _historyPointerRatio;
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 2;

    public MainWindow(DynexRuntime runtime, GitHubUpdateService? updates = null, bool showSetupGuide = false)
    {
        _guideAllowed = showSetupGuide && !runtime.Settings.SetupGuideDone;
        InitializeComponent();
        DeviceGrid.Language = System.Windows.Markup.XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
        OverviewDeviceGrid.Language = System.Windows.Markup.XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
        _runtime = runtime;
        _updates = updates;
        _feedback = new UiFeedback(this, silent: DesktopLifecycle.ProfilePath is not null);
        ConfiguredProcessesList.ItemsSource = _configuredProcesses;
        _configuredProcesses.CollectionChanged += (_, _) =>
        {
            NoConfiguredProcessesText.Visibility = _configuredProcesses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NotifyUpdateRestartSafety();
        };
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
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        _runtime.Updated += Runtime_Updated;
        IsVisibleChanged += (_, _) => RenderPendingSnapshot();
        StateChanged += (_, _) => RenderPendingSnapshot();
        if (_updates is not null) _updates.Changed += UpdateStatus_Changed;
        LoadSettings(_runtime.Settings);
        SettingsPage.AddHandler(System.Windows.Controls.TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => NotifyUpdateRestartSafety()));
        SettingsPage.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => NotifyUpdateRestartSafety()));
        SettingsPage.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => NotifyUpdateRestartSafety()));
        SettingsPage.AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => NotifyUpdateRestartSafety()));
        CheckUpdatesButton.IsEnabled = _updates is not null;
        RenderUpdateStatus();
        SidebarVersionText.Text = $"VERSION {DynexRuntime.AppVersion}";
        if (_runtime.Snapshot is { } current) RenderSnapshot(current);
        ShowOverview();
        ApplyResponsiveScale();
    }

    private string? _shownPingAlert;
    private DateTime _analysisRenderedAt;
    private async void ExportQualityLog_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "dynex-internetstoerungen.csv", Filter = "CSV-Datei (*.csv)|*.csv" };
        if (dialog.ShowDialog(this) != true) return;
        await File.WriteAllTextAsync(dialog.FileName, _runtime.QualityLogCsv(), System.Text.Encoding.UTF8);
        ErrorText.Text = "Störungsprotokoll gespeichert. Es eignet sich als Nachweis gegenüber deinem Internetanbieter.";
    });
    private void RenderAnalysis(RuntimeSnapshot? s)
    {
        if (AnalysisSettingsSection.Visibility != Visibility.Visible) return;
        // The report reads small local files; refresh at most every 30 seconds.
        if (DateTime.UtcNow - _analysisRenderedAt > TimeSpan.FromSeconds(30))
        {
            _analysisRenderedAt = DateTime.UtcNow;
            WeeklyReportText.Text = _runtime.WeeklyReportText();
            QualityLogText.Text = _runtime.QualityEventCount == 0
                ? "Bisher keine Ausfälle oder Pingspitzen aufgezeichnet."
                : $"{_runtime.QualityEventCount} Störungen aufgezeichnet (Ausfälle bei funktionierendem Router und deutliche Pingspitzen).";
        }
        var guide = _runtime.RouterGuideFor();
        RouterGuideTitle.Text = guide.Router;
        RouterGuideSteps.Text = string.Join("\n", guide.Steps.Select((step, i) => $"{i + 1}. {step}"));
        if (s is null) return;
        var lines = new List<string>();
        if (s.TopPrograms is { Count: > 0 } top)
            lines.AddRange(top.Select(p => $"Dieser PC · {p.Name}: ↓ {p.DownMbps:0.0} / ↑ {p.UpMbps:0.0} Mbit/s"));
        lines.AddRange(s.Devices.Skip(1).Where(d => d.TopProgram is not null)
            .Select(d => $"{d.Name} · {d.TopProgram}: ↓ {d.DownloadMbps ?? 0:0.0} Mbit/s gesamt"));
        TopProgramsText.Text = lines.Count > 0 ? string.Join("\n", lines)
            : "Programme erscheinen, sobald beim Spielen die Priorisierung läuft.";
    }
    private void UpdateTray(RuntimeSnapshot s)
    {
        if (s.PingAlert is { } alert && !ReferenceEquals(alert, _shownPingAlert))
        {
            _shownPingAlert = alert;
            if (_runtime.Settings.Notifications) _tray.ShowBalloonTip(6000, "Ping gestiegen", alert, Forms.ToolTipIcon.Warning);
        }
        RenderGuide(s);
        if (s.LastSession is { } session && !ReferenceEquals(session, _shownSession))
        {
            _shownSession = session;
            if (_runtime.Settings.Notifications) _tray.ShowBalloonTip(8000, "Spielsitzung beendet", session.Text, Forms.ToolTipIcon.Info);
        }
        var ping = s.External.Current is { } ms ? $"{ms:0} ms" : "—";
        var text = $"DYNEX · Ping {ping} · {s.Devices.Count} Geräte";
        _tray.Text = text.Length <= 63 ? text : text[..63]; // NotifyIcon rejects longer texts.
        if (!_runtime.Settings.Notifications) return;
        var peers = s.Devices.Skip(1).Select(d => d.Name).ToArray();
        foreach (var note in _notifications.Observe(peers, s.QosActive, s.LimitReason, s.Connection, DateTimeOffset.UtcNow))
            if (!string.IsNullOrWhiteSpace(note.Text)) _tray.ShowBalloonTip(5000, note.Title, note.Text, Forms.ToolTipIcon.Info);
    }

    private RuntimeSnapshot? _pendingSnapshot;
    // While DYNEX sits in the notification area (typically during games) only the tray state is updated.
    // Charts, device lists and texts are rendered once when the window becomes visible again.
    private void Runtime_Updated(RuntimeSnapshot snapshot) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
    {
        NetworkProfileText.Text = _runtime.NetworkProfileStatus;
        if (_settingsNetworkProfileId != _runtime.Settings.ActiveNetworkProfileId)
            ErrorText.Text = "Netzwerk gewechselt: " + _runtime.ActiveNetworkName + ". Gespeicherte Netzwerkeinstellungen sind aktiv; vor dem Speichern die Einstellungen neu laden.";
        if (IsVisible && WindowState != WindowState.Minimized) { _pendingSnapshot = null; RenderSnapshot(snapshot); }
        else { _pendingSnapshot = snapshot; UpdateTray(snapshot); }
        if (_updates?.Status.UpdateReady == true) NotifyUpdateRestartSafety();
    }));
    private void RenderPendingSnapshot()
    {
        if (_pendingSnapshot is not { } pending || !IsVisible || WindowState == WindowState.Minimized) return;
        _pendingSnapshot = null;
        RenderSnapshot(pending);
    }
    private void LoadSettings(UserSettings s)
    {
        _loading = true;
        _settingsNetworkProfileId = s.ActiveNetworkProfileId;
        NetworkProfileText.Text = _runtime.NetworkProfileStatus;
        RenderCapacity();
        AutoConnectCheck.IsChecked = s.AutoConnectLan; AutoStartCheck.IsChecked = s.AutoStart;
        NotificationsCheck.IsChecked = s.Notifications;
        UiSoundsCheck.IsChecked = s.UiSounds;
        UiAnimationsCheck.IsChecked = s.UiAnimations;
        ApplyFeedbackPreferences();
        PingAlarmCheck.IsChecked = s.PingAlarm;
        EarlyGamesBox.Text = s.EarlyLimitGames;
        AutoUpdateCheck.IsChecked = s.AutoUpdate;
        BetaUpdatesCheck.IsChecked = s.BetaUpdates;
        DriverCheck.IsChecked = s.DriverEnabled; PeersBox.Text = s.DirectPeers;
        _configuredProcesses.Clear();
        foreach (var process in SettingsStore.Split(s.GameProcesses)) _configuredProcesses.Add(process);
        RouterBox.Text = s.RouterTarget; ExternalBox.Text = s.ExternalTarget;
        ModeCombo.SelectedIndex = s.Mode == GamingMode.Off ? 1 : 0;
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
                ActiveNetworkProfileId = _settingsNetworkProfileId,
                AutoConnectLan = AutoConnectCheck.IsChecked == true,
                AutoUpdate = AutoUpdateCheck.IsChecked == true,
                AutoStart = AutoStartCheck.IsChecked == true, DriverEnabled = DriverCheck.IsChecked == true,
                Notifications = NotificationsCheck.IsChecked == true,
                UiSounds = UiSoundsCheck.IsChecked == true,
                UiAnimations = UiAnimationsCheck.IsChecked == true,
                PingAlarm = PingAlarmCheck.IsChecked == true,
                EarlyLimitGames = EarlyGamesBox.Text.Trim(),
                GameProcesses = ConfiguredProcessesText, DirectPeers = PeersBox.Text.Trim(),
                RouterTarget = RouterBox.Text.Trim(), ExternalTarget = ExternalBox.Text.Trim()
            };
            await _runtime.SaveSettingsAsync(next);
            _updates?.SetEnabled(next.AutoUpdate);
            ErrorText.Text = "Einstellungen gespeichert.";
        });
    }

    private void DeviceManager_Click(object sender, RoutedEventArgs e) => new DeviceManagerWindow(_runtime) { Owner = this }.ShowDialog();
    private void ReloadSettings_Click(object sender, RoutedEventArgs e) { LoadSettings(_runtime.Settings); ErrorText.Text = "Gespeicherte Einstellungen geladen."; }
    private void PeerConnectionTest_Click(object sender, RoutedEventArgs e) => new ConnectionTestWindow(_runtime) { Owner = this }.ShowDialog();
    private void UsageHistory_Click(object sender, RoutedEventArgs e) => _runtime.ShowUsageHistory(this);
    private void NetworkProfiles_Click(object sender, RoutedEventArgs e) => new NetworkProfilesWindow(_runtime) { Owner = this }.ShowDialog();

    private async void BetaUpdatesCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var requested = BetaUpdatesCheck.IsChecked == true;
        await RunUiAction(async () =>
        {
            await _runtime.SaveBetaUpdatePreferenceAsync(requested);
            _updates?.SetBetaChannel(requested);
            ErrorText.Text = requested ? "Vorabversionen werden beim nächsten Prüfen berücksichtigt." : "Nur stabile Versionen.";
        });
        // If saving fails, keep the displayed opt-in aligned with the persisted channel.
        _loading = true;
        BetaUpdatesCheck.IsChecked = _runtime.Settings.BetaUpdates;
        _loading = false;
        NotifyUpdateRestartSafety();
    }
    private async void AutoConnectCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _savingConnectionMode) return;
        var enabled = AutoConnectCheck.IsChecked == true;
        _savingConnectionMode = true;
        ConnectionModeControls.IsEnabled = false;
        try
        {
            await RunUiAction(async () =>
            {
                EnsureNetworkEditContext();
                await _runtime.SaveNetworkSettingsAsync(autoConnectLan: enabled);
                ErrorText.Text = enabled
                    ? "Automatische Erkennung gespeichert. DYNEX sucht und verbindet PCs im privaten Heimnetz."
                    : "Manuelle Gruppe gespeichert. Verbunden werden PCs mit demselben Gruppencode.";
            });
        }
        finally
        {
            AutoConnectCheck.IsChecked = _runtime.Settings.AutoConnectLan;
            ConnectionModeControls.IsEnabled = true;
            _savingConnectionMode = false;
            NotifyUpdateRestartSafety();
        }
    }
    private async void ExportSettings_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "dynex-einstellungen.json", Filter = "JSON-Datei (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        await File.WriteAllTextAsync(dialog.FileName, _runtime.ExportSettings());
        ErrorText.Text = "Einstellungen gesichert. Kopplungsschlüssel und Speedtest-Ergebnis sind nicht enthalten.";
    });
    private async void ImportSettings_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JSON-Datei (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        if (new FileInfo(dialog.FileName).Length > 256 * 1024) throw new ArgumentException("Die Sicherungsdatei ist zu groß.");
        await _runtime.ImportSettingsAsync(await File.ReadAllTextAsync(dialog.FileName));
        LoadSettings(_runtime.Settings);
        ErrorText.Text = "Einstellungen wiederhergestellt. Speedtest-Wert und PC-Name dieses PCs bleiben erhalten.";
    });

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
            NotifyUpdateRestartSafety();
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_updates is null) return;
        CheckUpdatesButton.IsEnabled = false;
        try { await _updates.CheckNowAsync(); }
        catch (Exception ex) { UpdateStatusText.Text = "Updateprüfung fehlgeschlagen: " + ex.Message; }
        finally
        {
            RenderUpdateStatus();
            (System.Windows.Application.Current as App)?.RetryReadyUpdateInstall();
        }
    }

    private void UpdateStatus_Changed() => Dispatcher.BeginInvoke(new Action(RenderUpdateStatus));

    private void RenderUpdateStatus()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(RenderUpdateStatus)); return; }
        InstalledVersionText.Text = $"Installierte Version: {DynexRuntime.AppVersion}";
        if (_updates is null)
        {
            UpdateStatusText.Text = _runtime.Settings.AutoUpdate
                ? "Updates sind vorgemerkt; die Updateprüfung ist in dieser Entwicklungsansicht nicht verfügbar."
                : "Automatische Updates sind ausgeschaltet.";
            CheckUpdatesButton.IsEnabled = false;
            return;
        }
        var status = _updates.Status;
        UpdateStatusText.Text = status.UpdateReady && HasUnsavedSettings
            ? $"Version {status.AvailableVersion} ist bereit. Bitte deine geänderten Einstellungen zuerst speichern; danach startet DYNEX das Update automatisch."
            : status.UpdateReady && _runtime.IsSpeedTestRunning
                ? $"Version {status.AvailableVersion} ist bereit. Die Installation startet nach dem laufenden Speedtest."
                : status.Message;
        UpdateDownloadProgress.Visibility = status.IsBusy && status.DownloadedBytes.HasValue ? Visibility.Visible : Visibility.Collapsed;
        UpdateDownloadProgress.Value = status.TotalBytes is > 0
            ? Math.Clamp(100d * (status.DownloadedBytes ?? 0) / status.TotalBytes.Value, 0, 100) : 0;
        CheckUpdatesButton.IsEnabled = !status.IsBusy && !_updateRestartInProgress;
    }

    public bool IsUpdateRestartSafe => !_allowClose && !_loading && !_savingAutoUpdate && !_settingUp &&
        !_checkingNetwork && !_refreshingStatus && !_updateRestartInProgress && _pendingUiActions == 0 &&
        !_runtime.IsSpeedTestRunning && _runtime.Settings.AutoUpdate && AutoUpdateCheck.IsChecked == true && !HasUnsavedSettings;

    private bool HasUnsavedSettings
    {
        get
        {
            var saved = _runtime.Settings;
            return AutoConnectCheck.IsChecked != saved.AutoConnectLan || AutoStartCheck.IsChecked != saved.AutoStart ||
                DriverCheck.IsChecked != saved.DriverEnabled || NotificationsCheck.IsChecked != saved.Notifications ||
                PingAlarmCheck.IsChecked != saved.PingAlarm ||
                UiSoundsCheck.IsChecked != saved.UiSounds || UiAnimationsCheck.IsChecked != saved.UiAnimations ||
                AutoUpdateCheck.IsChecked != saved.AutoUpdate ||
                BetaUpdatesCheck.IsChecked != saved.BetaUpdates ||
                EarlyGamesBox.Text.Trim() != saved.EarlyLimitGames ||
                !_configuredProcesses.SequenceEqual(SettingsStore.Split(saved.GameProcesses)) ||
                PeersBox.Text.Trim() != saved.DirectPeers || RouterBox.Text.Trim() != saved.RouterTarget ||
                ExternalBox.Text.Trim() != saved.ExternalTarget;
        }
    }

    private void NotifyUpdateRestartSafety()
    {
        if (_loading) return;
        RenderUpdateStatus();
        UpdateRestartSafetyChanged?.Invoke();
    }

    private async void RunningProcesses_DropDownOpened(object sender, EventArgs e) => await RefreshRunningProcessesAsync();
    private async void RefreshProcesses_Click(object sender, RoutedEventArgs e) => await RefreshRunningProcessesAsync();
    private async void ProcessSearch_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (!_processesLoaded) await RefreshRunningProcessesAsync();
    }
    private void ProcessSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && RunningProcessesBox is not null) ApplyRunningProcessFilter();
    }
    private void ApplyRunningProcessFilter(string? selected = null)
    {
        selected ??= RunningProcessesBox.SelectedItem as string;
        var query = ProcessSearchBox.Text.Trim();
        var matches = _runningProcessNames.Where(name => name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        RunningProcessesBox.ItemsSource = matches;
        RunningProcessesBox.SelectedItem = matches.FirstOrDefault(name => string.Equals(name, selected, StringComparison.OrdinalIgnoreCase));
        AddProcessButton.IsEnabled = !_refreshingProcesses && RunningProcessesBox.SelectedItem is string;
        ProcessPickerHint.Text = _processLoadError is { } error ? "Programmliste konnte nicht geladen werden: " + error
            : _refreshingProcesses ? "Laufende Programme werden geladen …"
            : !_processesLoaded ? "Suche oder Auswahl öffnen, um laufende Programme zu laden."
            : _runningProcessNames.Count == 0 ? "Keine auswählbaren Programme gefunden. Spiel starten und Liste aktualisieren."
            : matches.Length == 0 ? "Kein laufendes Programm passt zur Suche. Suchbegriff ändern oder Liste aktualisieren."
            : query.Length > 0 ? $"{matches.Length} von {_runningProcessNames.Count} Programmen · auswählen und hinzufügen."
            : $"{matches.Length} laufende Programme · auswählen und hinzufügen. Bekannte Launcher und Hilfsprozesse sind ausgeblendet.";
    }
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
            _runningProcessNames = names;
            _processesLoaded = true;
            _processLoadError = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _runningProcessNames = Array.Empty<string>();
            _processesLoaded = false;
            _processLoadError = ex.Message;
        }
        finally
        {
            _refreshingProcesses = false;
            RefreshProcessesButton.IsEnabled = true;
            if (!_allowClose) ApplyRunningProcessFilter(selected);
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
            var existing = ConfiguredProcessesText;
            var updated = GameDetector.AppendConfiguredProcess(existing, selected);
            var added = updated != existing;
            if (added) _configuredProcesses.Add(SettingsStore.Split(updated)[^1]);
            ProcessPickerHint.Text = added
                ? $"{selected} hinzugefügt. Mit „Einstellungen speichern“ übernehmen."
                : $"{selected} ist bereits eingetragen.";
        }
        catch (ArgumentException ex) { ProcessPickerHint.Text = ex.Message; }
    }

    private string ConfiguredProcessesText => string.Join("; ", _configuredProcesses);
    private void RemoveProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string name } && _configuredProcesses.Remove(name))
            ProcessPickerHint.Text = $"{name} entfernt. Mit „Einstellungen speichern“ übernehmen.";
    }

    private async void SpeedTest_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        if (_runtime.IsSpeedTestWaiting && _runtime.SpeedTestWaitingFor == SpeedTestWaitReason.Network)
        {
            ShowSettings("network");
            return;
        }
        await _runtime.MeasureCapacityAsync();
        RenderCapacity();
    });

    private void RenderCapacity()
    {
        var settings = _runtime.Settings;
        var running = _runtime.IsSpeedTestRunning;
        var waiting = _runtime.IsSpeedTestWaiting;
        var snapshot = _runtime.Snapshot;
        var fromPeer = snapshot?.CapacityFromPeer == true;
        var capacity = snapshot?.SharedCapacityMbps ?? (settings.CapacityMeasuredAtUtc.HasValue ? settings.CapacityMbps : (double?)null);
        CapacityBox.Text = capacity?.ToString("0.##", CultureInfo.CurrentCulture) ?? "—";
        CapacityBox.ToolTip = fromPeer ? "Frische Leitungskapazität von verbundenem PC; keine eigene Messung."
            : snapshot?.ParticipantCount > 1 ? "Konservative gemeinsame Leitung aus den Kapazitäten verbundener PCs."
            : "Eigene erfolgreiche Speedtest-Messung";
        CapacityBox.Opacity = capacity.HasValue ? 1 : 0.6;
        CapacityMeasurementText.Text = capacity.HasValue ? $"{capacity:0.##} Mbit/s" : "Noch nicht gemessen";
        SpeedTestButton.IsEnabled = !running && (!waiting || _runtime.SpeedTestWaitingFor == SpeedTestWaitReason.Network);
        SpeedTestButton.Content = running ? "Messung läuft …" : waiting ? _runtime.SpeedTestWaitingFor switch
        {
            SpeedTestWaitReason.Network => "Netzwerk prüfen",
            SpeedTestWaitReason.OpenGame => "Spiel noch geöffnet",
            SpeedTestWaitReason.PeerTraffic => "Wartet auf freie Leitung",
            _ => "Prüfung läuft …"
        } : settings.CapacityMeasuredAtUtc.HasValue ? "Erneut messen" : "Speedtest starten";
        SpeedTestProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        var measured = settings.CapacityMeasuredAtUtc is { } at
            ? $"Gemessen am {at.ToLocalTime():dd.MM.yyyy HH:mm}" +
              ". "
            : "Noch nicht gemessen. ";
        if (fromPeer) measured = "Leitungswert eines verbundenen PCs; keine eigene Messung. ";
        var showWaiting = waiting && !fromPeer;
        var detail = running || showWaiting ? _runtime.SpeedTestMessage : measured +
            (string.IsNullOrEmpty(_runtime.SpeedTestMessage) ? "Manuelle Diagnose; der Messwert steuert keine Priorisierung." : _runtime.SpeedTestMessage);
        SpeedTestHint.Text = running || showWaiting ? _runtime.SpeedTestMessage : measured +
            (string.IsNullOrEmpty(_runtime.SpeedTestMessage) ? "Manuelle Diagnose; der Messwert steuert keine Priorisierung." : _runtime.SpeedTestMessage);
        if (!running && !waiting && Dynex.Core.Bufferbloat.Rate(settings.IdleLatencyMs, settings.LoadedLatencyMs) is { } bloat)
        {
            SpeedTestHint.Text += "\n" + bloat.Summary;
            detail += "\n" + bloat.Summary + (bloat.Recommendation is { } tip ? "\n" + tip : "");
        }
        SpeedTestHint.ToolTip = detail;
    }

    private async void GuideDone_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        await _runtime.CompleteSetupGuideAsync();
        GuidePanel.Visibility = Visibility.Collapsed;
    });
    private void RenderGuide(RuntimeSnapshot s)
    {
        if (!_guideAllowed || _runtime.Settings.SetupGuideDone) { GuidePanel.Visibility = Visibility.Collapsed; return; }
        GuidePanel.Visibility = Visibility.Visible;
        var privateOk = s.NetworkProblem is null &&
            s.Connection?.Kind is not (Dynex.Core.ConnectionProblemKind.NoPrivateNetwork or Dynex.Core.ConnectionProblemKind.FirewallMissing);
        var peersOk = s.Devices.Count > 1;
        GuideNetworkText.Text = privateOk ? "✓ 1 · Heimnetz ist privat und freigegeben." : "✗ 1 · " + (s.NetworkProblem ?? s.Connection?.ToString() ?? "Netzwerk prüfen.");
        GuidePeerText.Text = peersOk ? $"✓ 2 · {s.Devices.Count - 1} weitere(r) PC verbunden." : "… 2 · Noch kein anderer PC gefunden. DYNEX auf dem zweiten PC starten (bei nur einem PC überspringen).";
        GuideSpeedText.Text = "✓ 3 · Speedtest ist optional und dient nur der Diagnose.";
        GuideDoneButton.Content = privateOk ? "Fertig" : "Später";
    }
    private async void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModeCombo.SelectedItem is not ComboBoxItem item || Enum.TryParse<GamingMode>(item.Tag?.ToString(), out var mode) is false) return;
        await RunUiAction(async () => await _runtime.SaveSettingsAsync(_runtime.Settings with { Mode = mode }));
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshingStatus || _updateRestartInProgress || _allowClose) return;
        _refreshingStatus = true;
        RefreshStatusButton.IsEnabled = false;
        RefreshStatusButton.Content = "Wird aktualisiert …";
        StatusRefreshText.Text = "Aktuelle Messwerte werden abgefragt …";
        StatusRefreshText.Visibility = Visibility.Visible;
        try
        {
            await RunUiAction(async () =>
            {
                try
                {
                    var snapshot = await _runtime.RefreshStatusAsync();
                    RenderSnapshot(snapshot);
                    StatusRefreshText.Text = $"Status aktualisiert um {DateTime.Now:HH:mm:ss} Uhr.";
                }
                catch (Exception ex)
                {
                    StatusRefreshText.Text = ex is OperationCanceledException
                        ? "Aktualisierung unterbrochen. Bitte erneut aktualisieren."
                        : $"Status konnte nicht aktualisiert werden: {ex.Message}";
                    throw;
                }
            });
        }
        finally
        {
            _refreshingStatus = false;
            RefreshStatusButton.IsEnabled = true;
            RefreshStatusButton.Content = "Status aktualisieren";
            NotifyUpdateRestartSafety();
        }
    }
    private async void Setup_Click(object sender, RoutedEventArgs e)
    {
        if (_settingUp) return;
        _settingUp = true;
        SetupButton.IsEnabled = false;
        try
        {
            await RunUiAction(async () =>
            {
                await _runtime.StopAsync();
                SetupHint.Text = "Windows richtet die Netzwerkpriorisierung und Heimnetzverbindung ein …";
                var result = await InstallerPrerequisites.RunAsync();
                if (result.ExitCode == 3010)
                {
                    ErrorText.Text = result.Message;
                    return;
                }
                if (result.ExitCode != 0) throw new InvalidOperationException(result.Message);
                await _runtime.SaveSettingsAsync(_runtime.Settings with { DriverEnabled = true });
                DriverCheck.IsChecked = true;
                ErrorText.Text = "Einrichtung abgeschlossen. DYNEX aktiviert den Netzwerkschutz automatisch beim Spielen.";
            });
        }
        finally
        {
            _settingUp = false; SetupButton.IsEnabled = true;
            try { await _runtime.StartAsync(); }
            catch (Exception error) { ErrorText.Text += " DYNEX konnte nicht neu starten: " + error.Message; }
            NotifyUpdateRestartSafety();
        }
    }
    private async void RevealCode_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        EnsureNetworkEditContext();
        if (!_revealed)
        {
            PairingBox.Text = await _runtime.ActivatePairingCodeAsync();
            AutoConnectCheck.IsChecked = _runtime.Settings.AutoConnectLan;
            ErrorText.Text = "Manuelle Gruppe aktiviert. Diesen Code auf dem anderen PC importieren.";
        }
        else PairingBox.Clear();
        _revealed = !_revealed; ((System.Windows.Controls.Button)sender).Content = _revealed ? "Verbergen" : "Anzeigen";
    });
    private async void CopyCode_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        EnsureNetworkEditContext();
        var code = await _runtime.ActivatePairingCodeAsync();
        AutoConnectCheck.IsChecked = _runtime.Settings.AutoConnectLan;
        Forms.Clipboard.SetText(code); ErrorText.Text = "Manuelle Gruppe aktiviert und Code kopiert. Auf dem anderen PC importieren.";
        if (_revealed) PairingBox.Text = code;
    });
    private async void PasteCode_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        if (!Forms.Clipboard.ContainsText()) throw new InvalidOperationException("Die Zwischenablage enthält keinen Text.");
        PairingBox.Text = Forms.Clipboard.GetText(); _revealed = true;
        await Task.CompletedTask;
    });
    private async void ImportCode_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        EnsureNetworkEditContext();
        if (string.IsNullOrWhiteSpace(PairingBox.Text)) throw new InvalidOperationException("Bitte zuerst einen Gruppencode einfügen.");
        await _runtime.ImportPairingCodeAsync(PairingBox.Text.Trim());
        PairingBox.Clear(); _revealed = false;
        AutoConnectCheck.IsChecked = _runtime.Settings.AutoConnectLan;
        PeersBox.Text = _runtime.Settings.DirectPeers;
        ErrorText.Text = "Manuelle Gruppe aktiviert. DYNEX sucht jetzt die PCs mit demselben Code.";
    });
    private async void Diagnostics_Click(object sender, RoutedEventArgs e) => await RunUiAction(async () =>
    {
        var report = await _runtime.ExportDiagnosticsAsync();
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "dynex-diagnose.json", Filter = "JSON-Datei (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true) File.Copy(report, dialog.FileName, true);
    });

    private async Task RunUiAction(Func<Task> action)
    {
        if (_updateRestartInProgress || _allowClose) return;
        _pendingUiActions++;
        try { ErrorText.Text = ""; ErrorText.ToolTip = null; await action(); }
        catch (Exception ex) { ErrorText.Text = $"Aktion fehlgeschlagen: {ex.Message}"; }
        finally { _pendingUiActions--; NotifyUpdateRestartSafety(); }
    }

    private void EnsureNetworkEditContext()
    {
        if (_settingsNetworkProfileId != _runtime.Settings.ActiveNetworkProfileId)
            throw new InvalidOperationException("Das Netzwerk hat gewechselt. Mit ‚Neu laden‘ die passenden Einstellungen laden, bevor du sie änderst.");
    }

    private async void NetworkHelp_Click(object sender, RoutedEventArgs e)
    {
        ShowSettings("network");
        if (_checkingNetwork || _savingConnectionMode || _updateRestartInProgress || _allowClose) return;
        _checkingNetwork = true;
        CheckConnectionButton.IsEnabled = NetworkHelpButton.IsEnabled = false;
        CheckConnectionButton.Content = "Prüfung läuft …";
        NetworkCheckStatusText.Text = "Heimnetz, Firewallfreigabe und PC-Verbindung werden geprüft …";
        NetworkCheckStatusText.Visibility = Visibility.Visible;
        try
        {
            await RunUiAction(async () =>
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                EnsureNetworkEditContext();
                await _runtime.SaveNetworkSettingsAsync(directPeers: PeersBox.Text.Trim());
                    var result = await _runtime.CheckNetworkAsync(deadline.Token, requestDiscovery: true);
                    var snapshot = await _runtime.RefreshStatusAsync(deadline.Token);
                    RenderSnapshot(snapshot);
                    NetworkCheckStatusText.Text = $"Geprüft um {DateTime.Now:HH:mm:ss} Uhr. {result.Summary}";
                }
                catch (Exception ex)
                {
                    NetworkCheckStatusText.Text = ex is OperationCanceledException
                        ? "Verbindungsprüfung unterbrochen oder Zeitlimit erreicht. Bitte erneut prüfen."
                        : $"Verbindungsprüfung fehlgeschlagen: {ex.Message}";
                    throw;
                }
            });
        }
        finally
        {
            _checkingNetwork = false;
            CheckConnectionButton.IsEnabled = NetworkHelpButton.IsEnabled = true;
            CheckConnectionButton.Content = "Verbindung prüfen";
            NotifyUpdateRestartSafety();
        }
    }
    private async void WindowsNetwork_Click(object sender, RoutedEventArgs e) => await RunUiAction(() =>
    {
        Process.Start(new ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true });
        return Task.CompletedTask;
    });

    private void RenderSnapshot(RuntimeSnapshot s)
    {
        UpdateTray(s);
        RenderCapacity();
        HeaderState.Text = s.State.ToUpperInvariant(); DetailText.Text = s.Detail;
        GameText.Text = s.WaitingForGameTraffic && s.ObservingGameTraffic ? $"Kein Spielverkehr · {s.Game}"
            : s.WaitingForGameTraffic ? $"Spielverkehr wird geprüft · {s.Game}"
            : s.IsGaming ? $"Spiel nutzt Netzwerk · {s.Game}"
            : _runtime.Settings.Mode == GamingMode.Off ? "Fokus-Erkennung ausgeschaltet"
            : !string.IsNullOrEmpty(s.Game) ? $"Im Hintergrund · {s.Game}" : "Kein Spiel erkannt";
        DownText.Text = s.DownloadMbps is { } down ? $"{down:0.0} Mbps" : "— Mbps";
        UpText.Text = s.UploadMbps is { } up ? $"{up:0.0} Mbps" : "— Mbps";
        UpCaption.Text = "Gesamter Versand";
        UpCaption.ToolTip = null;
        if (s.HomeTraffic is { } home)
        {
            // NIC counters include LAN traffic, so the remainder is only an estimate.
            var dynex = s.Devices.Sum(d => d.DownloadMbps ?? 0);
            var others = Math.Max(0, home.DownloadMbps - dynex);
            DownCaption.Text = $"Ganzes Heimnetz: {home.DownloadMbps:0.0} Mbps";
            DownCaption.ToolTip = $"Router {home.RouterName}: ↓ {home.DownloadMbps:0.0} / ↑ {home.UploadMbps:0.0} Mbit/s\nGeräte ohne DYNEX (Konsole, Handy, TV …) ca. {others:0.0} Mbit/s\nGelesen über UPnP; DYNEX steuert diese Geräte nicht.";
        }
        else { DownCaption.Text = "Gesamter Empfang"; DownCaption.ToolTip = "Router-Werte erscheinen, wenn der Router UPnP-Statusinformationen anbietet."; }
        PriorityOverviewText.Text = s.QosActive ? "PRIORITÄT AKTIV" : s.ObservingGameTraffic ? "SPIEL WIRD BEOBACHTET" : "BEREIT";
        PriorityDetailText.Text = s.QosActive
            ? $"Spiel: {s.GamePriorityPackets:N0} · Sprache: {s.VoicePriorityPackets:N0} · Video: {s.VideoPriorityPackets:N0}"
            : "DYNEX markiert erkannte Pakete; Router-QoS/SQM steuert deren Behandlung im Heimnetz.";
        var installed = PacketFilterSetup.IsInstalled && PrioritizationServiceSetup.IsConfigured();
        var observingWithoutGameTraffic = s.ObservingGameTraffic && !s.IsGaming && !s.QosActive;
        DriverText.Text = s.QosActive ? "PRIORITÄT AKTIV" : observingWithoutGameTraffic ? "BEOBACHTET" : !s.DriverAvailable ? "PAKET UNVOLLSTÄNDIG" : !installed ? "EINMAL EINRICHTEN" : !_runtime.Settings.DriverEnabled ? "AUS" : s.ProtectionIdle ? "INAKTIV" : "NICHT BESTÄTIGT";
        DriverDetail.Text = s.QosActive ? "Spiel-, Sprach- und erkannte Videopakete werden für QoS markiert." : observingWithoutGameTraffic ? "Spielverkehr wird beobachtet; noch keine priorisierten Pakete bestätigt." : s.DriverAvailable ? s.DriverState : "DYNEX bitte vollständig installieren.";
        DriverDetail.ToolTip = s.DriverState;
        ProtectionStatusText.Text = s.QosActive ? "NETZWERKSCHUTZ · PRIORISIERUNG AKTIV"
            : observingWithoutGameTraffic ? "NETZWERKSCHUTZ · SPIELVERKEHR WIRD BEOBACHTET"
            : s.ProtectionIdle ? "NETZWERKSCHUTZ · INAKTIV" : "NETZWERKSCHUTZ · NOCH NICHT AKTIV";
        if (s.QosActive)
        {
            QosStatusText.Text = "Priorisierung aktiv";
            QosDetailText.Text = $"Spiele: {s.GamePriorityPackets:N0} · Sprache: {s.VoicePriorityPackets:N0} · Video: {s.VideoPriorityPackets:N0}";
        }
        else if (observingWithoutGameTraffic)
        {
            QosStatusText.Text = "BEOBACHTET";
            QosDetailText.Text = "Spielverkehr wird beobachtet; Priorisierung beginnt bei erkanntem Verkehr.";
        }
        else if (s.ProtectionIdle)
        {
            QosStatusText.Text = "Netzwerkfilter inaktiv";
            QosDetailText.Text = "Paketpriorisierung ausgeschaltet oder für die Diagnose pausiert.";
        }
        else if (s.GamePriorityPackets > 0 || s.VoicePriorityPackets > 0 || s.VideoPriorityPackets > 0)
        {
            QosStatusText.Text = "QoS nicht bestätigt";
            QosDetailText.Text = $"Zuletzt: Spiel {s.GamePriorityPackets:N0} · Sprache {s.VoicePriorityPackets:N0} · Video {s.VideoPriorityPackets:N0}";
        }
        else
        {
            QosStatusText.Text = "Priorisierung noch nicht aktiv";
            QosDetailText.Text = _runtime.Settings.DriverEnabled ? "Der Netzwerkfilter hat die Priorisierung noch nicht bestätigt." : "In den Einstellungen einschalten: Spiele und Sprache zuerst, erkannte Videos danach.";
        }
        QosDetailText.ToolTip = "DYNEX markiert erkannte Spiel-, Sprach- und Videopakete für QoS. Der Router muss diese Markierungen unterstützen. Paket-Zähler belegen keine Behandlung durch den Router.";
        DriverCheck.IsEnabled = s.DriverAvailable && installed && !_settingUp;
        SetupButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        SetupButton.IsEnabled = s.DriverAvailable && !_settingUp;
        if (!_settingUp) SetupHint.Text = !s.DriverAvailable ? "Die Netzwerkkomponente fehlt. DYNEX bitte vollständig installieren." : installed
            ? "DYNEX markiert bestätigten Spiel- und Sprachverkehr. Router-QoS entscheidet über die weitere Behandlung."
            : "Einmal einrichten und die Windows-Rückfrage bestätigen. Danach startet die Priorisierung ohne weitere Rückfragen; die Verbindung kann bei der Einrichtung kurz unterbrochen werden.";
        SetPing(s.Router, RouterPing, RouterStats, RouterTarget); SetPing(s.External, ExternalPing, ExternalStats, ExternalTarget);
        RenderAnalysis(s);
        _deviceSnapshot = s.Devices;
        RenderDevicePage();
        OverviewDeviceGrid.ItemsSource = _deviceSnapshot;
        OverviewDeviceCount.Text = $"{s.Devices.Count} {(s.Devices.Count == 1 ? "GERÄT" : "GERÄTE")}";
        OverviewDevicesEmpty.Visibility = s.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeviceCount.Text = $"{s.Devices.Count} GERÄTE"; NetworkStatus.Text = s.NetworkStatus;
        var otherDevices = Math.Max(0, s.Devices.Count - 1);
        DashboardPeersText.Text = otherDevices == 0
            ? "Hier siehst du dein Gerät. Weitere verbundene Geräte erscheinen direkt in dieser Übersicht."
            : $"{otherDevices} weitere {(otherDevices == 1 ? "Gerät ist" : "Geräte sind")} hier live sichtbar. Details findest du in der Geräteansicht.";
        NetworkStatus.ToolTip = s.NetworkStatus;
        NetworkHelpButton.Visibility = s.NetworkProblem is not null || s.Devices.Count < 2 || s.Connection is { IsProblem: true } ? Visibility.Visible : Visibility.Collapsed;
        NetworkHelpText.Text = (s.NetworkProblem is { } problem && s.Connection is { IsProblem: true } step ? $"{problem} Nächster Schritt: {step.NextStep}" : s.NetworkProblem)
            ?? s.Connection?.ToString() ?? s.NetworkDiagnostics?.Hint ?? (s.Devices.Count < 2
            ? "Noch kein weiteres Gerät verbunden. DYNEX muss auf den beteiligten Geräten laufen und Windows-Netzwerke auf Privat stehen. Die DYNEX-Firewallfreigabe richtet das Setup ein."
            : $"{s.Devices.Count} Geräte verbunden. Die Verbindung wird automatisch überwacht.");
        NetworkHelpText.ToolTip = s.NetworkDiagnostics is { } network
            ? $"{NetworkHelpText.Text}\nModus: {network.Mode}\nLokale Adressen: {string.Join(", ", network.LocalAddresses)}\nGesendet: {network.SentDatagrams} · Empfangen: {network.ReceivedDatagrams} (davon eigene: {network.SelfDatagrams}) · Bestätigte Statuspakete: {network.AcceptedStatusDatagrams}\nVollständige Werte: Diagnose exportieren."
            : NetworkHelpText.Text;
        DevicePriorityText.Text = s.QosActive
            ? "DYNEX bestätigt QoS-Markierungen auf diesem PC."
            : "DYNEX markiert lokal erkannte Spiel-, Sprach- und Videopakete für QoS.";
        CoordinationText.Text = "DYNEX markiert Pakete auf PCs, auf denen es läuft. Router-QoS oder SQM bestimmt die Behandlung im gesamten Anschluss.";
        PairFingerprint.Text = string.IsNullOrWhiteSpace(s.PairingFingerprint) ? "Noch keine Gruppe gekoppelt" : $"Gruppe · {s.PairingFingerprint}";
        if (s.NetworkProblem is not null)
        {
            CoordinationText.Text += " Geräteverbindung: " + s.NetworkProblem;
        }
        // A lone PC without any observed partner is normal; surface only causes DYNEX actually observed.
        else if (s.Connection is { IsProblem: true, Kind: not Dynex.Core.ConnectionProblemKind.NoResponse } advice)
            CoordinationText.Text += " Geräteverbindung: " + advice;
        DrawHistory(s.History);
        HistoryCompare.Text = "Pingmessungen der letzten Minuten.";
        HistoryCompare.ToolTip = "Pingmessungen am Router und im Internet. Die Anzeige belegt keine Priorität durch deinen Router.";
    }

    private void RenderDevicePage()
    {
        const int pageSize = 8;
        var pageCount = Math.Max(1, (int)Math.Ceiling(_deviceSnapshot.Count / (double)pageSize));
        _devicePage = Math.Clamp(_devicePage, 0, pageCount - 1);
        DeviceGrid.ItemsSource = _deviceSnapshot.Skip(_devicePage * pageSize).Take(pageSize).ToArray();
        DevicesPageText.Text = $"Seite {_devicePage + 1} / {pageCount}";
        PreviousDevicesButton.IsEnabled = _devicePage > 0;
        NextDevicesButton.IsEnabled = _devicePage + 1 < pageCount;
    }

    private void PreviousDevices_Click(object sender, RoutedEventArgs e)
    {
        _devicePage = Math.Max(0, _devicePage - 1);
        RenderDevicePage();
    }

    private void NextDevices_Click(object sender, RoutedEventArgs e)
    {
        _devicePage++;
        RenderDevicePage();
    }

    internal bool IsOverviewVisible => OverviewPage.Visibility == Visibility.Visible;

    internal void ShowOverview()
    {
        var changed = OverviewPage.Visibility != Visibility.Visible;
        OverviewPage.Visibility = Visibility.Visible;
        SettingsPage.Visibility = Visibility.Collapsed;
        DevicesPage.Visibility = Visibility.Collapsed;
        UpdateNavigation("overview");
        PageTitle.Text = "Übersicht";
        if (changed && IsLoaded) _feedback.Transition(OverviewPage);
    }

    internal void ShowDevices()
    {
        var changed = DevicesPage.Visibility != Visibility.Visible;
        OverviewPage.Visibility = SettingsPage.Visibility = Visibility.Collapsed;
        DevicesPage.Visibility = Visibility.Visible;
        UpdateNavigation("devices");
        PageTitle.Text = "Verbundene Geräte";
        RenderDevicePage();
        if (changed && IsLoaded) _feedback.Transition(DevicesPage);
    }

    private void UpdateNavigation(string page)
    {
        SetNavigationState(OverviewNavButton, page == "overview");
        SetNavigationState(DevicesNavButton, page == "devices");
        SetNavigationState(AnalysisNavButton, page == "analysis");
        SetNavigationState(SettingsNavButton, page == "settings");
        SetNavigationState(SidebarSettingsButton, page == "settings");
        SettingsSectionsNav.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        SettingsMenuLabel.Visibility = page == "settings" && SidebarColumn.Width.Value > 100 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetNavigationState(System.Windows.Controls.Button button, bool selected)
    {
        button.Background = (System.Windows.Media.Brush)FindResource(selected ? "SelectedSurface" : "Bg");
        button.BorderBrush = selected ? (System.Windows.Media.Brush)FindResource("Accent") : System.Windows.Media.Brushes.Transparent;
        button.Foreground = selected ? System.Windows.Media.Brushes.White : (System.Windows.Media.Brush)FindResource("Muted");
    }

    private void FeedbackPreference_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyFeedbackPreferences();
        NotifyUpdateRestartSafety();
    }

    private void ApplyFeedbackPreferences()
    {
        _feedback.SoundsEnabled = UiSoundsCheck.IsChecked == true;
        _feedback.AnimationsEnabled = UiAnimationsCheck.IsChecked == true && DesktopLifecycle.ProfilePath is null;
    }

    internal void ShowSettings(string section = "general")
    {
        section = section.Trim().ToLowerInvariant();
        if (section is not ("general" or "games" or "network" or "analysis" or "updates" or "appearance"))
            throw new ArgumentException("Unbekannter Einstellungsbereich.", nameof(section));
        var changed = SettingsPage.Visibility != Visibility.Visible || !Equals(SettingsPage.Tag, section);
        SettingsPage.Tag = section;
        if (section != "analysis") _selectedSettingsSection = section;
        OverviewPage.Visibility = Visibility.Collapsed;
        DevicesPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
        UpdateNavigation(section == "analysis" ? "analysis" : "settings");
        GeneralSettingsSection.Visibility = Visibility.Collapsed;
        GamesSettingsSection.Visibility = Visibility.Collapsed;
        NetworkSettingsSection.Visibility = Visibility.Collapsed;
        UpdatesSettingsSection.Visibility = Visibility.Collapsed;
        AnalysisSettingsSection.Visibility = Visibility.Collapsed;
        AppearanceSettingsSection.Visibility = Visibility.Collapsed;
        foreach (var button in SettingsSectionsNav.Children.OfType<System.Windows.Controls.Button>())
            SetNavigationState(button, Equals(button.Tag, section));
        switch (section)
        {
            case "general": GeneralSettingsSection.Visibility = Visibility.Visible; break;
            case "games": GamesSettingsSection.Visibility = Visibility.Visible; break;
            case "network": NetworkSettingsSection.Visibility = Visibility.Visible; break;
            case "updates": UpdatesSettingsSection.Visibility = Visibility.Visible; break;
            case "analysis": AnalysisSettingsSection.Visibility = Visibility.Visible; RenderAnalysis(_runtime.Snapshot); break;
            case "appearance": AppearanceSettingsSection.Visibility = Visibility.Visible; break;
            default: throw new ArgumentException("Unbekannter Einstellungsbereich.", nameof(section));
        }
        (SettingsHeading.Text, SettingsDescription.Text) = section switch
        {
            "games" => ("Spiele", "Erkennung, laufende Programme und Netzwerkverkehr."),
            "network" => ("Netzwerk", "Deine PCs verbinden und die Verbindung prüfen."),
            "analysis" => ("Analyse", "Verlauf, Störungen und Hinweise zu deinem Router."),
            "updates" => ("Updates", "Installierte Version und automatische Aktualisierungen."),
            "appearance" => ("Darstellung", "Klick-Sounds und Bewegung nach deinem Geschmack."),
            _ => ("Allgemein", "Internetanschluss und Spiel-Priorisierung.")
        };
        PageTitle.Text = section == "analysis" ? "Analyse" : $"Einstellungen · {SettingsHeading.Text}";
        SettingsActionBar.Visibility = section == "analysis" ? Visibility.Collapsed : Visibility.Visible;
        if (changed && IsLoaded) _feedback.Transition(SettingsPage);
    }

    private void OverviewNav_Click(object sender, RoutedEventArgs e) => ShowOverview();
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => ShowSettings(_selectedSettingsSection);
    private void DevicesNav_Click(object sender, RoutedEventArgs e) => ShowDevices();
    private void SettingsSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string section }) ShowSettings(section);
    }
    private static void SetPing(PingView ping, TextBlock current, TextBlock stats, TextBlock target)
    {
        current.Text = ping.Current is { } ms ? $"{ms:0.0} ms" : "— ms";
        stats.Text = $"Schwankung {Fmt(ping.Jitter)}\nFehlende Antworten {Fmt(ping.Loss, "%")}";
        target.Text = string.IsNullOrWhiteSpace(ping.Target) ? "Ziel nicht konfiguriert" : ping.Target;
    }
    private static string Fmt(double? v, string suffix = " ms") => v is { } n ? $"{n:0.0}{suffix}" : "—";
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
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var compactDashboard = height < 800;
        var expandedNavigation = width >= 1200;
        SidebarColumn.Width = new GridLength(expandedNavigation ? 196 : 68);
        var scale = Math.Clamp(Math.Min(width / 1440, height / 940), 1, 1.33);
        var resources = System.Windows.Application.Current.Resources;
        resources["NavigationLabelVisibility"] = expandedNavigation ? Visibility.Visible : Visibility.Collapsed;
        SettingsMenuLabel.Visibility = SettingsSectionsNav.Visibility == Visibility.Visible && expandedNavigation ? Visibility.Visible : Visibility.Collapsed;
        resources["UiSmallFontSize"] = 12 * scale;
        resources["UiBodyFontSize"] = 14 * scale;
        resources["UiSectionFontSize"] = 16 * scale;
        resources["UiMetricFontSize"] = 22 * scale;
        resources["UiHeroFontSize"] = 32 * scale;
        // Keep headers and live values readable as the rest of the dashboard grows.
        // The device-name column keeps the remaining space and its existing minimum.
        var columnWeights = new[] { 2.2, 1.35, 1.3, 1.5, 1.35, 1.35, 1.15 };
        var columnMinimums = new[] { 100d, 84, 70, 84, 100, 96, 74 };
        for (var column = 0; column < DeviceGrid.Columns.Count; column++)
        {
            DeviceGrid.Columns[column].MinWidth = columnMinimums[column];
            DeviceGrid.Columns[column].Width = new DataGridLength(columnWeights[column], DataGridLengthUnitType.Star);
        }
        StatusGrid.Columns = 4;
        MetricsRow.Height = new GridLength((compactDashboard ? 100 : 116) * scale);
        DeviceGrid.RowHeight = 38 * scale;
        DeviceGrid.ColumnHeaderHeight = 34 * scale;
        OverviewDevicesRow.Height = new GridLength((compactDashboard ? 158 : 190) * scale);
        OverviewDeviceGrid.RowHeight = 27 * scale;
        OverviewDeviceGrid.ColumnHeaderHeight = 25 * scale;
        DashboardHomePanel.Visibility = compactDashboard ? Visibility.Collapsed : Visibility.Visible;
        ChartPanel.Padding = compactDashboard ? new Thickness(18, 8, 18, 8) : new Thickness(18, 16, 18, 16);
        HistoryHoverText.Margin = compactDashboard ? new Thickness(0, 1, 0, 2) : new Thickness(0, 3, 0, 4);
        HistoryCompare.Margin = compactDashboard ? new Thickness(0, 2, 0, 0) : new Thickness(0, 5, 0, 0);
        PingPanel.Padding = compactDashboard ? new Thickness(18, 5, 18, 5) : new Thickness(18, 12, 18, 12);
        PingLayout.ColumnDefinitions[0].Width = new GridLength(1.9, GridUnitType.Star);
        PingLayout.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        DrawHistory(_lastHistory);
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
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
        if (_updateRestartInProgress) return;
        await ExitCoreAsync();
    }

    public async Task<bool> RequestUpdateRestartAsync(Func<bool> launchInstaller)
    {
        if (!IsUpdateRestartSafe) return false;
        _updateRestartInProgress = true;
        IsEnabled = false;
        try
        {
            // The setup must never copy files while our packet helper is still attached.
            await _runtime.StopAsync(requireHelperExit: true);
            if (!_runtime.Settings.AutoUpdate || !launchInstaller())
                throw new InvalidOperationException("Die Update-Installation wurde nicht gestartet. Mit „Jetzt prüfen“ erneut versuchen.");
        }
        catch (Exception error)
        {
            _updates?.ReportInstallerLaunchFailure(error.Message);
            ErrorText.Text = "Update nicht gestartet: " + error.Message;
            try { await _runtime.StartAsync(); }
            catch (Exception restartError) { ErrorText.Text += " DYNEX konnte nicht weiterlaufen: " + restartError.Message; }
            _updateRestartInProgress = false;
            IsEnabled = true;
            NotifyUpdateRestartSafety();
            return false;
        }
        await ExitCoreAsync();
        return true;
    }

    private async Task ExitCoreAsync()
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
