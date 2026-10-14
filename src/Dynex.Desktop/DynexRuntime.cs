using System.IO;
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
    private readonly ShaperController shaper = new();
    private readonly List<HistoryPoint> history = [];
    private IPeerService? peers;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private bool started;
    private LimitPlanner? activePlanner;
    private string? plannerPolicy;
    private readonly bool isolated;
    private readonly ISpeedTestService speedTest;
    private readonly bool automaticSpeedTestEnabled;
    private readonly object speedTestSync = new();
    private CancellationTokenSource? speedTestCancellation;
    private volatile Task? speedTestTask;
    private bool automaticSpeedTestAttempted;
    private volatile bool stopping;
    public bool IsSpeedTestRunning { get; private set; }
    public string SpeedTestMessage { get; private set; } = "";
    public UserSettings Settings { get; private set; }
    internal int PeerPort { get; }
    public RuntimeSnapshot? Snapshot { get; private set; }
    public event Action<RuntimeSnapshot>? Updated;
    public DynexRuntime(string? profilePath = null) : this(profilePath, null) { }
    internal DynexRuntime(string? profilePath, ISpeedTestService? speedTest)
    {
        isolated = profilePath != null;
        PeerPort = isolated ? GetIsolatedPeerPort() : 39274;
        this.speedTest = speedTest ?? new SpeedTestService();
        automaticSpeedTestEnabled = !isolated || speedTest is not null;
        store = new(profilePath);
        Settings = store.Load();
        identity = store.LoadIdentity();
    }
    private DynexSettings CoreSettings() => new()
    {
        DeviceId = Settings.AutoConnectLan ? AutoLanIdentity.GetDeviceId(identity.SigningPrivateKeyBase64!) : identity.DeviceId,
        DeviceName = Environment.MachineName, GroupKeyBase64 = identity.GroupKey,
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
            if (automaticSpeedTestEnabled && !automaticSpeedTestAttempted && Settings.CapacityMeasuredAtUtc is null)
            {
                automaticSpeedTestAttempted = true;
                _ = BeginSpeedTest();
            }
            else await StartWorkerAsync();
        }
        finally { lifecycle.Release(); }
    }
    public async Task MeasureCapacityAsync()
    {
        var observed = speedTestTask;
        var running = observed;
        if (running is { IsCompleted: false }) { await running; return; }
        await lifecycle.WaitAsync();
        try
        {
            if (!started || stopping) throw new InvalidOperationException("DYNEX wird gerade beendet.");
            // A concurrent caller may wait behind the entire test. Share the test that
            // started since this call, even if it completed before this gate was acquired.
            running = speedTestTask is { } pending && (!pending.IsCompleted || !ReferenceEquals(pending, observed))
                ? pending : BeginSpeedTest();
        }
        finally { lifecycle.Release(); }
        await running;
    }
    // Called with the lifecycle gate held. The task waits for that gate before doing any work.
    private Task BeginSpeedTest()
    {
        IsSpeedTestRunning = true;
        SpeedTestMessage = "Download-Speedtest läuft · maximal 10 Sekunden / 256 MiB. Die lokale Begrenzung pausiert.";
        lock (speedTestSync) speedTestCancellation = new CancellationTokenSource();
        speedTestTask = MeasureCapacityCoreAsync(speedTestCancellation.Token);
        PublishSpeedTestSnapshot();
        return speedTestTask;
    }
    private async Task MeasureCapacityCoreAsync(CancellationToken token)
    {
        await lifecycle.WaitAsync();
        try
        {
            token.ThrowIfCancellationRequested();
            await StopWorkerAsync();
            // Closing the helper waits for its exit, so a prior rate cannot throttle this test.
            await shaper.StopAsync(requireExit: true);
            token.ThrowIfCancellationRequested();
            var result = await speedTest.MeasureAsync(token);
            token.ThrowIfCancellationRequested();
            var measured = SettingsStore.Normalize(Settings with
            {
                CapacityMbps = Math.Round(result.DownloadMbps, 2),
                CapacityMeasuredAtUtc = DateTimeOffset.UtcNow
            });
            SettingsStore.Validate(measured);
            store.Save(measured);
            Settings = measured;
            SpeedTestMessage = "Automatisch gemessen und gespeichert. Bei einem Anschlusswechsel erneut messen.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { SpeedTestMessage = "Speedtest abgebrochen. Der bisherige Wert bleibt erhalten."; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { SpeedTestMessage = $"Speedtest fehlgeschlagen: {ex.Message} Der bisherige Wert bleibt erhalten. Bitte erneut messen."; }
        finally
        {
            IsSpeedTestRunning = false;
            lock (speedTestSync) { speedTestCancellation?.Dispose(); speedTestCancellation = null; }
            // The artificial load must not become an adaptive latency baseline.
            activePlanner = null; plannerPolicy = null;
            try
            {
                PublishSpeedTestSnapshot();
                if (started && !stopping) await StartWorkerAsync();
            }
            finally { lifecycle.Release(); }
        }
    }
    private void PublishSpeedTestSnapshot()
    {
        var emptyPing = new PingView(null, null, null, 0, "");
        HistoryPoint[] points;
        lock (history) points = history.ToArray();
        Snapshot = new RuntimeSnapshot(IsSpeedTestRunning ? "Internetgeschwindigkeit wird gemessen" : "Speedtest beendet",
            SpeedTestMessage, false, "", false, shaper.Status.Message, null, null, null,
            emptyPing, emptyPing, [], points, "Netzwerküberwachung wird nach dem Speedtest fortgesetzt", Fingerprint(), shaper.IsAvailable);
        Updated?.Invoke(Snapshot);
    }
    private async Task StartWorkerAsync()
    {
        cancellation = new();
        if (Settings.DriverEnabled && !isolated) await shaper.StartAsync();
        peers = Settings.AutoConnectLan
            ? new AutoLanPeerService(CoreSettings(), identity.SigningPrivateKeyBase64!)
            : new PeerService(CoreSettings());
        string? networkError = null;
        try { await peers.StartAsync(cancellation.Token); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException) { networkError = ex.Message; }
        var settings = Settings;
        var service = peers;
        var token = cancellation.Token;
        var policy = JsonSerializer.Serialize(new { settings.CapacityMbps, settings.BackgroundPercent, settings.ReservePerGamerMbps, settings.ManualLimitMbps, settings.Adaptive, settings.RouterTarget, settings.ExternalTarget, settings.AutoConnectLan, Group = settings.AutoConnectLan ? "LAN" : Fingerprint() });
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
                    var measurement = await monitor.SampleAsync(settings, token);
                    var detectedGames = GameDetector.ScanAll(settings.GameProcesses);
                    var game = detectedGames.FirstOrDefault()?.GameName ?? "";
                    var gaming = settings.Mode == GamingMode.On || settings.Mode == GamingMode.Auto && game.Length != 0;
                    var remote = service.Snapshot();
                    var now = DateTimeOffset.UtcNow;
                    var latencyTarget = settings.ExternalTarget + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(measurement.RouteId)))[..16];
                    var decision = planner.Plan(gaming, remote, new ProbeStats(now, measurement.External.Current, measurement.Down, true, latencyTarget), now);
                    if (settings.DriverEnabled && !isolated) await shaper.ApplyAsync(decision.DownloadLimitMbps, token, detectedGames, qosEnabled: true);
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
                        : settings.ManualLimitMbps.HasValue ? "Ein manuelles Downloadlimit ist eingestellt."
                        : "Beim Spielen werden große Downloads automatisch begrenzt – auch auf deinem Gaming-PC.";
                    if (decision.DownloadLimitMbps.HasValue && !driver.Active)
                        detail = "Gaming erkannt. Die Downloadbegrenzung ist noch nicht aktiv: " + driver.Message;
                    var point = new HistoryPoint(now, measurement.Router.Current, measurement.External.Current, measurement.Down, driver.Active || remote.Any(p => p.ShaperActive));
                    lock (history) { history.Add(point); if (history.Count > 300) history.RemoveAt(0); }
                    HistoryPoint[] points;
                    lock (history) points = history.ToArray();
                    var networkState = initialNetworkError ?? service.NetworkStatus;
                    var snapshot = new RuntimeSnapshot(state, detail, gaming, settings.Mode == GamingMode.On ? "Manuell aktiviert" : game, driver.Active,
                        driver.Message, driver.Mbps, measurement.Down, measurement.Up, measurement.Router, measurement.External, devices, points,
                        networkState ?? $"{remote.Count} verbundene PCs · {(settings.AutoConnectLan ? "Automatisch im privaten LAN" : "Private Gruppe")}", Fingerprint(), shaper.IsAvailable,
                        decision.SharedDownloadBudgetMbps, decision.ParticipantCount, decision.GamingCount,
                        decision.SharedCapacityMbps, decision.SharedBudgetComplete,
                        (ConfirmedShare(driver.Active, driver.Mbps) ? 1 : 0) +
                        remote.Count(p => ConfirmedShare(p.ShaperActive, p.AppliedLimitMbps)),
                        driver.QosActive, driver.GamePriorityPackets, driver.VoicePriorityPackets);
                    Snapshot = snapshot;
                    Updated?.Invoke(snapshot);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Monitoring failure must not keep an old automatic rate active.
                    await shaper.ApplyAsync(null, token);
                    service.Publish(false);
                    var emptyPing = new PingView(null, null, null, 0, "");
                    var prior = Snapshot ?? new RuntimeSnapshot("", "", false, "", false, "", null, 0, 0, emptyPing, emptyPing, [], [], initialNetworkError ?? service.NetworkStatus ?? "", Fingerprint());
                    Snapshot = prior with { State = "Messung unterbrochen", Detail = ex.Message, IsGaming = false, DriverActive = false, LimitMbps = null, DownloadMbps = null, UploadMbps = null, Devices = [], Router = emptyPing, External = emptyPing, DriverState = shaper.Status.Message,
                        SharedDownloadBudgetMbps = null, ParticipantCount = 1, GamingCount = 0, SharedCapacityMbps = null, SharedBudgetComplete = false, ConfirmedParticipantCount = 0,
                        QosActive = false, GamePriorityPackets = 0, VoicePriorityPackets = 0 };
                    Updated?.Invoke(Snapshot);
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public async Task SaveSettingsAsync(UserSettings settings)
    {
        settings = SettingsStore.Normalize(settings);
        SettingsStore.Validate(settings);
        await lifecycle.WaitAsync();
        try
        {
            var prior = Settings;
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
    public string ExportPairingCode() => "DYNEX1:" + identity.GroupKey;
    public async Task ImportPairingCodeAsync(string code)
    {
        var key = code.Trim();
        if (key.StartsWith("DYNEX1:", StringComparison.Ordinal)) key = key[7..];
        byte[] bytes;
        try { bytes = Convert.FromBase64String(key); } catch (FormatException) { throw new ArgumentException("Der Gruppencode ist ungültig."); }
        if (bytes.Length != 32) throw new ArgumentException("Der Gruppencode benötigt einen 256-Bit-Schlüssel.");
        CryptographicOperations.ZeroMemory(bytes);
        await lifecycle.WaitAsync();
        try
        {
            var replacement = identity with { GroupKey = key };
            // A failed disk/DPAPI write must leave the currently running group intact.
            var privateSettings = Settings with { AutoConnectLan = false };
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
    private string Fingerprint() => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(identity.GroupKey)))[..16];
    public async Task<string> ExportDiagnosticsAsync()
    {
        var path = Path.Combine(store.DirectoryPath, "Diagnose-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
        var snapshot = Snapshot;
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
            Version = "0.4.1", CapturedAtUtc = DateTimeOffset.UtcNow, Settings, Snapshot = snapshot,
            WithoutConfirmedLimits = Summarize(false), WithConfirmedLimits = Summarize(true),
            Note = "Keine Schlüssel. NIC-Verkehr enthält LAN. ICMP-Ziel ist kein Spielservernachweis. Beobachtungsvergleich, kein kausaler Bufferbloat-Beweis."
        }, new JsonSerializerOptions { WriteIndented = true }));
        return path;
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
        if (peers != null) { await peers.DisposeAsync(); peers = null; }
        cancellation?.Dispose(); cancellation = null; worker = null;
    }
    public async Task StopAsync()
    {
        stopping = true;
        // Cancel before taking the gate: the running test owns it until its streams close.
        lock (speedTestSync) speedTestCancellation?.Cancel();
        await lifecycle.WaitAsync();
        try { started = false; await StopWorkerAsync(); await shaper.StopAsync(); }
        finally { lifecycle.Release(); }
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
