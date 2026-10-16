using System.IO;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Threading.Channels;
using Dynex.Core;
using Dynex.Desktop.Games;
using Microsoft.Win32;

namespace Dynex.Desktop;

public sealed partial class DynexRuntime : IAsyncDisposable
{
    private readonly SettingsStore store;
    private SettingsStore.Identity identity;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly ShaperController shaper;
    private readonly bool shapingAllowed;
    private readonly List<HistoryPoint> history = [];
    private IPeerService? peers;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private readonly object refreshSync = new();
    private Channel<TaskCompletionSource<RuntimeSnapshot>>? refreshRequests;
    private bool started;
    private LimitPlanner? activePlanner;
    private string? plannerPolicy;
    private readonly bool isolated;
    private readonly ISpeedTestService speedTest;
    private readonly Func<string, IReadOnlyList<GameDetectionResult>> scanGames;
    private readonly Func<ForegroundProcess?> readForeground;
    private readonly Func<IReadOnlyList<GameTrafficIdentity>?> readGameTraffic;
    private readonly bool automaticSpeedTestEnabled;
    private readonly object speedTestSync = new();
    private CancellationTokenSource? speedTestCancellation;
    private volatile Task? speedTestTask;
    private volatile bool stopping;
    private long peerStartedAt;
    private volatile bool monitoringReady;
    private volatile bool speedTestLoadStarted;
    private volatile bool pendingGamingSettings;
    private string? peerStartError;
    private readonly SemaphoreSlim networkCheckGate = new(1, 1);
    private NetworkConnectionCheck? networkConnection;
    private readonly SessionTracker sessions = new();
    private readonly PingAlarm pingAlarm = new();
    private string? lastPingAlert;
    private readonly QualityMonitor quality = new();
    private readonly List<QualityEvent> qualityEvents = [];
    private string? scheduleProfileAtStart;
    private int scheduleRestartRequested;
    internal static ScheduleSettings ScheduleOf(UserSettings settings) => new(settings.NightFree,
        TimeOnly.TryParseExact(settings.NightFrom, "HH:mm", out var from) ? from : new TimeOnly(1, 0),
        TimeOnly.TryParseExact(settings.NightTo, "HH:mm", out var to) ? to : new TimeOnly(8, 0),
        string.IsNullOrEmpty(settings.WeekendProfile) ? null : settings.WeekendProfile);
    /// <summary>Background share after the schedule: a weekend profile overrides the chosen profile.</summary>
    private double EffectiveBackgroundPercent(UserSettings settings) =>
        Schedule.Evaluate(ScheduleOf(settings), DateTime.Now).Profile is { } profile && ProfileBackgroundPercent.TryGetValue(profile, out var percent)
            ? percent : settings.BackgroundPercent;
    private void RecordQuality(QualityEvent value)
    {
        lock (qualityEvents) { qualityEvents.Add(value); if (qualityEvents.Count > 1000) qualityEvents.RemoveAt(0); }
        if (isolated) return;
        try
        {
            QualityEvent[] copy;
            lock (qualityEvents) copy = qualityEvents.ToArray();
            File.WriteAllText(Path.Combine(store.DirectoryPath, "quality.json"), JsonSerializer.Serialize(copy));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Log is optional. */ }
    }
    private IReadOnlyList<SessionSummary> LoadSessions()
    {
        try
        {
            var path = Path.Combine(store.DirectoryPath, "sessions.json");
            return File.Exists(path) && new FileInfo(path).Length < 256 * 1024
                ? JsonSerializer.Deserialize<List<SessionSummary>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
    public string WeeklyReportText()
    {
        QualityEvent[] events;
        lock (qualityEvents) events = qualityEvents.ToArray();
        return WeeklyReport.Build(LoadSessions(), events, DateTimeOffset.UtcNow);
    }
    public int QualityEventCount { get { lock (qualityEvents) return qualityEvents.Count; } }
    public string QualityLogCsv()
    {
        lock (qualityEvents) return "Beginn;Dauer (s);Art;Spitze (ms)" + Environment.NewLine + string.Join(Environment.NewLine, qualityEvents.Select(e => e.Csv));
    }
    public RouterGuideText RouterGuideFor() => lastRouterSample is { } router
        ? RouterGuide.For(router.Manufacturer, router.Model, router.RouterName) : RouterGuide.For(null, null, null);
    private readonly RouterTrafficMonitor? routerTraffic;
    private Task<RouterTrafficSample?>? routerSample;
    private RouterTrafficSample? lastRouterSample;
    private long routerSampleStarted;
    private SessionSummary? lastSession;
    internal static readonly IReadOnlyDictionary<string, double> ProfileBackgroundPercent =
        new Dictionary<string, double> { ["Turnier"] = 40, ["Standard"] = 65, ["Entspannt"] = 85 };
    public async Task ApplyProfileAsync(string profile)
    {
        if (!ProfileBackgroundPercent.TryGetValue(profile, out var percent)) throw new ArgumentException("Unbekanntes Profil.");
        await SaveSettingsAsync(Settings with { Profile = profile, BackgroundPercent = percent });
    }
    public async Task CompleteSetupGuideAsync()
    {
        if (Settings.SetupGuideDone) return;
        await lifecycle.WaitAsync();
        try { var next = Settings with { SetupGuideDone = true }; store.Save(next); Settings = next; }
        finally { lifecycle.Release(); }
    }
    private void RecordSession(SessionSummary summary)
    {
        lastSession = summary;
        if (isolated) return;
        try
        {
            var path = Path.Combine(store.DirectoryPath, "sessions.json");
            var list = File.Exists(path) && new FileInfo(path).Length < 256 * 1024
                ? JsonSerializer.Deserialize<List<SessionSummary>>(File.ReadAllText(path)) ?? [] : [];
            list.Insert(0, summary);
            File.WriteAllText(path, JsonSerializer.Serialize(list.Take(20)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { /* History is optional. */ }
    }
    internal static string AppVersion { get; } = (typeof(DynexRuntime).Assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(3);
    private TaskCompletionSource monitoringPulse = NewPulse();
    public bool IsSpeedTestWaiting { get; private set; }
    private long pausedUntilTicks;
    /// <summary>User pause of automatic and manual download limits. Not persisted: a restart ends it.</summary>
    public DateTimeOffset? PausedUntil
    {
        get { var ticks = Interlocked.Read(ref pausedUntilTicks); return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero); }
    }
    public void PauseLimits(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(duration));
        Interlocked.Exchange(ref pausedUntilTicks, DateTimeOffset.UtcNow.Add(duration).UtcTicks);
    }
    public void ResumeLimits() => Interlocked.Exchange(ref pausedUntilTicks, 0);
    public SpeedTestWaitReason SpeedTestWaitingFor { get; private set; }
    private static TaskCompletionSource NewPulse() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsSpeedTestRunning { get; private set; }
    public string SpeedTestMessage { get; private set; } = "";
    internal double? PlannedDownloadLimitMbps { get; private set; }
    public UserSettings Settings { get; private set; }
    internal int PeerPort { get; }
    public RuntimeSnapshot? Snapshot { get; private set; }
    public PeerNetworkDiagnostics? GetNetworkDiagnostics() => peers?.GetNetworkDiagnostics();
    internal async Task<NetworkConnectionCheck> CheckNetworkAsync(CancellationToken cancellationToken = default, bool requestDiscovery = false)
    {
        await networkCheckGate.WaitAsync(cancellationToken);
        try
        {
            if (requestDiscovery) peers?.RequestDiscovery();
            var result = await NetworkConnectionCheck.ReadAsync(isolated, cancellationToken,
                requestDiscovery ? SettingsStore.Split(Settings.DirectPeers).ToArray() : null);
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref networkConnection, result);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Volatile.Write(ref networkConnection, null);
            throw;
        }
        finally { networkCheckGate.Release(); }
    }
    private async Task RefreshFirewallAsync(CancellationToken token)
    {
        // Read-only; feeds the connection advice so "firewall missing" is not guessed from silence.
        try { if (!isolated) await CheckNetworkAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    /// <summary>Requests a fresh measurement from the existing monitor. Worker changes cancel pending requests.</summary>
    public Task<RuntimeSnapshot> RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new TaskCompletionSource<RuntimeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (refreshSync)
        {
            if (stopping || refreshRequests is null || !refreshRequests.Writer.TryWrite(request))
                throw new InvalidOperationException("Die Netzwerküberwachung läuft gerade nicht. Bitte erneut versuchen.");
        }
        return request.Task.WaitAsync(cancellationToken);
    }
    /// <summary>
    /// After standby Windows may hand back a socket whose multicast memberships, interface binding and
    /// peer sessions are stale. Rebuilding the worker starts a fresh signed session; peers reconnect within
    /// one or two heartbeats once both PCs are back on the same private network.
    /// </summary>
    public async Task ReconnectNetworkAsync()
    {
        await RefreshNetworkProfileAsync(reconnect: true);
    }
    public event Action<RuntimeSnapshot>? Updated;
    private static bool HasGameTraffic(GameDetectionResult game, IReadOnlyList<GameTrafficIdentity>? activity) =>
        game.ProcessId is > 0 && game.ProcessStartedAt is { } started &&
        activity?.Any(p => p.ProcessId == game.ProcessId && p.StartedAtUtcTicks == started.UtcTicks) == true;
    public DynexRuntime(string? profilePath = null) : this(profilePath, null) { }
    internal DynexRuntime(string? profilePath, ISpeedTestService? speedTest,
        Func<string, IReadOnlyList<GameDetectionResult>>? scanGames = null,
        Func<ForegroundProcess?>? readForeground = null, ShaperController? shaper = null,
        Func<IReadOnlyList<GameTrafficIdentity>?>? readGameTraffic = null,
        Func<NetworkIdentity?>? networkIdentityReader = null)
    {
        isolated = profilePath != null;
        networkProfilesEnabled = !isolated || networkIdentityReader is not null;
        readNetworkIdentity = networkIdentityReader ?? (isolated ? () => null : NetworkIdentityProbe.Read);
        this.shaper = shaper ?? new();
        shapingAllowed = !isolated || shaper is not null;
        PeerPort = isolated ? GetIsolatedPeerPort() : 39274;
        this.speedTest = speedTest ?? new SpeedTestService();
        this.scanGames = scanGames ?? GameDetector.ScanAll;
        this.readForeground = readForeground ?? ForegroundGame.Read;
        this.readGameTraffic = readGameTraffic ?? (() => this.shaper.Status.ActiveGameProcesses);
        automaticSpeedTestEnabled = !isolated || speedTest is not null;
        store = new(profilePath);
        // Isolated test profiles never contact the real router.
        routerTraffic = isolated ? null : new RouterTrafficMonitor();
        Settings = store.Load();
        if (!isolated)
        {
            try
            {
                var path = Path.Combine(store.DirectoryPath, "quality.json");
                if (File.Exists(path) && new FileInfo(path).Length < 1024 * 1024)
                    qualityEvents.AddRange(JsonSerializer.Deserialize<List<QualityEvent>>(File.ReadAllText(path)) ?? []);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        identity = store.LoadIdentity();
        InitializeDevices();
        InitializeUsageHistory();
    }
    private DynexSettings CoreSettings() => new()
    {
        DeviceId = Settings.AutoConnectLan ? AutoLanIdentity.GetDeviceId(identity.SigningPrivateKeyBase64!) : identity.DeviceId,
        DeviceName = Environment.MachineName, GroupKeyBase64 = identity.GroupKey,
        CapacityMeasured = Settings.CapacityMeasuredAtUtc is not null,
        UploadCapacityMbps = Settings.CapacityMeasuredAtUtc is not null ? Settings.UploadCapacityMbps : null,
        LineMbps = Settings.CapacityMbps, HeadroomPercent = 100 - EffectiveBackgroundPercent(Settings),
        GamingReserveMbps = Settings.ReservePerGamerMbps, ManualDownloadLimitMbps = Settings.ManualLimitMbps,
        SharedDownloadMaximumMbps = Settings.SharedDownloadMaximumMbps,
        AdaptiveEnabled = Settings.Adaptive, LoopbackOnly = isolated, Port = PeerPort, AppVersion = AppVersion, ShareWeight = Settings.SharePriority,
        // Test profiles may exercise real address editing/import, but must never send to that LAN.
        DirectPeers = isolated ? [] : SettingsStore.Split(Settings.DirectPeers).Select(ip => new IPEndPoint(IPAddress.Parse(ip), 39274)).ToArray()
    };
    private static int GetIsolatedPeerPort()
    {
        using var reservation = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
    }
    public async Task StartAsync()
    {
        while (true)
        {
            Task? finishing = null;
            await lifecycle.WaitAsync();
            try
            {
                if (started) return;
                if (speedTestTask is { IsCompleted: false } pending)
                {
                    lock (speedTestSync) speedTestCancellation?.Cancel();
                    finishing = pending;
                }
                else
                {
                    stopping = false;
                    var current = await Task.Run(readNetworkIdentity);
                    ApplyNetworkIdentity(current);
                    started = true;
                    await StartWorkerAsync();
                    StartNetworkSpeedTestIfNeeded(current);
                    return;
                }
            }
            finally { lifecycle.Release(); }
            await finishing!;
        }
    }
    public async Task MeasureCapacityAsync()
    {
        var observed = speedTestTask;
        var running = observed;
        if (running is { IsCompleted: false } && IsSpeedTestRunning) { await running; return; }
        await lifecycle.WaitAsync();
        try
        {
            if (!started || stopping) throw new InvalidOperationException("DYNEX wird gerade beendet.");
            if (GamingBlocksSpeedTest())
            {
                SpeedTestMessage = "Speedtest nicht gestartet: Auf diesem oder einem verbundenen PC ist ein Spiel offen. Nach dem Spielen erneut messen.";
                PublishSpeedTestSnapshot();
                return;
            }
            // A concurrent caller may wait behind the entire test. Share the test that
            // started since this call, even if it completed before this gate was acquired.
            running = speedTestTask is { } pending && (!pending.IsCompleted || !ReferenceEquals(pending, observed))
                ? pending : BeginSpeedTest();
        }
        finally { lifecycle.Release(); }
        await running;
    }
    // Called with the lifecycle gate held. The task waits for that gate before doing any work.
    private Task BeginSpeedTest(bool automatic = false)
    {
        lock (speedTestSync)
        {
            if (speedTestTask is { IsCompleted: false } pending) return pending;
            speedTestCancellation = new CancellationTokenSource();
            speedTestNetworkProfileId = Settings.ActiveNetworkProfileId;
            speedTestTask = MeasureCapacityCoreAsync(automatic, speedTestCancellation.Token);
        }
        IsSpeedTestWaiting = true;
        SpeedTestWaitingFor = SpeedTestWaitReason.Monitoring;
        SpeedTestMessage = "DYNEX prüft Spiele und die Verbindung zu deinen PCs …";
        PublishSpeedTestSnapshot();
        return speedTestTask;
    }
    private bool GamingBlocksSpeedTest(UserSettings? settings = null)
    {
        settings ??= Settings;
        return scanGames(settings.GameProcesses).Count != 0 ||
            peers?.Snapshot().Any(p => p.IsGaming || !string.IsNullOrEmpty(p.Game)) == true;
    }
    private void CancelSpeedTestForGaming()
    {
        lock (speedTestSync) speedTestCancellation?.Cancel();
    }
    private void PeerStatusChanged()
    {
        if (speedTestLoadStarted && GamingBlocksSpeedTest()) CancelSpeedTestForGaming();
        Interlocked.Exchange(ref monitoringPulse, NewPulse()).TrySetResult();
    }
    private async Task MeasureCapacityCoreAsync(bool automatic, CancellationToken token)
    {
        var gateHeld = false;
        var protectionPaused = false;
        try
        {
            while (true)
            {
                await lifecycle.WaitAsync(token);
                gateHeld = true;
                pendingGamingSettings = false;
                token.ThrowIfCancellationRequested();
                // An empty snapshot is meaningful only after discovery's heartbeat exchange.
                var discoveryAvailable = peerStartError is null && peers?.NetworkStatus is null && monitoringReady;
                var discoveryReady = discoveryAvailable && Stopwatch.GetElapsedTime(peerStartedAt) >= CoreSettings().HeartbeatInterval * (Settings.AutoConnectLan ? 2 : 1);
                var pulse = monitoringPulse.Task;
                if (discoveryReady && !GamingBlocksSpeedTest())
                {
                    IsSpeedTestWaiting = false;
                    SpeedTestWaitingFor = SpeedTestWaitReason.None;
                    IsSpeedTestRunning = true;
                    SpeedTestMessage = "Download-Speedtest läuft · maximal 10 Sekunden / 256 MiB. Die lokale Begrenzung pausiert.";
                    PublishSpeedTestSnapshot();
                    // Closing the helper waits for its exit, so a prior rate cannot throttle this test.
                    protectionPaused = true;
                    await shaper.StopAsync(requireExit: true);
                    token.ThrowIfCancellationRequested();
                    if (!pendingGamingSettings && !GamingBlocksSpeedTest())
                    {
                        var measuredNetwork = readNetworkIdentity()?.Id;
                        if (networkProfilesEnabled && (measuredNetwork is null || measuredNetwork != Settings.ActiveNetworkProfileId))
                            throw new InvalidOperationException("Das aktuelle Netzwerk ist noch keinem Profil sicher zugeordnet. Nach der Netzwerkerkennung erneut messen.");
                        if (automatic)
                        {
                            var attempted = Settings with { AutomaticSpeedTestAttempted = true };
                            store.Save(attempted);
                            Settings = attempted;
                        }
                        // Monitoring/peer reception remain alive and cancel only actual load.
                        speedTestLoadStarted = true;
                        var result = await speedTest.MeasureAsync(token);
                        token.ThrowIfCancellationRequested();
                        if (readNetworkIdentity()?.Id != measuredNetwork || Settings.ActiveNetworkProfileId != measuredNetwork)
                            throw new OperationCanceledException("Netzwerkwechsel während des Speedtests.", token);
                        if (GamingBlocksSpeedTest()) throw new OperationCanceledException(token);
                        var measured = SettingsStore.Normalize(Settings with
                        {
                            CapacityMbps = Math.Round(result.DownloadMbps, 2),
                            CapacityMeasuredAtUtc = DateTimeOffset.UtcNow,
                            IdleLatencyMs = result.IdleLatencyMs is { } idle ? Math.Round(idle, 1) : null,
                            LoadedLatencyMs = result.LoadedLatencyMs is { } loaded ? Math.Round(loaded, 1) : null,
                            // A blocked upload test keeps the previous upload measurement.
                            UploadCapacityMbps = result.UploadMbps ?? Settings.UploadCapacityMbps
                        });
                        SettingsStore.Validate(measured);
                        store.Save(measured);
                        Settings = measured;
                        SpeedTestMessage = "Gemessen und gespeichert. Bei einem Anschlusswechsel erneut messen.";
                        break;
                    }
                    // Gaming appeared during helper release, before any test was started.
                    // Restore protection and continue waiting without consuming the attempt.
                    IsSpeedTestRunning = false;
                    IsSpeedTestWaiting = true;
                    await StopWorkerAsync();
                    if (started && !stopping) await StartWorkerAsync();
                    protectionPaused = false;
                }
                var networkProblem = peerStartError ?? peers?.NetworkStatus;
                SpeedTestWaitingFor = networkProblem is not null ? SpeedTestWaitReason.Network
                    : !monitoringReady ? SpeedTestWaitReason.Monitoring
                    : !discoveryReady ? SpeedTestWaitReason.Discovery : SpeedTestWaitReason.OpenGame;
                SpeedTestMessage = SpeedTestWaitingFor switch
                {
                    SpeedTestWaitReason.Network => "Speedtest noch nicht möglich: " + networkProblem,
                    SpeedTestWaitReason.Monitoring => "DYNEX prüft Spiele und Netzwerk. Der Speedtest startet anschließend automatisch.",
                    SpeedTestWaitReason.Discovery => "DYNEX sucht die anderen PCs im Netzwerk. Der Speedtest startet nach dem Statusabgleich.",
                    _ => "Speedtest startet automatisch, sobald kein Spiel mehr offen ist. Auch ein Spiel im Hintergrund zählt."
                };
                PublishSpeedTestSnapshot();
                lifecycle.Release(); gateHeld = false;
                await pulse.WaitAsync(token);
            }
        }
        catch (OperationCanceledException)
        { SpeedTestMessage = "Speedtest abgebrochen. Der bisherige Wert bleibt erhalten. Nach dem Spielen kannst du manuell erneut messen."; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { SpeedTestMessage = $"Speedtest fehlgeschlagen: {ex.Message} Der bisherige Wert bleibt erhalten. Bitte erneut messen."; }
        finally
        {
            if (!gateHeld) await lifecycle.WaitAsync();
            try
            {
                speedTestLoadStarted = false;
                IsSpeedTestRunning = false;
                IsSpeedTestWaiting = false;
                SpeedTestWaitingFor = SpeedTestWaitReason.None;
                lock (speedTestSync) { speedTestCancellation?.Dispose(); speedTestCancellation = null; }
                // The artificial load must not become an adaptive latency baseline.
                if (protectionPaused)
                {
                    await StopWorkerAsync();
                    activePlanner = null; plannerPolicy = null;
                    if (started && !stopping) await StartWorkerAsync();
                }
                PublishSpeedTestSnapshot();
            }
            finally { lifecycle.Release(); }
        }
    }
    private void PublishSpeedTestSnapshot()
    {
        if (Snapshot is { } prior)
        {
            Snapshot = prior with
            {
                State = IsSpeedTestRunning ? "Internetgeschwindigkeit wird gemessen" : IsSpeedTestWaiting ? "Speedtest wartet" : "Speedtest beendet",
                Detail = SpeedTestMessage
            };
            Updated?.Invoke(Snapshot);
            return;
        }
        var emptyPing = new PingView(null, null, null, 0, "");
        HistoryPoint[] points;
        lock (history) points = history.ToArray();
        Snapshot = new RuntimeSnapshot(IsSpeedTestRunning ? "Internetgeschwindigkeit wird gemessen" : IsSpeedTestWaiting ? "Speedtest wartet" : "Speedtest beendet",
            SpeedTestMessage, false, "", false, shaper.Status.Message, null, null, null,
            emptyPing, emptyPing, [], points, "Netzwerküberwachung wird nach dem Speedtest fortgesetzt", Fingerprint(), shaper.IsAvailable);
        Updated?.Invoke(Snapshot);
    }
    private async Task StartWorkerAsync()
    {
        cancellation = new();
        peers = Settings.AutoConnectLan
            ? new AutoLanPeerService(CoreSettings(), identity.SigningPrivateKeyBase64!)
            : new PeerService(CoreSettings());
        ConfigureDevicePolicy(peers);
        string? networkError = null;
        monitoringReady = false;
        try { await peers.StartAsync(cancellation.Token); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException) { networkError = ex.Message; }
        peerStartError = networkError;
        peers.PeersChanged += PeerStatusChanged;
        peerStartedAt = Stopwatch.GetTimestamp();
        scheduleProfileAtStart = Schedule.Evaluate(ScheduleOf(Settings), DateTime.Now).Profile;
        Interlocked.Exchange(ref scheduleRestartRequested, 0);
        _ = RefreshFirewallAsync(cancellation.Token);
        var settings = Settings;
        var service = peers;
        var token = cancellation.Token;
        var policy = JsonSerializer.Serialize(new { settings.CapacityMbps, settings.UploadCapacityMbps, settings.CapacityMeasuredAtUtc, settings.BackgroundPercent, settings.ReservePerGamerMbps, settings.ManualLimitMbps, settings.SharedDownloadMaximumMbps, settings.Adaptive, settings.SharePriority, settings.RouterTarget, settings.ExternalTarget, settings.AutoConnectLan, Group = settings.AutoConnectLan ? "LAN" : Fingerprint() });
        if (activePlanner == null || plannerPolicy != policy)
        { activePlanner = new LimitPlanner(CoreSettings()); plannerPolicy = policy; }
        var planner = activePlanner;
        var requests = Channel.CreateUnbounded<TaskCompletionSource<RuntimeSnapshot>>(new() { SingleReader = true });
        lock (refreshSync) refreshRequests = requests;
        // Run even if cancellation arrived before scheduling: its finally must settle every request.
        worker = Task.Run(() => RunAsync(settings, service, planner, networkError, requests, token));
    }
    private async Task RunAsync(UserSettings settings, IPeerService service, LimitPlanner planner, string? initialNetworkError,
        Channel<TaskCompletionSource<RuntimeSnapshot>> requests, CancellationToken token)
    {
        var monitor = new NetworkMonitor();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        var pending = new List<TaskCompletionSource<RuntimeSnapshot>>();
        CancellationTokenSource? routerCancellation = null;
        string? routerRoute = null;
        try
        {
            var nextTick = timer.WaitForNextTickAsync(token).AsTask();
            while (!token.IsCancellationRequested)
            {
                while (requests.Reader.TryRead(out var queuedRequest)) pending.Add(queuedRequest);
                try
                {
                    var detectedGames = scanGames(settings.GameProcesses);
                    if (speedTestLoadStarted && (detectedGames.Count != 0 ||
                        service.Snapshot().Any(p => p.IsGaming || !string.IsNullOrEmpty(p.Game)))) CancelSpeedTestForGaming();
                    Interlocked.Exchange(ref monitoringPulse, NewPulse()).TrySetResult();
                    var measurement = await monitor.SampleAsync(settings, token);
                    if (routerTraffic is not null && routerRoute != measurement.RouteId)
                    {
                        routerCancellation?.Cancel();
                        if (routerSample is not null)
                        {
                            try { await routerSample; }
                            catch (Exception ex) when (ex is not OutOfMemoryException) { }
                        }
                        token.ThrowIfCancellationRequested();
                        routerCancellation?.Dispose();
                        routerCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                        routerTraffic.Reset();
                        routerRoute = measurement.RouteId;
                        routerSample = null;
                        lastRouterSample = null;
                        routerSampleStarted = 0;
                    }
                    monitoringReady = true;
                    // Resolve focus after awaiting probes so an Alt-Tab during ping does not
                    // publish a stale active game. All detected games still bypass shaping.
                    var foreground = readForeground();
                    var focusedGame = ForegroundGame.Find(detectedGames, foreground);
                    var early = GameDetector.ParseAdditionalNames(settings.EarlyLimitGames);
                    var candidates = settings.Mode == GamingMode.Off ? [] : GameProtection.FindCandidates(detectedGames, foreground, early);
                    var activity = readGameTraffic();
                    var protectedGame = candidates.FirstOrDefault(g => HasGameTraffic(g, activity));
                    var displayedGame = protectedGame ?? focusedGame ?? candidates.FirstOrDefault() ?? detectedGames.FirstOrDefault();
                    var game = displayedGame?.GameName ?? "";
                    var gaming = protectedGame is not null;
                    var waitingForTraffic = candidates.Count > 0 && !gaming;
                    var canObserve = candidates.Any(g => g.ProcessId > 0 && g.ProcessStartedAt is not null);
                    var schedule = Schedule.Evaluate(ScheduleOf(settings), DateTime.Now);
                    if (schedule.Profile != scheduleProfileAtStart && Interlocked.Exchange(ref scheduleRestartRequested, 1) == 0)
                        // Rebuild the planner with the scheduled profile; retried if a speed test blocked it.
                        _ = Task.Run(async () => { await ReconnectNetworkAsync(); Interlocked.Exchange(ref scheduleRestartRequested, 0); });
                    ScheduleNetworkProfileCheck();
                    var remote = service.Snapshot();
                    ObserveDevices(remote);
                    var now = DateTimeOffset.UtcNow;
                    RecordUsageSample(measurement, remote, now);
                    var latencyTarget = settings.ExternalTarget + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(measurement.RouteId)))[..16];
                    var limitedDownloadMbps = shaper.Status.LimitedDownloadMbps;
                    var decision = planner.Plan(gaming, remote, IsSpeedTestRunning ? null : new ProbeStats(now, measurement.External.Current,
                        measurement.Down, true, latencyTarget, LimitedDownloadMbps: limitedDownloadMbps), now);
                    var capacityAvailable = decision.SharedCapacityMbps is not null;
                    var capacityFromPeer = settings.CapacityMeasuredAtUtc is null && capacityAvailable;
                    if (!capacityAvailable)
                        decision = decision with { DownloadLimitMbps = null, SharedDownloadBudgetMbps = null, SharedCapacityMbps = null, SharedBudgetComplete = false };
                    // A manual value is a maximum while gaming, never an always-on limit.
                    if (!decision.GamingActive) decision = decision with { DownloadLimitMbps = null };
                    var pausedUntil = PausedUntil;
                    if (pausedUntil is { } until && until <= now) { Interlocked.CompareExchange(ref pausedUntilTicks, 0, until.UtcTicks); pausedUntil = null; }
                    var limitsOff = pausedUntil is not null || schedule.LimitsOff;
                    if (limitsOff) decision = decision with { DownloadLimitMbps = null };
                    PlannedDownloadLimitMbps = IsSpeedTestRunning ? null : decision.DownloadLimitMbps;
                    var plannedUpload = IsSpeedTestRunning || !capacityAvailable ? null
                        : UploadLimit.Compute(decision, settings.CapacityMeasuredAtUtc is not null ? settings.UploadCapacityMbps : null, EffectiveBackgroundPercent(settings),
                            settings.ManualUploadLimitMbps, settings.UploadLimitEnabled, limitsOff);
                    if (settings.DriverEnabled && shapingAllowed && !IsSpeedTestRunning)
                        await shaper.ApplyAsync(PlannedDownloadLimitMbps, token, detectedGames, qosEnabled: decision.GamingActive,
                            protectionStillNeeded: () => service.Snapshot().Any(p => p.IsGaming) ||
                                settings.Mode != GamingMode.Off && GameProtection.FindCandidates(
                                    scanGames(settings.GameProcesses), readForeground(), early).Any(g => HasGameTraffic(g, readGameTraffic())),
                            uploadMbps: plannedUpload, observeGameTraffic: canObserve,
                            observationStillNeeded: () => settings.Mode != GamingMode.Off && GameProtection.FindCandidates(
                                scanGames(settings.GameProcesses), readForeground(), early).Any(g => g.ProcessId > 0 && g.ProcessStartedAt is not null),
                            appRules: limitsOff ? [] : settings.ApplicationRules,
                            applicationRulesStillNeeded: () => !stopping && !IsSpeedTestRunning && Settings.DriverEnabled &&
                                PausedUntil is null && !Schedule.Evaluate(ScheduleOf(Settings), DateTime.Now).LimitsOff);
                    // Read the release flag before the status: a release completing in between then shows
                    // up as "not yet idle" for one cycle instead of "idle but QoS still active".
                    var captureReleased = shaper.CaptureReleased;
                    var driver = shaper.Status;
                    // Idle devices have a prospective share; they do not consume a
                    // downloader's allocation. Legacy/unknown activity keeps the all-PC divisor.
                    var downloadWeight = decision.DownloadShareWeightTotal ?? decision.ShareWeightTotal ?? decision.ParticipantCount;
                    var activeDownloadWeight = downloadWeight - decision.LocalShareWeight +
                        (limitedDownloadMbps > .05 ? decision.LocalShareWeight : 0);
                    var knownWeights = remote.All(p => p.ShareWeight is >= .25 and <= 4 && double.IsFinite(p.ShareWeight.Value));
                    double? ShareFor(double weight, bool local, bool downloading) => decision.SharedDownloadBudgetMbps is { } pool
                        ? Math.Floor(pool * weight / (decision.DownloadActivityComplete
                            ? local ? downloadWeight : activeDownloadWeight + (downloading ? 0 : weight)
                            : decision.ShareWeightTotal ?? decision.ParticipantCount) * 100) / 100 : null;
                    bool ConfirmedShare(bool active, double? applied, double weight, bool local, bool downloading) => active && applied is { } rate &&
                        double.IsFinite(rate) && rate > 0 && (ShareFor(weight, local, downloading) is not { } share || rate <= share + .000001);
                    service.Publish(gaming, measurement.Down, measurement.External.Current, driver.Mbps, game[..Math.Min(80, game.Length)], driver.Active,
                        decision.IdleBaselineMs, latencyTarget, decision.IdleBaselineAtUtc, uploadMbps: measurement.Up,
                        topProgram: driver.TopPrograms?.FirstOrDefault()?.Name, limitedDownloadMbps: driver.LimitedDownloadMbps);
                    var devices = new List<DeviceView> { new(settings.DeviceName + " · dieser PC", measurement.Adapter, gaming, game, driver.Mbps, driver.Active ? "Begrenzt" : gaming ? "Gaming" : "Unbegrenzt", measurement.Down, measurement.Up,
                        driver.TopPrograms?.FirstOrDefault()?.Name) };
                    var aliases = Devices.ToDictionary(d => d.DeviceId, d => d.DisplayName);
                    devices.AddRange(remote.Select(p => new DeviceView(aliases.GetValueOrDefault(p.DeviceId, p.DeviceName), p.Address ?? "LAN", p.IsGaming, p.Game ?? "", p.ShaperActive ? p.AppliedLimitMbps : null, p.ShaperActive ? "Begrenzt" : p.IsGaming ? "Gaming" : "Unbegrenzt", p.DownloadMbps, p.UploadMbps, p.TopProgram)));
                    if (settings.PingAlarm && pingAlarm.Observe(measurement.External.Current, gaming,
                        devices.Select(d => new LoadSource(d.Name, d.DownloadMbps, d.UploadMbps, d.TopProgram)).ToArray(), now) is { } alert)
                        lastPingAlert = alert;
                    if (quality.Observe(measurement.External.Current, measurement.Router.Current, now) is { } qualityEvent) RecordQuality(qualityEvent);
                    var state = driver.Active ? "Downloads werden begrenzt" : gaming ? "Gaming-Modus aktiv" : decision.GamingActive ? "Gaming im LAN erkannt"
                        : waitingForTraffic ? "Warte auf Spielverkehr" : "Bereit für deine nächste Runde";
                    var detail = gaming
                        ? "Große Downloads werden auf diesem und verbundenen DYNEX-PCs begrenzt. Spiele und Sprachchat bleiben frei."
                        : decision.AdaptiveReduced ? "Der Ping ist gestiegen. DYNEX bremst die Downloads stärker."
                        : decision.GamingActive ? "Ein anderer PC spielt. Deine Downloads sollen langsamer weiterlaufen."
                        : waitingForTraffic ? (driver.ObservingGameTraffic && canObserve
                            ? "Kein Spielverkehr erkannt. Downloads und Uploads bleiben frei; DYNEX beobachtet weiter."
                            : "Spielverkehr noch nicht messbar. Downloads und Uploads bleiben frei. " + driver.Message)
                        : captureReleased ? "Kein Spiel im Fokus. Der Netzwerkfilter ist inaktiv."
                        : "Kein Spiel im Fokus. DYNEX gibt den Netzwerkfilter frei: " + driver.Message;
                    if (!capacityAvailable)
                        detail = decision.GamingActive
                            ? "Downloads bleiben frei, bis ein Speedtest erfolgreich gespeichert wurde. Spiele und Sprachchat können beim Spielen bereits priorisiert werden."
                            : "Downloads bleiben frei, bis ein Speedtest erfolgreich gespeichert wurde. " + detail;
                    if (IsSpeedTestRunning) { state = "Internetgeschwindigkeit wird gemessen"; detail = SpeedTestMessage; }
                    else if (IsSpeedTestWaiting && !capacityFromPeer) detail = SpeedTestMessage;
                    else if (capacityFromPeer) detail += " Leitung von verbundenem PC übernommen; keine eigene Messung gespeichert.";
                    if (decision.DownloadLimitMbps.HasValue && !driver.Active && !IsSpeedTestRunning)
                        detail = "Gaming erkannt. Die Downloadbegrenzung ist noch nicht aktiv: " + driver.Message;
                    if (driver.ApplicationRulesActive && !IsSpeedTestRunning)
                    {
                        state = "Programmregeln aktiv";
                        detail = "Deine Programme werden nach ihren eigenen Download- und Uploadregeln begrenzt.";
                    }
                    // Router discovery can take seconds; never let it delay game detection or limits.
                    // Every six seconds is enough for a home overview and spares the router's CPU.
                    if (routerSample is { IsCompleted: true })
                    {
                        lastRouterSample = routerSample.IsCompletedSuccessfully ? routerSample.Result : null;
                        _ = routerSample.Exception;
                        routerSample = null;
                    }
                    if (routerTraffic is not null && routerSample is not { IsCompleted: false } &&
                        (routerSampleStarted == 0 || Stopwatch.GetElapsedTime(routerSampleStarted) >= TimeSpan.FromSeconds(6)))
                    {
                        routerSampleStarted = Stopwatch.GetTimestamp();
                        routerSample = routerTraffic.SampleAsync(routerCancellation?.Token ?? token);
                    }
                    var homeTraffic = lastRouterSample;
                    // An open game remains visible in playtime history even while its network is idle.
                    var playingGame = protectedGame ?? focusedGame ?? candidates.FirstOrDefault();
                    if (sessions.Observe(settings.Mode != GamingMode.Off && playingGame is not null,
                        playingGame?.GameName ?? "", measurement.External.Current, driver.Active, now) is { } ended) RecordSession(ended);
                    var point = new HistoryPoint(now, measurement.Router.Current, measurement.External.Current, measurement.Down, driver.Active || remote.Any(p => p.ShaperActive));
                    lock (history) { history.Add(point); if (history.Count > 300) history.RemoveAt(0); }
                    HistoryPoint[] points;
                    lock (history) points = history.ToArray();
                    var networkState = initialNetworkError ?? service.NetworkStatus;
                    if (DeviceRegistryError is { } registryError) networkState = string.Join(" · ", new[] { networkState, registryError }.Where(s => !string.IsNullOrEmpty(s)));
                    var diagnostics = service.GetNetworkDiagnostics();
                    var connection = Volatile.Read(ref networkConnection);
                    var limitReason = LimitExplanation.Describe(decision, gaming, protectedGame?.GameName, remote,
                        capacityAvailable, IsSpeedTestRunning, settings.DriverEnabled && shapingAllowed,
                        driver.Active, settings.ManualLimitMbps, driver.Mbps, pausedUntil);
                    if (schedule.LimitsOff && pausedUntil is null && decision.GamingActive) limitReason = schedule.Reason + " · Downloads frei";
                    if (waitingForTraffic && !decision.GamingActive && !IsSpeedTestRunning && pausedUntil is null)
                        limitReason = driver.ObservingGameTraffic && canObserve ? "Kein Spielverkehr · kein Limit" : "Spielverkehr nicht bestätigt · kein Limit";
                    var snapshot = new RuntimeSnapshot(state, detail, gaming, game, driver.Active,
                        driver.Message, driver.Mbps, measurement.Down, measurement.Up, measurement.Router, measurement.External, devices, points,
                        networkState ?? $"{remote.Count} verbundene PCs · {(settings.AutoConnectLan ? "Automatisch im privaten LAN" : "Private Gruppe")}", Fingerprint(), shaper.IsAvailable,
                        decision.SharedDownloadBudgetMbps, decision.ParticipantCount, decision.GamingCount,
                        decision.SharedCapacityMbps, decision.SharedBudgetComplete,
                        (ConfirmedShare(driver.Active, driver.Mbps, decision.LocalShareWeight, true, limitedDownloadMbps > .05) ? 1 : 0) +
                        remote.Count(p => ConfirmedShare(p.ShaperActive, p.AppliedLimitMbps, knownWeights ? p.ShareWeight!.Value : 1,
                            false, p.LimitedDownloadMbps > .05)),
                        driver.QosActive, driver.GamePriorityPackets, driver.VoicePriorityPackets, networkState,
                        ProtectionIdle: !decision.GamingActive && captureReleased && !driver.ApplicationRulesActive,
                        NetworkDiagnostics: diagnostics, LimitReason: limitReason,
                        Connection: diagnostics.Assess(connection?.FirewallConfigured, connection?.Lan), PausedUntil: pausedUntil, LastSession: lastSession, HomeTraffic: homeTraffic, UploadLimitMbps: driver.UploadMbps,
                        PingAlert: lastPingAlert, TopPrograms: driver.TopPrograms,
                        ScheduleReason: schedule.Reason, WaitingForGameTraffic: waitingForTraffic,
                        ObservingGameTraffic: driver.ObservingGameTraffic && canObserve,
                        ApplicationRulesActive: driver.ApplicationRulesActive,
                        SharedUploadCapacityMbps: decision.SharedUploadCapacityMbps, CapacityFromPeer: capacityFromPeer,
                        DownloadActivityComplete: decision.DownloadActivityComplete);
                    Snapshot = snapshot;
                    Updated?.Invoke(snapshot);
                    foreach (var request in pending) request.TrySetResult(snapshot);
                    pending.Clear();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    monitoringReady = false;
                    lastRouterSample = null;
                    PlannedDownloadLimitMbps = null;
                    if (IsSpeedTestRunning) CancelSpeedTestForGaming();
                    // Monitoring failure must not keep an old automatic rate active.
                    await shaper.ApplyAsync(null, token);
                    service.Publish(false);
                    var emptyPing = new PingView(null, null, null, 0, "");
                    var prior = Snapshot ?? new RuntimeSnapshot("", "", false, "", false, "", null, 0, 0, emptyPing, emptyPing, [], [], initialNetworkError ?? service.NetworkStatus ?? "", Fingerprint());
                    Snapshot = prior with { State = "Messung unterbrochen", Detail = ex.Message, IsGaming = false, DriverActive = false, LimitMbps = null, DownloadMbps = null, UploadMbps = null, Devices = [], Router = emptyPing, External = emptyPing, DriverState = shaper.Status.Message,
                        SharedDownloadBudgetMbps = null, ParticipantCount = 1, GamingCount = 0, SharedCapacityMbps = null, SharedBudgetComplete = false, ConfirmedParticipantCount = 0,
                        QosActive = false, GamePriorityPackets = 0, VoicePriorityPackets = 0, ProtectionIdle = false, NetworkDiagnostics = null,
                        LimitReason = "Messung unterbrochen · kein Limit aktiv", Connection = null, HomeTraffic = null,
                        WaitingForGameTraffic = false, ObservingGameTraffic = false, SharedUploadCapacityMbps = null, CapacityFromPeer = false,
                        DownloadActivityComplete = false };
                    Updated?.Invoke(Snapshot);
                    foreach (var request in pending) request.TrySetException(ex);
                    pending.Clear();
                }
                // Keep a single outstanding timer wait when an explicit request wakes the worker early.
                using var wakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                var requested = requests.Reader.WaitToReadAsync(wakeCancellation.Token).AsTask();
                await Task.WhenAny(nextTick, requested);
                token.ThrowIfCancellationRequested();
                if (nextTick.IsCompleted)
                {
                    if (!await nextTick) break;
                    nextTick = timer.WaitForNextTickAsync(token).AsTask();
                }
                wakeCancellation.Cancel();
                try { await requested; }
                catch (OperationCanceledException) when (wakeCancellation.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            lock (refreshSync)
            {
                if (ReferenceEquals(refreshRequests, requests)) refreshRequests = null;
                requests.Writer.TryComplete();
            }
            while (requests.Reader.TryRead(out var queuedRequest)) pending.Add(queuedRequest);
            foreach (var request in pending) request.TrySetCanceled();
            routerCancellation?.Cancel();
            if (routerSample is not null)
            {
                try { await routerSample; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
            }
            routerCancellation?.Dispose();
            routerSample = null;
            lastRouterSample = null;
        }
    }
    public async Task SaveBetaUpdatePreferenceAsync(bool enabled)
    {
        await lifecycle.WaitAsync();
        try
        {
            var next = Settings with { BetaUpdates = enabled };
            store.Save(next);
            Settings = next;
        }
        finally { lifecycle.Release(); }
    }
    /// <summary>Portable settings backup. Contains no keys; measured capacity stays specific to this PC.</summary>
    public string ExportSettings() => JsonSerializer.Serialize(new SettingsBackup(1, AppVersion, Settings with
    {
        CapacityMbps = 200, CapacityMeasuredAtUtc = null, AutomaticSpeedTestAttempted = false, IdleLatencyMs = null, LoadedLatencyMs = null,
        UploadCapacityMbps = null, ActiveNetworkProfileId = null, NetworkProfiles = []
    }), new JsonSerializerOptions { WriteIndented = true });
    public async Task ImportSettingsAsync(string json)
    {
        if (json.Length > 256 * 1024) throw new ArgumentException("Die Sicherungsdatei ist zu groß.");
        SettingsBackup? backup;
        try { backup = JsonSerializer.Deserialize<SettingsBackup>(json); }
        catch (JsonException) { throw new ArgumentException("Die Datei ist keine DYNEX-Einstellungssicherung."); }
        if (backup is not { Format: 1, Settings: { } imported }) throw new ArgumentException("Die Datei ist keine DYNEX-Einstellungssicherung.");
        var current = Settings;
        // Keep this PC's own identity-like values: name, measured line and the once-only speed test marker.
        await SaveSettingsAsync(imported with
        {
            ActiveNetworkProfileId = current.ActiveNetworkProfileId, NetworkProfiles = current.NetworkProfiles,
            DeviceName = current.DeviceName, CapacityMbps = current.CapacityMbps,
            CapacityMeasuredAtUtc = current.CapacityMeasuredAtUtc, AutomaticSpeedTestAttempted = current.AutomaticSpeedTestAttempted,
            IdleLatencyMs = current.IdleLatencyMs, LoadedLatencyMs = current.LoadedLatencyMs, UploadCapacityMbps = current.UploadCapacityMbps
        });
    }
    private sealed record SettingsBackup(int Format, string Version, UserSettings Settings);
    public async Task SaveAutoUpdatePreferenceAsync(bool enabled)
    {
        await lifecycle.WaitAsync();
        try
        {
            var next = Settings with { AutoUpdate = enabled };
            store.Save(next);
            Settings = next;
        }
        finally { lifecycle.Release(); }
    }
    public async Task SaveSettingsAsync(UserSettings settings)
    {
        settings = SettingsStore.Normalize(settings);
        SettingsStore.Validate(settings);
        if (IsSpeedTestRunning && GamingBlocksSpeedTest(settings))
        {
            pendingGamingSettings = true;
            if (speedTestLoadStarted) CancelSpeedTestForGaming();
        }
        await lifecycle.WaitAsync();
        try
        {
            var prior = Settings;
            if (settings.ActiveNetworkProfileId != prior.ActiveNetworkProfileId)
                throw new InvalidOperationException("Das Netzwerk hat gewechselt. Einstellungen neu laden und erneut speichern.");
            settings = settings with { NetworkProfiles = prior.NetworkProfiles };
            // The once-only startup marker is monotonic, even when a UI action
            // captured older settings before the first measurement began.
            if (prior.AutomaticSpeedTestAttempted)
                settings = settings with { AutomaticSpeedTestAttempted = true };
            // A settings action queued during the test must not restore its old capacity.
            if (settings.CapacityMeasuredAtUtc != prior.CapacityMeasuredAtUtc)
                settings = SettingsStore.Normalize(settings with { CapacityMbps = prior.CapacityMbps, CapacityMeasuredAtUtc = prior.CapacityMeasuredAtUtc,
                    IdleLatencyMs = prior.IdleLatencyMs, LoadedLatencyMs = prior.LoadedLatencyMs, UploadCapacityMbps = prior.UploadCapacityMbps });
            settings = SettingsStore.Normalize(settings);
            store.Save(settings);
            try { if (!isolated && settings.AutoStart != prior.AutoStart) SetAutostart(settings.AutoStart); }
            catch { store.Save(prior); throw; }
            await StopWorkerAsync();
            if (!settings.DriverEnabled) await shaper.StopAsync();
            Settings = settings;
            if (prior.RouterTarget != settings.RouterTarget || prior.ExternalTarget != settings.ExternalTarget)
                lock (history) history.Clear();
            if (started && !stopping) await StartWorkerAsync();
        }
        finally { lifecycle.Release(); }
    }
    // Update only the connection controls, from the latest settings under the lifecycle gate.
    // Unsaved game/process preferences and a concurrent speed-test result must remain untouched.
    internal async Task SaveNetworkSettingsAsync(bool? autoConnectLan = null, string? directPeers = null)
    {
        var expectedProfileId = Settings.ActiveNetworkProfileId;
        await lifecycle.WaitAsync();
        try
        {
            if (expectedProfileId != Settings.ActiveNetworkProfileId)
                throw new InvalidOperationException("Das Netzwerk hat gewechselt. Verbindungseinstellungen neu laden.");
            var next = SettingsStore.Normalize(Settings with
            {
                AutoConnectLan = autoConnectLan ?? Settings.AutoConnectLan,
                DirectPeers = directPeers ?? Settings.DirectPeers
            });
            SettingsStore.Validate(next);
            if (next == Settings) return;
            store.Save(next);
            await StopWorkerAsync();
            Settings = next;
            if (started && !stopping) await StartWorkerAsync();
        }
        finally { lifecycle.Release(); }
    }
    internal string ExportPairingCode() => "DYNEX1:" + identity.GroupKey;
    public async Task<string> ActivatePairingCodeAsync()
    {
        var expectedProfileId = Settings.ActiveNetworkProfileId;
        await lifecycle.WaitAsync();
        try
        {
            if (expectedProfileId != Settings.ActiveNetworkProfileId)
                throw new InvalidOperationException("Das Netzwerk hat gewechselt. Verbindungseinstellungen neu laden.");
            // Both ends must use the authenticated private-group protocol. Merely
            // copying the key while AutoLAN remains active leaves incompatible peers.
            if (Settings.AutoConnectLan)
            {
                var privateSettings = Settings with { AutoConnectLan = false };
                store.Save(privateSettings);
                await StopWorkerAsync();
                await shaper.ApplyAsync(null, CancellationToken.None);
                Settings = privateSettings;
                if (started && !stopping) await StartWorkerAsync();
            }
            // Include a unicast route for home networks that suppress multicast.
            var invitation = new PairingInvitation(identity.GroupKey,
                isolated ? [] : LanDiscovery.GetPrivateLocalAddresses().ToArray());
            return "DYNEX2:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(invitation));
        }
        finally { lifecycle.Release(); }
    }
    public async Task ImportPairingCodeAsync(string code)
    {
        var expectedProfileId = Settings.ActiveNetworkProfileId;
        if (code.Length > 4096) throw new ArgumentException("Der Gruppencode ist zu lang.");
        var key = code.Trim();
        string[] addresses = [];
        if (key.StartsWith("DYNEX2:", StringComparison.Ordinal))
        {
            try
            {
                var invitation = JsonSerializer.Deserialize<PairingInvitation>(Convert.FromBase64String(key[7..]));
                if (invitation?.Key is null || invitation.Addresses is null || invitation.Addresses.Length > 16)
                    throw new ArgumentException("Der Gruppencode ist ungültig.");
                key = invitation.Key;
                addresses = invitation.Addresses.Select(address =>
                    IPAddress.TryParse(address, out var ip) && SettingsStore.IsPrivate(ip)
                        ? ip.ToString() : throw new ArgumentException("Der Gruppencode enthält keine gültige private IPv4-Adresse.")).Distinct().ToArray();
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            { throw new ArgumentException("Der Gruppencode ist ungültig."); }
        }
        if (key.StartsWith("DYNEX1:", StringComparison.Ordinal)) key = key[7..];
        byte[] bytes;
        try { bytes = Convert.FromBase64String(key); } catch (FormatException) { throw new ArgumentException("Der Gruppencode ist ungültig."); }
        try { if (bytes.Length != 32) throw new ArgumentException("Der Gruppencode benötigt einen 256-Bit-Schlüssel."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        await lifecycle.WaitAsync();
        try
        {
            if (expectedProfileId != Settings.ActiveNetworkProfileId)
                throw new InvalidOperationException("Das Netzwerk hat gewechselt. Verbindungseinstellungen neu laden.");
            var replacement = identity with { GroupKey = key };
            // A failed disk/DPAPI write must leave the currently running group intact.
            var privateSettings = Settings with { AutoConnectLan = false,
                DirectPeers = string.Join(Environment.NewLine, SettingsStore.Split(Settings.DirectPeers).Concat(addresses).Distinct()) };
            SettingsStore.Validate(privateSettings);
            store.SaveIdentity(replacement);
            try { store.Save(privateSettings); }
            catch { store.SaveIdentity(identity); throw; }
            await StopWorkerAsync();
            await shaper.ApplyAsync(null, CancellationToken.None);
            identity = replacement;
            Settings = privateSettings;
            if (started && !stopping) await StartWorkerAsync();
        }
        finally { lifecycle.Release(); }
    }
    private sealed record PairingInvitation(string Key, string[] Addresses);
    private string Fingerprint() => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(identity.GroupKey)))[..16];
    public async Task<string> ExportDiagnosticsAsync()
    {
        var path = Path.Combine(store.DirectoryPath, "Diagnose-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
        var snapshot = Snapshot;
        var networkEnvironment = await NetworkConnectionCheck.ReadAsync(isolated);
        var points = snapshot?.History ?? [];
        object Summarize(bool protection)
        {
            var samples = points.Where(p => p.Protection == protection).ToArray();
            var external = samples.Select(p => p.ExternalMs).Where(p => p.HasValue).Select(p => p!.Value).ToArray();
            var router = samples.Select(p => p.RouterMs).Where(p => p.HasValue).Select(p => p!.Value).ToArray();
            var changes = samples.Zip(samples.Skip(1)).Where(p => (p.Second.Time - p.First.Time).TotalSeconds < 5 && p.First.ExternalMs.HasValue && p.Second.ExternalMs.HasValue).Select(p => Math.Abs(p.Second.ExternalMs!.Value - p.First.ExternalMs!.Value)).ToArray();
            return new { Count = samples.Length, ExternalAverageMs = external.Length > 0 ? external.Average() : (double?)null, RouterAverageMs = router.Length > 0 ? router.Average() : (double?)null,
                ExternalJitterMs = changes.Length > 0 ? changes.Average() : (double?)null, IcmpLossPercent = samples.Length > 0 ? 100.0 * (samples.Length - external.Length) / samples.Length : (double?)null };
        }
        Directory.CreateDirectory(store.DirectoryPath);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            Version = AppVersion, CapturedAtUtc = DateTimeOffset.UtcNow, Settings, Snapshot = snapshot,
            NetworkTransport = GetNetworkDiagnostics(), NetworkEnvironment = networkEnvironment,
            ConnectionAdvice = GetNetworkDiagnostics()?.Assess(networkEnvironment.FirewallConfigured),
            WithoutConfirmedLimits = Summarize(false), WithConfirmedLimits = Summarize(true),
            RecentSessions = File.Exists(Path.Combine(store.DirectoryPath, "sessions.json")) ? File.ReadAllText(Path.Combine(store.DirectoryPath, "sessions.json")) : null,
            LatestCrash = CrashLog.Reports().FirstOrDefault() is { } crash ? File.ReadAllText(crash) : null,
            Note = "Keine Schlüssel. NIC-Verkehr enthält LAN. ICMP-Ziel ist kein Spielservernachweis. Beobachtungsvergleich, kein kausaler Bufferbloat-Beweis."
        }, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
    internal void ApplyAutostartPreference()
    {
        if (!isolated) SetAutostart(Settings.AutoStart);
    }
    private static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("DYNEX", "\"" + Path.Combine(AppContext.BaseDirectory, "DYNEX.exe") + "\" --tray");
        else key.DeleteValue("DYNEX", false);
    }
    private async Task StopWorkerAsync()
    {
        lock (refreshSync)
        {
            refreshRequests?.Writer.TryComplete();
            refreshRequests = null;
        }
        cancellation?.Cancel();
        if (worker != null) { try { await worker; } catch (OperationCanceledException) { } }
        FlushUsageHistory();
        FlushDevices();
        if (peers != null) { peers.PeersChanged -= PeerStatusChanged; await peers.DisposeAsync(); peers = null; }
        cancellation?.Dispose(); cancellation = null; worker = null;
        PlannedDownloadLimitMbps = null;
        // Peer-derived values belong to this running network session, never to
        // persistent settings or a later connection using another scope.
        if (Snapshot is { } prior)
            Snapshot = prior with { SharedDownloadBudgetMbps = null, SharedCapacityMbps = null,
                SharedUploadCapacityMbps = null, SharedBudgetComplete = false, CapacityFromPeer = false, DownloadActivityComplete = false };
    }
    public async Task StopAsync(bool requireHelperExit = false)
    {
        stopping = true;
        // Cancel before taking the gate: the running test owns it until its streams close.
        lock (speedTestSync) speedTestCancellation?.Cancel();
        await lifecycle.WaitAsync();
        try { started = false; await StopWorkerAsync(); await shaper.StopAsync(requireExit: requireHelperExit); }
        finally { lifecycle.Release(); }
        var pending = speedTestTask;
        if (pending is { IsCompleted: false }) await pending;
    }
    public async ValueTask DisposeAsync() { await StopAsync(); routerTraffic?.Dispose(); }
}
