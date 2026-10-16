using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using global::Android.App;
using global::Android.Content;
using global::Android.Content.PM;
using global::Android.Graphics;
using global::Android.Graphics.Drawables;
using global::Android.OS;
using global::Android.Views;
using global::Android.Widget;

namespace Dynex.Android;

[Activity(Label = "DYNEX", MainLauncher = true, Exported = true, Theme = "@style/AppTheme", ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : Activity
{
    private const int VpnConsentRequest = 7101;
    private const int NotificationPermissionRequest = 7102;
    private const string AdGuardAutomationPackage = "com.adguard.android";
    private const string FirstRunWizardPreferences = "dynex-ui-state";
    private const string AdGuardWizardShownKey = "adguard-setup-wizard-shown-v1";
    private const string ServiceName = "de.dynex.mobile.DynexVpnService";
    private const string ActionStart = "Start";
    private const string ActionStop = "Stop";
    private const string ActionRefresh = "Refresh";
    private const string ActionSettingsChanged = "SettingsChanged";
    private const string ActionPause = "Pause";
    private const string OtherVpnWarning = "Anderes VPN aktiv – DYNEX pausiert. AdGuard und DYNEX können Androids VPN-Verbindung nicht gleichzeitig verwenden.";

    private readonly Color _background = Color.ParseColor("#090A0D");
    private readonly Color _surface = Color.ParseColor("#111318");
    private readonly Color _surfaceRaised = Color.ParseColor("#181A20");
    private readonly Color _line = Color.ParseColor("#292B33");
    private readonly Color _accent = Color.ParseColor("#C91F32");
    private readonly Color _accentBright = Color.ParseColor("#FF4655");
    private readonly Color _text = Color.ParseColor("#F3F3F5");
    private readonly Color _muted = Color.ParseColor("#9296A1");
    private readonly Color _green = Color.ParseColor("#69C69A");

    private LinearLayout _content = null!;
    private ScrollView _scroll = null!;
    private LinearLayout _overview = null!;
    private LinearLayout _settingsPage = null!;
    private TextView _connectionStatus = null!;
    private TextView _limitStatus = null!;
    private TextView _pcCapacity = null!;
    private TextView _downloadValue = null!;
    private TextView _uploadValue = null!;
    private TextView _deviceSummary = null!;
    private LinearLayout _deviceList = null!;
    private TextView _connectionExplanation = null!;
    private TextView _protectionExplanation = null!;
    private TextView _vpnConsentExplanation = null!;
    private Button _pauseButton = null!;
    private Button _settingsPauseButton = null!;
    private Button _headerRefreshButton = null!;
    private Button _connectionRefreshButton = null!;
    private Button _startButton = null!;
    private Button _stopButton = null!;
    private Button _saveButton = null!;
    private Button _overviewTab = null!;
    private Button _settingsTab = null!;
    private Button _proxySetupButton = null!;
    private EditText _deviceName = null!;
    private Switch _autoConnect = null!;
    private Switch _autoStart = null!;
    private Switch _adGuardProxy = null!;
    private EditText _gamingLimit = null!;
    private SeekBar _sharePriority = null!;
    private TextView _sharePriorityValue = null!;
    private bool _pendingVpnStart;
    private string? _pendingServiceAction;
    private bool? _pendingPause;
    private bool _loadingSettings;
    private bool _stopConfirmationShowing;
    private bool _showAdGuardWizardStatusAfterStart;
    private AlertDialog? _adGuardWizardDialog;
    private EditText? _adGuardWizardPassword;
    private TextView? _adGuardWizardStatus;
    private MobileSettings _settings = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window!.SetSoftInputMode(SoftInput.AdjustResize);
        _settings = MobileSettingsStore.Load(this);
        BuildInterface();
        LoadSettingsIntoForm();
        Render(MobileState.Current);
        MobileState.Changed += OnMobileStateChanged;
        if (CanRefresh(MobileState.Current)) SendServiceAction(ActionRefresh);
        new Handler(Looper.MainLooper!).Post(ShowFirstRunAdGuardWizardIfNeeded);
    }

    protected override void OnDestroy()
    {
        MobileState.Changed -= OnMobileStateChanged;
        _adGuardWizardDialog?.Dismiss();
        _adGuardWizardDialog = null;
        _adGuardWizardPassword = null;
        _adGuardWizardStatus = null;
        base.OnDestroy();
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (_content is not null)
        {
            Render(MobileState.Current);
            new Handler(Looper.MainLooper!).Post(ShowFirstRunAdGuardWizardIfNeeded);
        }
    }

    private void BuildInterface()
    {
        var outer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        outer.SetFitsSystemWindows(true);
        outer.SetBackgroundColor(_background);

        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding(Dp(20), Dp(15), Dp(20), Dp(12));
        var mark = new ImageView(this) { ContentDescription = "DYNEX-Logo" };
        mark.SetImageResource(Resource.Drawable.dynex_logo);
        header.AddView(mark, new LinearLayout.LayoutParams(Dp(40), Dp(40)) { RightMargin = Dp(10) });
        var brand = new LinearLayout(this) { Orientation = Orientation.Vertical };
        brand.AddView(Label("DYNEX", 22, _text, true));
        brand.AddView(Label("MOBILE NETWORK PROTECTION", 10, _muted, true));
        header.AddView(brand, new LinearLayout.LayoutParams(0, -2, 1));
        _headerRefreshButton = TextButton("↻", false);
        _headerRefreshButton.ContentDescription = "Verbindung aktualisieren";
        _headerRefreshButton.Click += (_, _) => RefreshConnection();
        header.AddView(_headerRefreshButton, new LinearLayout.LayoutParams(Dp(44), Dp(42)));
        outer.AddView(header);

        var navigation = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        navigation.SetPadding(Dp(20), 0, Dp(20), Dp(10));
        _overviewTab = TextButton("ÜBERSICHT", false);
        _settingsTab = TextButton("EINSTELLUNGEN", false);
        _overviewTab.Click += (_, _) => ShowPage(false);
        _settingsTab.Click += (_, _) => ShowPage(true);
        navigation.AddView(_overviewTab, new LinearLayout.LayoutParams(0, Dp(42), 1) { RightMargin = Dp(8) });
        navigation.AddView(_settingsTab, new LinearLayout.LayoutParams(0, Dp(42), 1));
        outer.AddView(navigation);

        _scroll = new ScrollView(this) { FillViewport = true };
        _scroll.SetClipToPadding(false);
        _scroll.SetPadding(Dp(20), 0, Dp(20), Dp(20));
        _content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _scroll.AddView(_content);
        outer.AddView(_scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        SetContentView(outer);

        BuildOverview();
        BuildSettings();
        ShowPage(false);
    }

    private void BuildOverview()
    {
        _overview = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _overview.AddView(Label("Geräteschutz", 26, _text, true), Spacing(bottom: 3));
        _overview.AddView(Label("Dein Handy folgt dem Gaming-Status deiner DYNEX-PCs.", 13, _muted), Spacing(bottom: 14));

        var connectionCard = Card();
        connectionCard.AddView(SectionLabel("VERBINDUNG"));
        _connectionStatus = Label("Verbindung wird vorbereitet", 16, _text, true);
        connectionCard.AddView(_connectionStatus, Spacing(top: 8, bottom: 4));
        _connectionExplanation = Label("Nur im WLAN: DYNEX verbindet sich mit deinen PCs. Mobile Daten bleiben unbegrenzt.", 12, _muted);
        connectionCard.AddView(_connectionExplanation);
        _protectionExplanation = Label("Ohne Gaming keine Verkehrsverarbeitung. Das VPN läuft nur, wenn PC-Gaming gemeldet und ein Limit geplant ist.", 12, _muted);
        connectionCard.AddView(_protectionExplanation, Spacing(top: 5));
        _vpnConsentExplanation = Label("Beim ersten Start fragt Android einmal nach der Freigabe für den lokalen VPN-Schutz.", 12, _muted);
        connectionCard.AddView(_vpnConsentExplanation, Spacing(top: 5));
        _startButton = PrimaryButton("SCHUTZ EINSCHALTEN", () => RequestVpnAndStart());
        connectionCard.AddView(_startButton, Spacing(top: 13));
        connectionCard.AddView(Label("Nach dem Einschalten läuft DYNEX auch bei geschlossener App im Hintergrund weiter. Stoppen beendet den Dienst.", 12, _muted), Spacing(top: 7));
        var connectionActions = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _stopButton = TextButton("Stoppen", false);
        _stopButton.Click += (_, _) => RequestStop();
        _connectionRefreshButton = TextButton("Aktualisieren", false);
        _connectionRefreshButton.Click += (_, _) => RefreshConnection();
        connectionActions.AddView(_stopButton, new LinearLayout.LayoutParams(0, Dp(40), 1) { RightMargin = Dp(8) });
        connectionActions.AddView(_connectionRefreshButton, new LinearLayout.LayoutParams(0, Dp(40), 1));
        connectionCard.AddView(connectionActions, Spacing(top: 8));
        _overview.AddView(connectionCard, Spacing(bottom: 10));

        var protectionCard = Card();
        protectionCard.AddView(SectionLabel("GAMING-SCHUTZ"));
        _limitStatus = Label("Warte auf Gaming-Status vom PC", 18, _accentBright, true);
        protectionCard.AddView(_limitStatus, Spacing(top: 8, bottom: 5));
        _pcCapacity = Label("PC-Leitung · warte auf Messwerte", 12, _muted);
        protectionCard.AddView(_pcCapacity, Spacing(bottom: 5));
        protectionCard.AddView(Label("Begrenzt wird erst, wenn der Schutz tatsächlich aktiv ist.", 12, _muted));
        _pauseButton = TextButton("PAUSE", false);
        _pauseButton.Click += (_, _) => TogglePause();
        protectionCard.AddView(_pauseButton, Spacing(top: 12));
        _overview.AddView(protectionCard, Spacing(bottom: 10));

        var rates = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var downCard = MetricCard("DOWNLOAD", out _downloadValue);
        var upCard = MetricCard("UPLOAD", out _uploadValue);
        rates.AddView(downCard, new LinearLayout.LayoutParams(0, -2, 1) { RightMargin = Dp(8) });
        rates.AddView(upCard, new LinearLayout.LayoutParams(0, -2, 1));
        _overview.AddView(rates, Spacing(bottom: 10));

        var deviceCard = Card();
        deviceCard.AddView(SectionLabel("GERÄTE IM WLAN"));
        _deviceSummary = Label("Suche nach DYNEX-PCs …", 12, _muted);
        deviceCard.AddView(_deviceSummary, Spacing(top: 6, bottom: 5));
        _deviceList = new LinearLayout(this) { Orientation = Orientation.Vertical };
        deviceCard.AddView(_deviceList);
        _overview.AddView(deviceCard);
    }

    private void BuildSettings()
    {
        _settingsPage = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _settingsPage.AddView(Label("Einstellungen", 26, _text, true), Spacing(bottom: 3));
        _settingsPage.AddView(Label("Dieses Handy wird im Heimnetz als Gerät geschützt.", 13, _muted), Spacing(bottom: 14));

        var deviceCard = Card();
        deviceCard.AddView(SectionLabel("DIESES GERÄT"));
        deviceCard.AddView(Label("Gerätename", 12, _muted), Spacing(top: 10, bottom: 5));
        _deviceName = EditField("DYNEX-Handy");
        deviceCard.AddView(_deviceName);
        _autoConnect = Toggle("Automatisch nur im WLAN mit PCs verbinden");
        _autoStart = Toggle("DYNEX mit Android starten");
        deviceCard.AddView(_autoConnect, Spacing(top: 6));
        deviceCard.AddView(_autoStart, Spacing(top: 2));
        _settingsPage.AddView(deviceCard, Spacing(bottom: 10));

        var proxyCard = Card();
        proxyCard.AddView(SectionLabel("ADGUARD-INTEGRATION"));
        _adGuardProxy = Toggle("Mit AdGuard verwenden");
        proxyCard.AddView(_adGuardProxy, Spacing(top: 6));
        proxyCard.AddView(Label("AdGuard behält Androids VPN. DYNEX stellt einen lokalen SOCKS5-Proxy für die AdGuard-Durchleitung bereit. Mobilfunk bleibt unbegrenzt, die Durchleitung bleibt aktiv. Wenn du den Proxy ausschaltest, benötigt DYNEX eine eigene Android-VPN-Freigabe.", 12, _muted), Spacing(top: 3, bottom: 8));
        proxyCard.AddView(Label("Einmal mit dem Automatisierungs-Passwort verbinden. DYNEX trägt die Proxy-Daten in AdGuard ein. Das Passwort bleibt verschlüsselt auf diesem Handy.", 12, _muted), Spacing(bottom: 9));
        _proxySetupButton = TextButton("ADGUARD EINRICHTEN", false);
        _proxySetupButton.Click += (_, _) => ShowAdGuardWizard();
        proxyCard.AddView(_proxySetupButton);
        _settingsPage.AddView(proxyCard, Spacing(bottom: 10));

        var limitsCard = Card();
        limitsCard.AddView(SectionLabel("BEI PC-GAMING"));
        limitsCard.AddView(Label("Maximaler Handydownload (Mbit/s)", 12, _muted), Spacing(top: 10, bottom: 5));
        _gamingLimit = EditField("Leer = automatisch");
        _gamingLimit.InputType = global::Android.Text.InputTypes.ClassNumber | global::Android.Text.InputTypes.NumberFlagDecimal;
        limitsCard.AddView(_gamingLimit);
        limitsCard.AddView(Label("Die Leitungswerte kommen automatisch vom PC. Leer lässt DYNEX daraus deinen Anteil berechnen; eine Eingabe kann diesen nur weiter senken.", 12, _muted), Spacing(top: 5));
        limitsCard.AddView(Label("Hintergrund-Anteil", 12, _muted), Spacing(top: 12, bottom: 3));
        _sharePriority = new SeekBar(this) { Max = 375, Progress = 75 };
        limitsCard.AddView(_sharePriority);
        _sharePriorityValue = Label("Anteil: 1,00×", 12, _muted);
        limitsCard.AddView(_sharePriorityValue);
        _sharePriority.ProgressChanged += (_, e) =>
        {
            var value = 0.25 + e.Progress / 100d;
            _sharePriorityValue.Text = $"Anteil: {value.ToString("0.00", CultureInfo.CurrentCulture)}×";
            UpdateSaveButton();
        };
        _settingsPage.AddView(limitsCard, Spacing(bottom: 10));

        var pauseCard = Card();
        pauseCard.AddView(SectionLabel("SCHUTZ STEUERN"));
        pauseCard.AddView(Label("Pausieren gibt die Downloadbegrenzung frei. Geräte bleiben verbunden.", 12, _muted), Spacing(top: 7, bottom: 10));
        _settingsPauseButton = TextButton("SCHUTZ PAUSIEREN", false);
        _settingsPauseButton.Click += (_, _) => TogglePause();
        pauseCard.AddView(_settingsPauseButton);
        _saveButton = PrimaryButton("EINSTELLUNGEN SPEICHERN", SaveSettings);
        pauseCard.AddView(_saveButton, Spacing(top: 10));
        _settingsPage.AddView(pauseCard);
        _deviceName.TextChanged += (_, _) => UpdateSaveButton();
        _gamingLimit.TextChanged += (_, _) => UpdateSaveButton();
        _autoConnect.CheckedChange += (_, _) => UpdateSaveButton();
        _autoStart.CheckedChange += (_, _) => UpdateSaveButton();
        _adGuardProxy.CheckedChange += (_, _) => UpdateSaveButton();
    }

    private void Render(MobileSnapshot state)
    {
        var adGuardMode = _settings.UseAdGuardProxy;
        var otherVpnBlocksStandalone = !adGuardMode && state.OtherVpnActive;
        _connectionExplanation.Text = adGuardMode
            ? "Nur im WLAN nach PCs suchen. AdGuard behält Androids VPN; DYNEX begrenzt Mobilfunk nicht."
            : "Nur im WLAN: DYNEX verbindet sich mit deinen PCs. Mobile Daten bleiben unbegrenzt.";
        _protectionExplanation.Text = adGuardMode
            ? "Ohne PC-Gaming keine Downloadbegrenzung. DYNEX stellt den lokalen SOCKS5-Proxy für AdGuard bereit."
            : "Ohne Gaming keine Verkehrsverarbeitung. Das VPN läuft nur, wenn PC-Gaming gemeldet und ein Limit geplant ist.";
        _vpnConsentExplanation.Visibility = adGuardMode ? ViewStates.Gone : ViewStates.Visible;
        var proxyRunning = adGuardMode && state.ProxyMode && state.IsRunning;
        var fallbackConnection = adGuardMode
            ? proxyRunning ? "DYNEX-Proxydienst läuft" : "AdGuard-Modus · DYNEX-Proxy noch nicht gestartet"
            : !state.IsOnWifi ? "Nur im WLAN · warte auf WLAN" : state.IsConnected ? "Mit DYNEX-PCs verbunden" : "Suche nach DYNEX-PCs";
        _connectionStatus.Text = otherVpnBlocksStandalone ? OtherVpnWarning
            : !string.IsNullOrWhiteSpace(state.ConnectionStatusText) ? state.ConnectionStatusText
            : fallbackConnection;
        if (otherVpnBlocksStandalone)
            _limitStatus.Text = "DYNEX pausiert · anderes VPN aktiv";
        else if (state.IsPaused)
            _limitStatus.Text = "Pausiert · keine Begrenzung";
        else if (state.LimitApplied)
            _limitStatus.Text = $"Begrenzt · {FormatRate(state.DownloadLimitMbps)} Mbit/s";
        else if (state.LimitPlanned)
            _limitStatus.Text = $"Limit geplant · {FormatRate(state.DownloadLimitMbps)} Mbit/s";
        else if (proxyRunning && !state.HasNetworkTransport)
            _limitStatus.Text = "Kein aktives Netz · keine Begrenzung";
        else if (proxyRunning && !state.IsOnWifi)
            _limitStatus.Text = "Mobilfunk · unbegrenzt";
        else if (proxyRunning && state.IsObservingGaming)
            _limitStatus.Text = "DYNEX-Proxydienst läuft · warte auf PC-Gaming";
        else if (proxyRunning)
            _limitStatus.Text = "DYNEX-Proxydienst läuft · keine Begrenzung ohne Gaming";
        else if (adGuardMode)
            _limitStatus.Text = "AdGuard-Proxy noch nicht gestartet";
        else if (state.IsObservingGaming)
            _limitStatus.Text = "Warte auf Gaming-Status vom PC";
        else if (state.GamingReported)
            _limitStatus.Text = "PC meldet Gaming · Limit nicht aktiv";
        else if (state.IsConnected)
            _limitStatus.Text = "Verbunden · Gaming-Status wird geprüft";
        else
            _limitStatus.Text = "Noch kein PC-Gaming-Status";

        _limitStatus.SetTextColor(otherVpnBlocksStandalone ? _accentBright : state.LimitApplied ? _green : state.LimitPlanned ? _accentBright : _text);
        _pcCapacity.Text = state.SharedCapacityMbps is not null
            ? $"Vom PC übernommen · ↓ {FormatRate(state.SharedCapacityMbps)} / ↑ {FormatRate(state.SharedUploadCapacityMbps)} Mbit/s"
            : "PC-Leitung · noch keine bestätigten Messwerte";
        if (_adGuardWizardStatus is not null)
        {
            if (state.ProxyForwardingObserved)
                _adGuardWizardStatus.Text = "Datenweiterleitung über DYNEX wurde beobachtet. Du kannst den Assistenten schließen und DYNEX normal verwenden.";
            else if (state.IsRunning && !state.HasNetworkTransport)
                _adGuardWizardStatus.Text = "Noch kein nutzbares Netz erkannt. Stelle eine Internetverbindung her. Falls AdGuard DYNEX erfasst: AdGuard → App-Verwaltung → DYNEX → Datenverkehr durch AdGuard leiten ausschalten. Danach hier erneut starten.";
            else if (!string.IsNullOrEmpty(state.ConnectionStatusText) && (!state.IsRunning || state.ConnectionStatusText.Contains("nicht aktiv")))
                _adGuardWizardStatus.Text = state.ConnectionStatusText;
            else if (state.AdGuardSetupRequested)
                _adGuardWizardStatus.Text = "Die Einrichtungsanfrage wurde gesendet, aber Datenweiterleitung ist noch nicht bestätigt. Lade eine Browserseite. Falls sie nicht lädt: In AdGuard → App-Verwaltung → DYNEX muss „Datenverkehr durch AdGuard leiten“ ausgeschaltet sein. Prüfe außerdem das Automatisierungs-Passwort.";
            else
                _adGuardWizardStatus.Text = "Noch nicht bestätigt. Öffne Browser oder App und lade eine Seite; DYNEX bestätigt die Einrichtung erst, wenn Datenweiterleitung beobachtet wurde.";
        }
        _downloadValue.Text = FormatRate(state.DownloadMbps);
        _uploadValue.Text = FormatRate(state.UploadMbps);
        var busy = _pendingServiceAction is not null || _pendingVpnStart;
        _startButton.Text = _pendingServiceAction == ActionStart ? "DYNEX STARTET …"
            : state.IsRunning ? "HINTERGRUNDDIENST AKTIV" : "SCHUTZ EINSCHALTEN";
        _stopButton.Text = _pendingServiceAction == ActionStop ? "WIRD GESTOPPT …" : "STOPPEN";
        SetButtonEnabled(_startButton, !state.IsRunning && !busy);
        SetButtonEnabled(_stopButton, state.IsRunning && !busy);
        _pauseButton.Text = _pendingPause.HasValue ? "WIRD ÜBERNOMMEN …" : state.IsPaused ? "WEITER" : "PAUSE";
        _settingsPauseButton.Text = _pendingPause.HasValue ? "WIRD ÜBERNOMMEN …"
            : state.IsPaused ? "SCHUTZ FORTSETZEN" : "SCHUTZ PAUSIEREN";
        SetButtonEnabled(_pauseButton, state.IsRunning && !busy && !_pendingPause.HasValue);
        SetButtonEnabled(_settingsPauseButton, state.IsRunning && !busy && !_pendingPause.HasValue);
        SetButtonEnabled(_headerRefreshButton, CanRefresh(state));
        SetButtonEnabled(_connectionRefreshButton, CanRefresh(state));
        _connectionRefreshButton.ContentDescription = CanRefresh(state) ? "PC-Suche aktualisieren"
            : !state.IsRunning ? "Aktualisieren: zuerst DYNEX einschalten"
            : !state.IsOnWifi ? "Aktualisieren nur im WLAN verfügbar" : "Automatische PC-Verbindung ist ausgeschaltet";
        _headerRefreshButton.ContentDescription = _connectionRefreshButton.ContentDescription;
        var adGuardInstalled = IsAdGuardInstalled();
        _proxySetupButton.Text = adGuardInstalled ? "ADGUARD EINRICHTEN" : "ADGUARD NICHT INSTALLIERT";
        SetButtonEnabled(_proxySetupButton, adGuardInstalled && !busy);
        _adGuardProxy.Enabled = !busy && (adGuardInstalled || _adGuardProxy.Checked);
        _adGuardProxy.Alpha = _adGuardProxy.Enabled ? 1f : .45f;
        UpdateSaveButton();
        RenderDevices(state.Devices);
    }

    private void RenderDevices(IReadOnlyList<MobileDeviceSnapshot> devices)
    {
        _deviceList.RemoveAllViews();
        _deviceSummary.Text = devices.Count == 0
            ? "Noch keine Geräte gefunden. DYNEX verbindet sich mit PCs im selben WLAN."
            : $"{devices.Count} {(devices.Count == 1 ? "Gerät" : "Geräte")} im WLAN";
        foreach (var device in devices)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Vertical };
            row.SetPadding(Dp(10), Dp(9), Dp(10), Dp(9));
            row.Background = Rounded(_surfaceRaised, _line, 3);
            var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            top.SetGravity(GravityFlags.CenterVertical);
            var name = Label(device.Name, 14, _text, true);
            name.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
            name.SetSingleLine(true);
            top.AddView(name, new LinearLayout.LayoutParams(0, -2, 1));
            var status = Label(device.StatusText, 11, _muted, true);
            status.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
            status.SetSingleLine(true);
            top.AddView(status, new LinearLayout.LayoutParams(Dp(110), -2));
            row.AddView(top);
            row.AddView(Label($"↓ {FormatRate(device.DownloadMbps)} Mbit/s    ↑ {FormatRate(device.UploadMbps)} Mbit/s    Limit {FormatRate(device.LimitMbps)} Mbit/s", 12, _muted), Spacing(top: 5));
            _deviceList.AddView(row, Spacing(top: 5));
        }
    }

    private void LoadSettingsIntoForm()
    {
        _loadingSettings = true;
        _deviceName.Text = _settings.DeviceName;
        _autoConnect.Checked = _settings.AutoConnect;
        _autoStart.Checked = _settings.AutoStart;
        _adGuardProxy.Checked = _settings.UseAdGuardProxy;
        _gamingLimit.Text = _settings.GamingDownloadLimitMbps?.ToString("0.##", CultureInfo.CurrentCulture) ?? string.Empty;
        _sharePriority.Progress = Math.Clamp((int)Math.Round((_settings.SharePriority - 0.25) * 100), 0, 375);
        _sharePriorityValue.Text = $"Anteil: {_settings.SharePriority.ToString("0.00", CultureInfo.CurrentCulture)}×";
        _loadingSettings = false;
        UpdateSaveButton();
    }

    private void UpdateSaveButton()
    {
        if (_loadingSettings || _saveButton is null) return;
        var dirty = _deviceName.Text?.Trim() != _settings.DeviceName
            || _autoConnect.Checked != _settings.AutoConnect || _autoStart.Checked != _settings.AutoStart
            || _adGuardProxy.Checked != _settings.UseAdGuardProxy
            || (_gamingLimit.Text?.Trim() ?? "") != (_settings.GamingDownloadLimitMbps?.ToString("0.##", CultureInfo.CurrentCulture) ?? "")
            || _sharePriority.Progress != Math.Clamp((int)Math.Round((_settings.SharePriority - .25) * 100), 0, 375);
        _saveButton.Text = dirty ? "EINSTELLUNGEN SPEICHERN" : "EINSTELLUNGEN GESPEICHERT";
        SetButtonEnabled(_saveButton, dirty && _pendingServiceAction is null && !_pendingVpnStart);
    }

    private void SaveSettings()
    {
        var name = _deviceName.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl))
        {
            _deviceName.Error = "Bitte 1–80 lesbare Zeichen eingeben.";
            return;
        }

        double? limit = null;
        var limitText = _gamingLimit.Text?.Trim();
        if (!string.IsNullOrEmpty(limitText))
        {
            if (!double.TryParse(limitText, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsed) || !double.IsFinite(parsed) || parsed is < .1 or > 100_000)
            {
                _gamingLimit.Error = "0,1–100000 eingeben oder leer lassen.";
                return;
            }
            limit = parsed;
        }

        var updatedSettings = _settings with
        {
            DeviceName = name,
            AutoConnect = _autoConnect.Checked,
            AutoStart = _autoStart.Checked,
            UseAdGuardProxy = _adGuardProxy.Checked,
            GamingDownloadLimitMbps = limit,
            SharePriority = 0.25 + _sharePriority.Progress / 100d
        };
        try
        {
            MobileSettingsStore.Save(this, updatedSettings);
            _settings = updatedSettings;
            LoadSettingsIntoForm();
            if (MobileState.Current.IsRunning) SendServiceAction(ActionSettingsChanged);
            Render(MobileState.Current);
            Toast.MakeText(this, "Einstellungen gespeichert", ToastLength.Short)?.Show();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("DYNEX", $"Mobile Einstellungen konnten nicht gespeichert werden: {ex.GetType().Name}");
            Toast.MakeText(this, "Einstellungen konnten nicht gespeichert werden", ToastLength.Long)?.Show();
        }
    }

    private void TogglePause()
    {
        if (!MobileState.Current.IsRunning || _pendingServiceAction is not null || _pendingPause.HasValue) return;
        var pause = !MobileState.Current.IsPaused;
        _pendingPause = pause;
        Render(MobileState.Current);
        var intent = CreateServiceIntent(ActionPause);
        intent.PutExtra("Pause", pause);
        StartAndroidService(intent);
    }

    private bool CanRefresh(MobileSnapshot state) => state.IsRunning && state.IsOnWifi && _settings.AutoConnect
        && _pendingServiceAction is null && !_pendingVpnStart;

    private void RefreshConnection()
    {
        if (!CanRefresh(MobileState.Current)) return;
        if (StartAndroidService(CreateServiceIntent(ActionRefresh)))
            Toast.MakeText(this, "PC-Suche im WLAN angefordert", ToastLength.Short)?.Show();
    }

    private void RequestStop()
    {
        if (!MobileState.Current.IsRunning || _pendingServiceAction is not null || _stopConfirmationShowing) return;
        if (!_settings.UseAdGuardProxy)
        {
            StopProtectionService();
            return;
        }

        var dialog = new AlertDialog.Builder(this);
        dialog.SetTitle("AdGuard-Proxy stoppen?");
        dialog.SetMessage("DYNEX fordert AdGuard auf, den Proxy auszuschalten. Falls AdGuard die Anfrage nicht annimmt (zum Beispiel nach einer Passwortänderung), musst du dort den Proxy ausschalten, damit das Internet weiter funktioniert. Mit Pause bleibt die Verbindung bestehen und das Downloadlimit entfällt.");
        dialog.SetPositiveButton("DYNEX stoppen", (_, _) => StopProtectionService());
        dialog.SetNegativeButton("Abbrechen", (_, _) => { });
        _stopConfirmationShowing = true;
        var confirmation = dialog.Create()!;
        confirmation.DismissEvent += (_, _) => _stopConfirmationShowing = false;
        confirmation.Show();
    }

    private void StopProtectionService()
    {
        if (!MobileState.Current.IsRunning || _pendingServiceAction is not null) return;
        _pendingServiceAction = ActionStop;
        Render(MobileState.Current);
        SendServiceAction(ActionStop);
    }

    private void RequestVpnAndStart(bool reconnect = false)
    {
        if (_pendingServiceAction is not null || _pendingVpnStart || (MobileState.Current.IsRunning && !reconnect)) return;
        if (_settings.UseAdGuardProxy && !AdGuardIntegration.HasSavedPassword(this))
        {
            ShowAdGuardWizard();
            return;
        }
        if (!_settings.UseAdGuardProxy && BlockForOtherVpn()) return;
        _pendingVpnStart = true;
        _pendingServiceAction = ActionStart;
        Render(MobileState.Current);
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions(new[] { global::Android.Manifest.Permission.PostNotifications }, NotificationPermissionRequest);
            return;
        }
        if (_settings.UseAdGuardProxy) StartProtectionService();
        else RequestVpnConsent();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != NotificationPermissionRequest) return;
        var continueStart = _pendingVpnStart;
        if (continueStart) RequestVpnConsent();
    }

    private void RequestVpnConsent()
    {
        if (_settings.UseAdGuardProxy)
        {
            StartProtectionService();
            return;
        }
        if (BlockForOtherVpn()) { ClearPendingActions(); return; }
        if (VpnCompatibility.OwnVpnActive) { StartProtectionService(); return; }
        var prepare = global::Android.Net.VpnService.Prepare(this);
        if (prepare is null)
        {
            StartProtectionService();
            return;
        }
        StartActivityForResult(prepare, VpnConsentRequest);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != VpnConsentRequest) return;
        if (resultCode == Result.Ok) StartProtectionService();
        else
        {
            ClearPendingActions();
            Toast.MakeText(this, "VPN-Freigabe abgebrochen · DYNEX bleibt ausgeschaltet", ToastLength.Long)?.Show();
        }
    }

    private void StartProtectionService()
    {
        if (!_settings.UseAdGuardProxy && BlockForOtherVpn()) { ClearPendingActions(); return; }
        try { MobileSettingsStore.Save(this, _settings); }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("DYNEX", "Start-Einstellungen konnten nicht gespeichert werden: " + ex.GetType().Name);
            ClearPendingActions();
            Toast.MakeText(this, "DYNEX konnte die Einstellungen nicht speichern", ToastLength.Long)?.Show();
            return;
        }
        _pendingVpnStart = false;
        if (!StartAndroidService(CreateServiceIntent(ActionStart), foreground: true)) return;
        if (_settings.UseAdGuardProxy && _showAdGuardWizardStatusAfterStart)
        {
            _showAdGuardWizardStatusAfterStart = false;
            ShowAdGuardWizardStatus();
        }
    }

    private bool BlockForOtherVpn()
    {
        if (_settings.UseAdGuardProxy) return false;
        if (!VpnCompatibility.HasOtherActiveVpn(this)) return false;
        var dialog = new AlertDialog.Builder(this);
        dialog.SetTitle("Anderes VPN aktiv");
        dialog.SetMessage(OtherVpnWarning);
        dialog.SetPositiveButton("OK", (_, _) => { });
        dialog.Show();
        return true;
    }

    private void ShowFirstRunAdGuardWizardIfNeeded()
    {
        if (IsFinishing || IsDestroyed || !IsAdGuardInstalled()) return;
        using var preferences = GetSharedPreferences(FirstRunWizardPreferences, FileCreationMode.Private)!;
        if (preferences.GetBoolean(AdGuardWizardShownKey, false)) return;
        using var edit = preferences.Edit()!;
        if (!edit.PutBoolean(AdGuardWizardShownKey, true)!.Commit())
        {
            global::Android.Util.Log.Warn("DYNEX", "Der Status des erstmalig angezeigten AdGuard-Assistenten konnte nicht gespeichert werden.");
        }
        ShowAdGuardWizard();
    }

    private bool IsAdGuardInstalled()
    {
        try { using var intent = PackageManager?.GetLaunchIntentForPackage(AdGuardAutomationPackage); return intent is not null; }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("DYNEX", "AdGuard-App konnte nicht geprüft werden: " + ex.GetType().Name);
            return false;
        }
    }

    private void ShowAdGuardWizard()
    {
        if (IsFinishing || IsDestroyed) return;
        DismissAdGuardWizardDialog();

        Intent? launchIntent = null;
        try { launchIntent = PackageManager?.GetLaunchIntentForPackage(AdGuardAutomationPackage); }
        catch (Exception ex) { global::Android.Util.Log.Warn("DYNEX", "AdGuard-App konnte nicht geprüft werden: " + ex.GetType().Name); }

        if (launchIntent is null || !AdGuardIntegration.IsAvailable(this))
        {
            ShowAdGuardUnavailable(launchIntent is null);
            return;
        }

        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.SetPadding(Dp(20), Dp(8), Dp(20), Dp(8));
        content.AddView(SectionLabel("SCHRITT 1 VON 3 · ADGUARD"), Spacing(bottom: 10));
        content.AddView(Label("AdGuard behält Androids VPN. DYNEX richtet darin einen lokalen SOCKS5-Proxy ein. Der aktuell in AdGuard ausgewählte Proxy wird dabei durch DYNEX ersetzt.", 13, _text), Spacing(bottom: 10));
        content.AddView(Label("Nimm zuerst DYNEX selbst aus: AdGuard → App-Verwaltung → DYNEX → „Datenverkehr durch AdGuard leiten“ ausschalten. So wird die Verbindung nicht im Kreis geleitet. Andere Apps bleiben geschützt.", 13, _text), Spacing(bottom: 10));
        content.AddView(Label("In AdGuard: Einstellungen → Allgemein → Erweiterte Einstellungen → Automatisierung. Automatisierung erlauben und dort das angezeigte Passwort abrufen. Im nächsten Schritt gibst du es einmal hier ein.", 13, _muted), Spacing(bottom: 10));
        var openAdGuard = TextButton("ADGUARD ÖFFNEN", false);
        openAdGuard.Click += (_, _) =>
        {
            try { StartActivity(launchIntent); }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("DYNEX", "AdGuard konnte nicht geöffnet werden: " + ex.GetType().Name);
                Toast.MakeText(this, "AdGuard konnte nicht geöffnet werden", ToastLength.Long)?.Show();
            }
        };
        content.AddView(openAdGuard, Spacing(bottom: 4));
        var builder = new AlertDialog.Builder(this);
        builder.SetTitle("AdGuard einrichten");
        var scroll = new ScrollView(this);
        scroll.AddView(content);
        builder.SetView(scroll);
        builder.SetPositiveButton("Weiter", (_, _) => { });
        builder.SetNegativeButton("Später", (_, _) => { });
        var dialog = builder.Create()!;
        TrackAdGuardWizardDialog(dialog);
        dialog.Show();
        dialog.GetButton((int)DialogButtonType.Positive)!.Click += (_, _) =>
        {
            dialog.Dismiss();
            ShowAdGuardWizardPassword();
        };
    }

    private void ShowAdGuardUnavailable(bool notInstalled)
    {
        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.SetPadding(Dp(20), Dp(8), Dp(20), Dp(8));
        content.AddView(Label(notInstalled
            ? "AdGuard für Android wurde auf diesem Gerät nicht gefunden. Du kannst DYNEX stattdessen im eigenständigen VPN-Modus verwenden. Öffne dafür die Einstellungen und schalte „Mit AdGuard verwenden“ aus."
            : "AdGuard ist installiert, aber die Automation-Schnittstelle ist nicht verfügbar. Benötigt wird AdGuard für Android ab Version 4 mit aktivierter Automation. Du kannst AdGuard öffnen oder DYNEX eigenständig verwenden.", 13, _text), Spacing(bottom: 10));
        if (!notInstalled)
        {
            var openAdGuard = TextButton("ADGUARD ÖFFNEN", false);
            openAdGuard.Click += (_, _) =>
            {
                try
                {
                    using var launchIntent = PackageManager?.GetLaunchIntentForPackage(AdGuardAutomationPackage);
                    if (launchIntent is not null) StartActivity(launchIntent);
                    else Toast.MakeText(this, "AdGuard ist nicht verfügbar", ToastLength.Long)?.Show();
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Warn("DYNEX", "AdGuard konnte nicht geöffnet werden: " + ex.GetType().Name);
                    Toast.MakeText(this, "AdGuard konnte nicht geöffnet werden", ToastLength.Long)?.Show();
                }
            };
            content.AddView(openAdGuard, Spacing(bottom: 4));
        }
        var builder = new AlertDialog.Builder(this);
        builder.SetTitle(notInstalled ? "AdGuard nicht gefunden" : "AdGuard-Automation nicht verfügbar");
        builder.SetView(content);
        builder.SetPositiveButton("Zu Einstellungen", (_, _) => ShowPage(true));
        builder.SetNegativeButton("Später", (_, _) => { });
        var dialog = builder.Create()!;
        TrackAdGuardWizardDialog(dialog);
        dialog.Show();
    }

    private void ShowAdGuardWizardPassword()
    {
        if (IsFinishing || IsDestroyed) return;
        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.SetPadding(Dp(20), Dp(8), Dp(20), Dp(8));
        content.AddView(SectionLabel("SCHRITT 2 VON 3 · PASSWORT"), Spacing(bottom: 10));
        content.AddView(Label("Füge das Automatisierungs-Passwort aus AdGuard hier ein. Es wird verschlüsselt im privaten Speicher dieses Handys abgelegt und nicht in die Zwischenablage kopiert.", 13, _text), Spacing(bottom: 10));
        var password = EditField("AdGuard-Automatisierungs-Passwort");
        password.InputType = global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextVariationPassword;
        password.ImportantForAutofill = ImportantForAutofill.No;
        content.AddView(password);
        var builder = new AlertDialog.Builder(this);
        builder.SetTitle("AdGuard einrichten");
        builder.SetView(content);
        builder.SetPositiveButton("Verbinden", (_, _) => { });
        builder.SetNegativeButton("Abbrechen", (_, _) => { });
        var dialog = builder.Create()!;
        TrackAdGuardWizardDialog(dialog, password);
        dialog.Window?.AddFlags(WindowManagerFlags.Secure);
        dialog.Show();
        dialog.Window?.AddFlags(WindowManagerFlags.Secure);
        _adGuardWizardPassword = password;
        var connectButton = dialog.GetButton((int)DialogButtonType.Positive)!;
        SetButtonEnabled(connectButton, false);
        password.TextChanged += (_, _) => SetButtonEnabled(connectButton, !string.IsNullOrWhiteSpace(password.Text));
        connectButton.Click += (_, _) =>
        {
            var cleartext = password.Text ?? string.Empty;
            try
            {
                AdGuardIntegration.SavePassword(this, cleartext);
                password.Text = string.Empty;
                _settings = _settings with { UseAdGuardProxy = true };
                MobileSettingsStore.Save(this, _settings);
                LoadSettingsIntoForm();
                dialog.Dismiss();
                _showAdGuardWizardStatusAfterStart = true;
                RequestVpnAndStart(reconnect: true);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("DYNEX", "AdGuard setup: " + ex.GetType().Name);
                password.Error = "Passwort prüfen. Die Verbindung konnte nicht eingerichtet werden.";
            }
            finally { cleartext = string.Empty; }
        };
    }

    private void ShowAdGuardWizardStatus()
    {
        if (IsFinishing || IsDestroyed) return;
        DismissAdGuardWizardDialog();
        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.SetPadding(Dp(20), Dp(8), Dp(20), Dp(8));
        content.AddView(SectionLabel("SCHRITT 3 VON 3 · PRÜFEN"), Spacing(bottom: 10));
        _adGuardWizardStatus = Label("Noch nicht bestätigt. Öffne Browser oder App und lade eine Seite; DYNEX bestätigt die Einrichtung erst, wenn Datenweiterleitung beobachtet wurde.", 13, _text);
        content.AddView(_adGuardWizardStatus, Spacing(bottom: 10));
        var openAdGuard = TextButton("ADGUARD ÖFFNEN", false);
        openAdGuard.Click += (_, _) =>
        {
            try
            {
                using var launchIntent = PackageManager?.GetLaunchIntentForPackage(AdGuardAutomationPackage);
                if (launchIntent is not null) StartActivity(launchIntent);
                else Toast.MakeText(this, "AdGuard ist nicht verfügbar", ToastLength.Long)?.Show();
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("DYNEX", "AdGuard konnte nicht geöffnet werden: " + ex.GetType().Name);
                Toast.MakeText(this, "AdGuard konnte nicht geöffnet werden", ToastLength.Long)?.Show();
            }
        };
        content.AddView(openAdGuard);
        var builder = new AlertDialog.Builder(this);
        builder.SetTitle("Weiterleitung prüfen");
        builder.SetView(content);
        builder.SetPositiveButton("Schließen", (_, _) => { });
        var dialog = builder.Create()!;
        TrackAdGuardWizardDialog(dialog);
        dialog.Show();
        Render(MobileState.Current);
    }

    private void TrackAdGuardWizardDialog(AlertDialog dialog, EditText? password = null)
    {
        _adGuardWizardDialog = dialog;
        dialog.DismissEvent += (_, _) =>
        {
            if (password is not null) password.Text = string.Empty;
            if (ReferenceEquals(_adGuardWizardDialog, dialog))
            {
                _adGuardWizardDialog = null;
                _adGuardWizardPassword = null;
                _adGuardWizardStatus = null;
            }
        };
    }

    private void DismissAdGuardWizardDialog()
    {
        _adGuardWizardDialog?.Dismiss();
        _adGuardWizardDialog = null;
        _adGuardWizardPassword = null;
        _adGuardWizardStatus = null;
    }

    private void SendServiceAction(string action) => StartAndroidService(CreateServiceIntent(action), foreground: action == ActionStart);

    private Intent CreateServiceIntent(string action)
    {
        var intent = new Intent(action);
        intent.SetClassName(PackageName!, ServiceName);
        return intent;
    }

    private bool StartAndroidService(Intent intent, bool foreground = false)
    {
        try
        {
            var started = foreground && Build.VERSION.SdkInt >= BuildVersionCodes.O
                ? StartForegroundService(intent) : StartService(intent);
            if (started is null) throw new InvalidOperationException("DYNEX-Dienst nicht verfügbar.");
            return true;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("DYNEX", "Dienstaktion fehlgeschlagen: " + ex.GetType().Name);
            ClearPendingActions();
            Toast.MakeText(this, "Aktion konnte nicht ausgeführt werden. Bitte erneut versuchen.", ToastLength.Long)?.Show();
            return false;
        }
    }

    private void ClearPendingActions()
    {
        _pendingVpnStart = false;
        _pendingServiceAction = null;
        _pendingPause = null;
        Render(MobileState.Current);
    }

    private void OnMobileStateChanged()
    {
        if (IsFinishing || IsDestroyed) return;
        RunOnUiThread(() =>
        {
            if (IsFinishing || IsDestroyed) return;
            var state = MobileState.Current;
            if (_pendingServiceAction == ActionStart && !_pendingVpnStart
                || _pendingServiceAction == ActionStop && !state.IsRunning)
                _pendingServiceAction = null;
            if (!state.IsRunning || state.IsPaused == _pendingPause) _pendingPause = null;
            Render(state);
        });
    }

    private void ShowPage(bool settings)
    {
        _content.RemoveAllViews();
        _content.AddView(settings ? _settingsPage : _overview);
        _scroll.ScrollTo(0, 0);
        _overviewTab.Enabled = settings;
        _settingsTab.Enabled = !settings;
        _overviewTab.SetTextColor(settings ? _muted : _accentBright);
        _settingsTab.SetTextColor(settings ? _accentBright : _muted);
        _overviewTab.Selected = !settings;
        _settingsTab.Selected = settings;
    }

    private LinearLayout Card()
    {
        var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
        card.SetPadding(Dp(14), Dp(13), Dp(14), Dp(13));
        card.SetBackgroundResource(Resource.Drawable.dynex_card_background);
        return card;
    }

    private LinearLayout MetricCard(string title, out TextView value)
    {
        var card = Card();
        card.AddView(SectionLabel(title));
        value = Label("—", 21, _text, true);
        card.AddView(value, Spacing(top: 7));
        card.AddView(Label("Mbit/s", 11, _muted));
        return card;
    }

    private TextView SectionLabel(string text) => Label(text, 11, _accentBright, true);

    private TextView Label(string text, float sp, Color color, bool bold = false)
    {
        var view = new TextView(this) { Text = text, TextSize = sp, Gravity = GravityFlags.CenterVertical };
        view.SetTextColor(color);
        if (bold) view.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        view.SetIncludeFontPadding(false);
        return view;
    }

    private EditText EditField(string hint)
    {
        var field = new EditText(this) { Hint = hint, TextSize = 14 };
        field.SetSingleLine(true);
        field.SetTextColor(_text);
        field.SetHintTextColor(_muted);
        field.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
        field.Background = Rounded(_surfaceRaised, _line, 3);
        return field;
    }

    private Switch Toggle(string text)
    {
        var toggle = new Switch(this) { Text = text, TextSize = 13 };
        toggle.SetTextColor(_text);
        toggle.SetPadding(0, Dp(6), 0, Dp(6));
        return toggle;
    }

    private Button PrimaryButton(string text, Action action)
    {
        var button = TextButton(text, true);
        button.Click += (_, _) => action();
        return button;
    }

    private Button TextButton(string text, bool primary)
    {
        var button = new Button(this) { Text = text, TextSize = 12 };
        button.SetAllCaps(true);
        button.Gravity = GravityFlags.Center;
        button.SetTextColor(_text);
        button.SetPadding(Dp(10), 0, Dp(10), 0);
        if (primary) button.SetBackgroundResource(Resource.Drawable.dynex_accent_button);
        else button.Background = Rounded(_surfaceRaised, _line, 3);
        return button;
    }

    private static void SetButtonEnabled(Button button, bool enabled)
    {
        button.Enabled = enabled;
        button.Alpha = enabled ? 1f : .4f;
    }

    private GradientDrawable Rounded(Color fill, Color stroke, int radiusDp)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(fill);
        drawable.SetCornerRadius(Dp(radiusDp));
        drawable.SetStroke(Dp(1), stroke);
        return drawable;
    }

    private LinearLayout.LayoutParams Spacing(int top = 0, int bottom = 0) => new(-1, -2)
    {
        TopMargin = Dp(top),
        BottomMargin = Dp(bottom)
    };

    private int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density + 0.5f);

    private string FormatRate(double? value) => value is { } rate
        ? $"{rate.ToString("0.0", CultureInfo.CurrentCulture)}"
        : "—";
}
