using System.IO;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Dynex.Core;
using Dynex.Desktop.Games;
using Microsoft.Win32;

namespace Dynex.Desktop;

public sealed class DynexRuntime : IAsyncDisposable
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
    private bool started;
    private LimitPlanner? activePlanner;
    private string? plannerPolicy;
    private readonly bool isolated;
    private readonly ISpeedTestService speedTest;
    private readonly Func<string, IReadOnlyList<GameDetectionResult>> scanGames;
    private readonly Func<ForegroundProcess?> readForeground;
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
    private TaskCompletionSource monitoringPulse = NewPulse();
    public bool IsSpeedTestWaiting { get; private set; }
    public SpeedTestWaitReason SpeedTestWaitingFor { get; private set; }
    private static TaskCompletionSource NewPulse() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsSpeedTestRunning { get; private set; }
    public string SpeedTestMessage { get; private set; } = "";
    internal double? PlannedDownloadLimitMbps { get; private set; }
    public UserSettings Settings { get; private set; }
    internal int PeerPort { get; }
    public RuntimeSnapshot? Snapshot { get; private set; }
    public PeerNetworkDiagnostics? GetNetworkDiagnostics() => peers?.GetNetworkDiagnostics();
    internal Task<NetworkConnectionCheck> CheckNetworkAsync() => NetworkConnectionCheck.ReadAsync(isolated);
    public event Action<RuntimeSnapshot>? Updated;
    public DynexRuntime(string? profilePath = null) : this(profilePath, null) { }
    internal DynexRuntime(string? profilePath, ISpeedTestService? speedTest,
        Func<string, IReadOnlyList<GameDetectionResult>>? scanGames = null,
        Func<ForegroundProcess?>? readForeground = null, ShaperController? shaper = null)
    {
        isolated = profilePath != null;
        this.shaper = shaper ?? new();
        shapingAllowed = !isolated || shaper is not null;
        PeerPort = isolated ? GetIsolatedPeerPort() : 39274;
        this.speedTest = speedTest ?? new SpeedTestService();
        this.scanGames = scanGames ?? GameDetector.ScanAll;
        this.readForeground = readForeground ?? ForegroundGame.Read;
        automaticSpeedTestEnabled = !isolated || speedTest is not null;
        store = new(profilePath);
        Settings = store.Load();
        identity = store.LoadIdentity();
    }
    private DynexSettings CoreSettings() => new()
    {
        DeviceId = Settings.AutoConnectLan ? AutoLanIdentity.GetDeviceId(identity.SigningPrivateKeyBase64!) : identity.DeviceId,
        DeviceName = Environment.MachineName, GroupKeyBase64 = identity.GroupKey,
        CapacityMeasured = Settings.CapacityMeasuredAtUtc is not null,
        LineMbps = Settings.CapacityMbps, HeadroomPercent = 100 - Settings.BackgroundPercent,
        GamingReserveMbps = Settings.ReservePerGamerMbps, ManualDownloadLimitMbps = Settings.ManualLimitMbps,
        AdaptiveEnabled = Settings.Adaptive, LoopbackOnly = isolated, Port = PeerPort,
        DirectPeers = SettingsStore.Split(Settings.DirectPeers).Select(ip => new IPEndPoint(IPAddress.Parse(ip), 39274)).ToArray()
    };
    private static int GetIsolatedPeerPort()
    {
        using var reservation = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
    }
    public async Task StartAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (started) return;
            stopping = false;
            started = true;
            await StartWorkerAsync();
            if (automaticSpeedTestEnabled && !Settings.AutomaticSpeedTestAttempted && Settings.CapacityMeasuredAtUtc is null)
                _ = BeginSpeedTest(automatic: true);
        }
        finally { lifecycle.Release(); }
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
        IsSpeedTestWaiting = true;
        SpeedTestWaitingFor = SpeedTestWaitReason.Monitoring;
        SpeedTestMessage = "DYNEX prüft Spiele und die Verbindung zu deinen PCs …";
        lock (speedTestSync) speedTestCancellation = new CancellationTokenSource();
        speedTestTask = MeasureCapacityCoreAsync(automatic, speedTestCancellation.Token);
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
                        if (GamingBlocksSpeedTest()) throw new OperationCanceledException(token);
                        var measured = SettingsStore.Normalize(Settings with
                        {
                            CapacityMbps = Math.Round(result.DownloadMbps, 2),
                            CapacityMeasuredAtUtc = DateTimeOffset.UtcNow
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
        string? networkError = null;
        monitoringReady = false;
        try { await peers.StartAsync(cancellation.Token); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException) { networkError = ex.Message; }
        peerStartError = networkError;
        peers.PeersChanged += PeerStatusChanged;
        peerStartedAt = Stopwatch.GetTimestamp();
        var settings = Settings;
        var service = peers;
        var token = cancellation.Token;
        var policy = JsonSerializer.Serialize(new { settings.CapacityMbps, settings.CapacityMeasuredAtUtc, settings.BackgroundPercent, settings.ReservePerGamerMbps, settings.ManualLimitMbps, settings.Adaptive, settings.RouterTarget, settings.ExternalTarget, settings.AutoConnectLan, Group = settings.AutoConnectLan ? "LAN" : Fingerprint() });
        if (activePlanner == null || plannerPolicy != policy)
        { activePlanner = new LimitPlanner(CoreSettings()); plannerPolicy = policy; }
        var planner = activePlanner;
        worker = Task.Run(() => RunAsync(settings, service, planner, networkError, token), token);
    }
    private async Task RunAsync(UserSettings settings, IPeerService service, LimitPlanner planner, string? initialNetworkError, CancellationToken token)
    {
        var monitor = new NetworkMonitor();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do
            {
                try
                {
                    var detectedGames = scanGames(settings.GameProcesses);
                    if (speedTestLoadStarted && (detectedGames.Count != 0 ||
                        service.Snapshot().Any(p => p.IsGaming || !string.IsNullOrEmpty(p.Game)))) CancelSpeedTestForGaming();
                    Interlocked.Exchange(ref monitoringPulse, NewPulse()).TrySetResult();
                    var measurement = await monitor.SampleAsync(settings, token);
                    monitoringReady = true;
                    // Resolve focus after awaiting probes so an Alt-Tab during ping does not
                    // publish a stale active game. All detected games still bypass shaping.
                    var focusedGame = ForegroundGame.Find(detectedGames, readForeground());
                    var game = focusedGame?.GameName ?? detectedGames.FirstOrDefault()?.GameName ?? "";
                    var gaming = settings.Mode != GamingMode.Off && focusedGame is not null;
                    var remote = service.Snapshot();
                    var now = DateTimeOffset.UtcNow;
                    var latencyTarget = settings.ExternalTarget + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(measurement.RouteId)))[..16];
                    var decision = planner.Plan(gaming, remote, IsSpeedTestRunning ? null : new ProbeStats(now, measurement.External.Current, measurement.Down, true, latencyTarget), now);
                    if (settings.CapacityMeasuredAtUtc is null)
                        decision = decision with { DownloadLimitMbps = null, SharedDownloadBudgetMbps = null, SharedCapacityMbps = null, SharedBudgetComplete = false };
                    // A manual value is a maximum while gaming, never an always-on limit.
                    if (!decision.GamingActive) decision = decision with { DownloadLimitMbps = null };
                    PlannedDownloadLimitMbps = IsSpeedTestRunning ? null : decision.DownloadLimitMbps;
                    if (settings.DriverEnabled && shapingAllowed && !IsSpeedTestRunning)
                        await shaper.ApplyAsync(PlannedDownloadLimitMbps, token, detectedGames, qosEnabled: decision.GamingActive,
                            protectionStillNeeded: () => service.Snapshot().Any(p => p.IsGaming) ||
                                settings.Mode != GamingMode.Off && ForegroundGame.Find(scanGames(settings.GameProcesses), readForeground()) is not null);
                    var driver = shaper.Status;
                    var sharedShare = decision.SharedDownloadBudgetMbps is { } pool
                        ? Math.Floor(pool / decision.ParticipantCount * 100) / 100 : (double?)null;
                    bool ConfirmedShare(bool active, double? applied) => active && applied is { } rate &&
                        double.IsFinite(rate) && rate > 0 && (sharedShare is null || rate <= sharedShare + .000001);
                    service.Publish(gaming, measurement.Down, measurement.External.Current, driver.Mbps, game[..Math.Min(80, game.Length)], driver.Active,
                        decision.IdleBaselineMs, latencyTarget, decision.IdleBaselineAtUtc, uploadMbps: measurement.Up);
                    var devices = new List<DeviceView> { new(settings.DeviceName + " · dieser PC", measurement.Adapter, gaming, game, driver.Mbps, driver.Active ? "Begrenzt" : gaming ? "Gaming" : "Unbegrenzt", measurement.Down, measurement.Up) };
                    devices.AddRange(remote.Select(p => new DeviceView(p.DeviceName, p.Address ?? "LAN", p.IsGaming, p.Game ?? "", p.ShaperActive ? p.AppliedLimitMbps : null, p.ShaperActive ? "Begrenzt" : p.IsGaming ? "Gaming" : "Unbegrenzt", p.DownloadMbps, p.UploadMbps)));
                    var state = driver.Active ? "Downloads werden begrenzt" : gaming ? "Gaming-Modus aktiv" : decision.GamingActive ? "Gaming im LAN erkannt" : "Bereit für deine nächste Runde";
                    var detail = gaming
                        ? "Große Downloads werden auf diesem und verbundenen DYNEX-PCs begrenzt. Spiele und Sprachchat bleiben frei."
                        : decision.AdaptiveReduced ? "Der Ping ist gestiegen. DYNEX bremst die Downloads stärker."
                        : decision.GamingActive ? "Ein anderer PC spielt. Deine Downloads sollen langsamer weiterlaufen."
                        : shaper.CaptureReleased ? "Kein Spiel im Fokus. Der Netzwerkfilter ist inaktiv."
                        : "Kein Spiel im Fokus. DYNEX gibt den Netzwerkfilter frei: " + driver.Message;
                    if (settings.CapacityMeasuredAtUtc is null)
                        detail = decision.GamingActive
                            ? "Downloads bleiben frei, bis ein Speedtest erfolgreich gespeichert wurde. Spiele und Sprachchat können beim Spielen bereits priorisiert werden."
                            : "Downloads bleiben frei, bis ein Speedtest erfolgreich gespeichert wurde. " + detail;
                    if (IsSpeedTestRunning) { state = "Internetgeschwindigkeit wird gemessen"; detail = SpeedTestMessage; }
                    else if (IsSpeedTestWaiting) detail = SpeedTestMessage;
                    if (decision.DownloadLimitMbps.HasValue && !driver.Active && !IsSpeedTestRunning)
                        detail = "Gaming erkannt. Die Downloadbegrenzung ist noch nicht aktiv: " + driver.Message;
                    var point = new HistoryPoint(now, measurement.Router.Current, measurement.External.Current, measurement.Down, driver.Active || remote.Any(p => p.ShaperActive));
                    lock (history) { history.Add(point); if (history.Count > 300) history.RemoveAt(0); }
                    HistoryPoint[] points;
                    lock (history) points = history.ToArray();
                    var networkState = initialNetworkError ?? service.NetworkStatus;
                    var snapshot = new RuntimeSnapshot(state, detail, gaming, game, driver.Active,
                        driver.Message, driver.Mbps, measurement.Down, measurement.Up, measurement.Router, measurement.External, devices, points,
                        networkState ?? $"{remote.Count} verbundene PCs · {(settings.AutoConnectLan ? "Automatisch im privaten LAN" : "Private Gruppe")}", Fingerprint(), shaper.IsAvailable,
                        decision.SharedDownloadBudgetMbps, decision.ParticipantCount, decision.GamingCount,
                        decision.SharedCapacityMbps, decision.SharedBudgetComplete,
                        (ConfirmedShare(driver.Active, driver.Mbps) ? 1 : 0) +
                        remote.Count(p => ConfirmedShare(p.ShaperActive, p.AppliedLimitMbps)),
                        driver.QosActive, driver.GamePriorityPackets, driver.VoicePriorityPackets, networkState,
                        ProtectionIdle: !decision.GamingActive && shaper.CaptureReleased,
                        NetworkDiagnostics: service.GetNetworkDiagnostics());
                    Snapshot = snapshot;
                    Updated?.Invoke(snapshot);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    monitoringReady = false;
                    PlannedDownloadLimitMbps = null;
                    if (IsSpeedTestRunning) CancelSpeedTestForGaming();
                    // Monitoring failure must not keep an old automatic rate active.
                    await shaper.ApplyAsync(null, token);
                    service.Publish(false);
                    var emptyPing = new PingView(null, null, null, 0, "");
                    var prior = Snapshot ?? new RuntimeSnapshot("", "", false, "", false, "", null, 0, 0, emptyPing, emptyPing, [], [], initialNetworkError ?? service.NetworkStatus ?? "", Fingerprint());
                    Snapshot = prior with { State = "Messung unterbrochen", Detail = ex.Message, IsGaming = false, DriverActive = false, LimitMbps = null, DownloadMbps = null, UploadMbps = null, Devices = [], Router = emptyPing, External = emptyPing, DriverState = shaper.Status.Message,
                        SharedDownloadBudgetMbps = null, ParticipantCount = 1, GamingCount = 0, SharedCapacityMbps = null, SharedBudgetComplete = false, ConfirmedParticipantCount = 0,
                        QosActive = false, GamePriorityPackets = 0, VoicePriorityPackets = 0, ProtectionIdle = false, NetworkDiagnostics = null };
                    Updated?.Invoke(Snapshot);
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
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
            // The once-only startup marker is monotonic, even when a UI action
            // captured older settings before the first measurement began.
            if (prior.AutomaticSpeedTestAttempted)
                settings = settings with { AutomaticSpeedTestAttempted = true };
            // A settings action queued during the test must not restore its old capacity.
            if (settings.CapacityMeasuredAtUtc != prior.CapacityMeasuredAtUtc)
                settings = SettingsStore.Normalize(settings with { CapacityMbps = prior.CapacityMbps, CapacityMeasuredAtUtc = prior.CapacityMeasuredAtUtc });
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
    internal string ExportPairingCode() => "DYNEX1:" + identity.GroupKey;
    public async Task<string> ActivatePairingCodeAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
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
            Version = "1.0.5", CapturedAtUtc = DateTimeOffset.UtcNow, Settings, Snapshot = snapshot,
            NetworkTransport = GetNetworkDiagnostics(), NetworkEnvironment = networkEnvironment,
            WithoutConfirmedLimits = Summarize(false), WithConfirmedLimits = Summarize(true),
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
        cancellation?.Cancel();
        if (worker != null) { try { await worker; } catch (OperationCanceledException) { } }
        if (peers != null) { peers.PeersChanged -= PeerStatusChanged; await peers.DisposeAsync(); peers = null; }
        cancellation?.Dispose(); cancellation = null; worker = null;
        PlannedDownloadLimitMbps = null;
    }
    public async Task StopAsync()
    {
        stopping = true;
        // Cancel before taking the gate: the running test owns it until its streams close.
        lock (speedTestSync) speedTestCancellation?.Cancel();
        await lifecycle.WaitAsync();
        try { started = false; await StopWorkerAsync(); await shaper.StopAsync(); }
        finally { lifecycle.Release(); }
        var pending = speedTestTask;
        if (pending is { IsCompleted: false }) await pending;
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
