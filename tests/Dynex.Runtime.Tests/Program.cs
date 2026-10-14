using System.Reflection;
using System.Text.Json;
using Dynex.Desktop;
using Dynex.Core;
using Dynex.Desktop.Games;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;

var checks = 0;
var failures = new List<string>();
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    checks++;
}
async Task Scenario(string name, Func<Task> run)
{
    try { await run(); }
    catch (Exception error)
    {
        failures.Add(name + ": " + error);
        Console.Error.WriteLine("FAIL SCENARIO: " + name + ": " + error.Message);
    }
}
var timeout = TimeSpan.FromSeconds(8);

await Scenario("automatic initial measurement and persistence", async () =>
{
    using var profile = new TestProfile();
    var service = new ControlledSpeedTest();
    await using (var runtime = new DynexRuntime(profile.Path, service, _ => []))
    {
        await runtime.StartAsync().WaitAsync(timeout);
        var request = await service.NextAsync().WaitAsync(timeout);
        Check(runtime.IsSpeedTestRunning && !request.Completion.Task.IsCompleted,
            "first start returns while automatic measurement is pending");
        Check(HasWorker(runtime) && HasPeers(runtime), "monitor and peer service remain active to protect gaming during initial measurement");
        var completed = runtime.MeasureCapacityAsync();
        request.Complete(86.54321);
        await completed.WaitAsync(timeout);
        var persisted = new SettingsStore(profile.Path).Load();
        Check(runtime.Settings.CapacityMbps == 86.54 && persisted.CapacityMbps == 86.54,
            "successful rate is rounded and persisted");
        Check(runtime.Settings.CapacityMeasuredAtUtc is not null &&
            persisted.CapacityMeasuredAtUtc == runtime.Settings.CapacityMeasuredAtUtc,
            "successful measurement timestamp is persisted");
        Check(Math.Abs(persisted.ReservePerGamerMbps - 4.327) < 0.000001,
            "gaming reserve is normalized from the new capacity");
        Check(HasWorker(runtime), "monitor worker resumes after successful measurement");
        await runtime.SaveSettingsAsync(runtime.Settings with { Mode = GamingMode.On }).WaitAsync(timeout);
        await runtime.StopAsync().WaitAsync(timeout);
        await runtime.StartAsync().WaitAsync(timeout);
        Check(service.Calls == 1 && !runtime.IsSpeedTestRunning,
            "save and stop/start do not repeat completed automatic measurement");
    }
    var restartedService = new ControlledSpeedTest();
    await using var restarted = new DynexRuntime(profile.Path, restartedService, _ => []);
    await restarted.StartAsync().WaitAsync(timeout);
    Check(restartedService.Calls == 0 && HasWorker(restarted),
        "a new runtime loads the timestamp and skips automatic measurement");
});

await Scenario("legacy settings without measurement timestamp", async () =>
{
    using var profile = new TestProfile();
    File.WriteAllText(System.IO.Path.Combine(profile.Path, "settings.json"),
        "{\"CapacityMbps\":80,\"AutoConnectLan\":false,\"RouterTarget\":\"127.0.0.1\",\"ExternalTarget\":\"127.0.0.1\"}");
    var service = new ControlledSpeedTest();
    await using var runtime = new DynexRuntime(profile.Path, service, _ => []);
    Check(runtime.Settings.CapacityMbps == 80 && runtime.Settings.CapacityMeasuredAtUtc is null,
        "legacy capacity loads without inventing a measurement timestamp");
    Check(!runtime.Settings.AutoUpdate,
        "legacy settings without AutoUpdate default to opt-out");
    Check(!runtime.Settings.AutomaticSpeedTestAttempted,
        "legacy settings without an automatic-test marker load as not attempted");
    Check(runtime.Settings.AutoStart && runtime.Settings.DriverEnabled,
        "legacy settings inherit the enabled startup and protection defaults");
    await runtime.StartAsync().WaitAsync(timeout);
    var request = await service.NextAsync().WaitAsync(timeout);
    var completed = runtime.MeasureCapacityAsync();
    request.Complete(123.45);
    await completed.WaitAsync(timeout);
    Check(service.Calls == 1 && runtime.Settings.CapacityMbps == 123.45,
        "legacy profile receives its initial automatic measurement");
});

await Scenario("auto-update preference persists without restarting networking", async () =>
{
    using var profile = new TestProfile(measured: true);
    await using var runtime = new DynexRuntime(profile.Path, new ControlledSpeedTest(), _ => []);
    await runtime.StartAsync().WaitAsync(timeout);
    var prior = runtime.Settings;
    var worker = Field(runtime, "worker");
    var peers = Field(runtime, "peers");
    var pairing = runtime.ExportPairingCode();

    await runtime.SaveAutoUpdatePreferenceAsync(true).WaitAsync(timeout);
    Check(runtime.Settings == prior with { AutoUpdate = true } &&
        new SettingsStore(profile.Path).Load() == prior with { AutoUpdate = true },
        "enabling updates persists only AutoUpdate and preserves all network settings");
    Check(ReferenceEquals(worker, Field(runtime, "worker")) && ReferenceEquals(peers, Field(runtime, "peers")) &&
        runtime.ExportPairingCode() == pairing,
        "enabling updates keeps the worker, peer service, and pairing identity intact");

    await runtime.SaveAutoUpdatePreferenceAsync(false).WaitAsync(timeout);
    Check(runtime.Settings == prior && new SettingsStore(profile.Path).Load() == prior,
        "disabling updates persists opt-out and restores the original settings");
    Check(ReferenceEquals(worker, Field(runtime, "worker")) && ReferenceEquals(peers, Field(runtime, "peers")) &&
        runtime.ExportPairingCode() == pairing,
        "disabling updates does not restart networking or change pairing identity");
});

await Scenario("duplicate measurement calls", async () =>
{
    using var profile = new TestProfile(measured: true);
    var service = new ControlledSpeedTest();
    await using var runtime = new DynexRuntime(profile.Path, service, _ => []);
    await runtime.StartAsync().WaitAsync(timeout);
    var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var issuedCount = 0;
    var measurements = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
    {
        var measurement = runtime.MeasureCapacityAsync();
        if (Interlocked.Increment(ref issuedCount) == 16) issued.TrySetResult();
        await measurement;
    })).ToArray();
    var request = await service.NextAsync().WaitAsync(timeout);
    await issued.Task.WaitAsync(timeout);
    Check(service.Calls == 1 && service.MaximumConcurrent == 1 && HasWorker(runtime) && HasPeers(runtime),
        "concurrent requests share one test while gaming detection stays active");
    request.Complete(321.09);
    await Task.WhenAll(measurements).WaitAsync(timeout);
    Check(service.Calls == 1 && service.MaximumConcurrent == 1,
        "all duplicate callers finish without a second measurement");
});

await Scenario("failure preserves prior measured settings", async () =>
{
    using var profile = new TestProfile(measured: true);
    var service = new ControlledSpeedTest();
    await using var runtime = new DynexRuntime(profile.Path, service, _ => []);
    await runtime.StartAsync().WaitAsync(timeout);
    var prior = runtime.Settings;
    var measurement = runtime.MeasureCapacityAsync();
    var request = await service.NextAsync().WaitAsync(timeout);
    request.Completion.TrySetException(new InvalidOperationException("simulated offline"));
    await measurement.WaitAsync(timeout);
    var saved = new SettingsStore(profile.Path).Load();
    Check(runtime.Settings == prior && saved == prior,
        "service failure preserves capacity and timestamp in memory and on disk");
    Check(!runtime.IsSpeedTestRunning && runtime.SpeedTestMessage.Contains("simulated offline") && HasWorker(runtime),
        "service failure reports its cause and resumes monitoring");
});

foreach (var rate in new[] { double.NaN, double.PositiveInfinity, -1d, 0d, 0.5d, 10_001d })
{
    await Scenario("invalid rate " + rate, async () =>
    {
        using var profile = new TestProfile(measured: true);
        var service = new ControlledSpeedTest();
        await using var runtime = new DynexRuntime(profile.Path, service, _ => []);
        await runtime.StartAsync().WaitAsync(timeout);
        var prior = runtime.Settings;
        var measurement = runtime.MeasureCapacityAsync();
        var request = await service.NextAsync().WaitAsync(timeout);
        request.Complete(rate);
        await measurement.WaitAsync(timeout);
        Check(runtime.Settings == prior && new SettingsStore(profile.Path).Load() == prior,
            "invalid rate " + rate + " preserves capacity and timestamp");
    });
}

await Scenario("failed automatic measurement is not repeated by settings/restart", async () =>
{
    using var profile = new TestProfile();
    var service = new ControlledSpeedTest();
    await using (var runtime = new DynexRuntime(profile.Path, service, _ => []))
    {
        await runtime.StartAsync().WaitAsync(timeout);
        var request = await service.NextAsync().WaitAsync(timeout);
        var measurement = runtime.MeasureCapacityAsync();
        request.Completion.TrySetException(new InvalidOperationException("simulated offline"));
        await measurement.WaitAsync(timeout);
        Check(runtime.Settings.CapacityMeasuredAtUtc is null && runtime.Settings.AutomaticSpeedTestAttempted &&
            new SettingsStore(profile.Path).Load().AutomaticSpeedTestAttempted,
            "failed startup test keeps its prior rate and persists the one-time attempt");
        await runtime.SaveSettingsAsync(runtime.Settings with { Mode = GamingMode.Off }).WaitAsync(timeout);
        await runtime.SaveSettingsAsync(runtime.Settings with { AutomaticSpeedTestAttempted = false }).WaitAsync(timeout);
        Check(runtime.Settings.AutomaticSpeedTestAttempted && new SettingsStore(profile.Path).Load().AutomaticSpeedTestAttempted,
            "stale settings cannot erase the once-only startup attempt");
        await runtime.StopAsync().WaitAsync(timeout);
        await runtime.StartAsync().WaitAsync(timeout);
        Check(service.Calls == 1 && runtime.Settings.CapacityMeasuredAtUtc is null,
            "same runtime does not retry the failed automatic measurement");
    }

    var restartedService = new ControlledSpeedTest();
    await using var restarted = new DynexRuntime(profile.Path, restartedService, _ => []);
    await restarted.StartAsync().WaitAsync(timeout);
    Check(!restarted.IsSpeedTestRunning && restartedService.Calls == 0,
        "a new runtime does not repeat the failed startup test");
    var manual = restarted.MeasureCapacityAsync();
    var manualRequest = await restartedService.NextAsync().WaitAsync(timeout);
    manualRequest.Complete(91.25);
    await manual.WaitAsync(timeout);
    Check(restartedService.Calls == 1 && restarted.Settings.CapacityMbps == 91.25,
        "manual measurement remains available after the failed automatic attempt");
});

await Scenario("cancelled automatic measurement is not repeated after restart", async () =>
{
    using var profile = new TestProfile();
    var service = new ControlledSpeedTest();
    await using (var runtime = new DynexRuntime(profile.Path, service, _ => []))
    {
        await runtime.StartAsync().WaitAsync(timeout);
        var request = await service.NextAsync().WaitAsync(timeout);
        await runtime.StopAsync().WaitAsync(timeout);
        Check(request.Token.IsCancellationRequested && runtime.Settings.AutomaticSpeedTestAttempted &&
            new SettingsStore(profile.Path).Load().AutomaticSpeedTestAttempted,
            "cancellation retains the persisted startup-attempt marker");
        Check(runtime.SpeedTestMessage.Contains("abgebrochen") && !runtime.IsSpeedTestRunning,
            "cancellation status remains visible and the attempt finishes");
    }
    var restartedService = new ControlledSpeedTest();
    await using var restarted = new DynexRuntime(profile.Path, restartedService, _ => []);
    await restarted.StartAsync().WaitAsync(timeout);
    Check(!restarted.IsSpeedTestRunning && restartedService.Calls == 0,
        "a new runtime does not repeat the cancelled startup test");
    var manual = restarted.MeasureCapacityAsync();
    var manualRequest = await restartedService.NextAsync().WaitAsync(timeout);
    manualRequest.Complete(88.75);
    await manual.WaitAsync(timeout);
    Check(restartedService.Calls == 1 && restarted.Settings.CapacityMbps == 88.75,
        "manual measurement remains available after the cancelled automatic attempt");
});

await Scenario("stale settings queued during measurement", async () =>
{
    using var profile = new TestProfile(measured: true);
    var service = new ControlledSpeedTest();
    await using var runtime = new DynexRuntime(profile.Path, service, _ => []);
    await runtime.StartAsync().WaitAsync(timeout);
    var stale = runtime.Settings with { ManualLimitMbps = 30 };
    var measurement = runtime.MeasureCapacityAsync();
    var request = await service.NextAsync().WaitAsync(timeout);
    var save = runtime.SaveSettingsAsync(stale);
    Check(!save.IsCompleted, "settings save waits for in-progress measurement");
    request.Complete(75.25);
    await Task.WhenAll(measurement, save).WaitAsync(timeout);
    var saved = new SettingsStore(profile.Path).Load();
    Check(runtime.Settings.CapacityMbps == 75.25 && saved.CapacityMbps == 75.25 &&
        runtime.Settings.CapacityMeasuredAtUtc != stale.CapacityMeasuredAtUtc,
        "queued stale save preserves newly measured capacity and timestamp");
    Check(saved.ManualLimitMbps == 30 && Math.Abs(saved.ReservePerGamerMbps - 3.7625) < 0.000001,
        "queued stale save applies manual limit and recalculates reserve from current capacity");
});

await Scenario("stop cancels measurement and prevents worker restart", () => OnSerialContext(async () =>
{
    using var profile = new TestProfile();
    var service = new ControlledSpeedTest();
    await using var runtime = new DynexRuntime(profile.Path, service, _ => []);
    await runtime.StartAsync().WaitAsync(timeout);
    var request = await service.NextAsync().WaitAsync(timeout);
    var measurement = runtime.MeasureCapacityAsync();
    var save = runtime.SaveSettingsAsync(runtime.Settings with { BackgroundPercent = 70 });
    // This continuation runs inline when Save completes. Stop's semaphore continuation is
    // posted to the serial context and cannot race ahead of this observation.
    var workerBeforeStop = Field(runtime, "worker");
    var workerRestarted = false;
    var observeSave = save.ContinueWith(_ => workerRestarted = !ReferenceEquals(workerBeforeStop, Field(runtime, "worker")) && HasWorker(runtime),
        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    var stop = runtime.StopAsync();
    await Task.WhenAll(measurement, save, observeSave, stop).WaitAsync(timeout);
    Check(!workerRestarted, "settings queued before stop never restart worker or peer service during shutdown");
    Check(request.Token.IsCancellationRequested && service.Active == 0 && !runtime.IsSpeedTestRunning,
        "stop cancels the active test and finishes without deadlock");
    Check(!HasWorker(runtime) && !HasPeers(runtime) && runtime.Settings.CapacityMeasuredAtUtc is null,
        "stop leaves no worker/peer service and preserves unmeasured settings");
    Check(runtime.SpeedTestMessage.Contains("abgebrochen"), "cancellation is visible in measurement status");
    try
    {
        await runtime.MeasureCapacityAsync().WaitAsync(timeout);
        throw new InvalidOperationException("a stopped runtime accepted a measurement");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("beendet"))
    { Check(service.Calls == 1, "stopped runtime rejects a new measurement"); }
}));


await Scenario("local game postpones automatic and rejects manual measurement even with protection off", async () =>
{
    using var profile = new TestProfile();
    var service = new ControlledSpeedTest();
    var playing = true;
    await using var runtime = new DynexRuntime(profile.Path, service,
        _ => Volatile.Read(ref playing) ? [new("Test game", "testgame", 1, null, true, "test")] : []);
    await runtime.SaveSettingsAsync(runtime.Settings with { Mode = GamingMode.Off, ManualLimitMbps = 15 });
    await runtime.StartAsync().WaitAsync(timeout);
    await Until(() => runtime.Snapshot?.Devices.Count == 1);
    Check(runtime.IsSpeedTestWaiting && !runtime.IsSpeedTestRunning && service.Calls == 0,
        "local open game postpones automatic load even when gaming mode is off");
    await runtime.MeasureCapacityAsync().WaitAsync(timeout);
    Check(service.Calls == 0 && !runtime.Settings.AutomaticSpeedTestAttempted &&
        !new SettingsStore(profile.Path).Load().AutomaticSpeedTestAttempted,
        "blocked automatic and manual requests do not consume the automatic attempt");
    Check(runtime.Snapshot is { SharedCapacityMbps: null, SharedDownloadBudgetMbps: null, SharedBudgetComplete: false, LimitMbps: null } &&
        !CoreConfiguration(runtime).CapacityMeasured,
        "unmeasured profile with a manual cap exposes neither a limit nor a shared capacity");
    Volatile.Write(ref playing, false);
    var request = await service.NextAsync().WaitAsync(timeout);
    Check(runtime.Settings.AutomaticSpeedTestAttempted && service.Calls == 1,
        "automatic test starts once after the local game closes");
    var completion = runtime.MeasureCapacityAsync();
    request.Complete(150);
    await completion.WaitAsync(timeout);
    await Until(() => runtime.Snapshot?.SharedCapacityMbps == 150);
    Check(CoreConfiguration(runtime).CapacityMeasured && runtime.Settings.CapacityMeasuredAtUtc is not null,
        "persisted successful measurement unlocks local capacity and shared-budget publication");
});

await Scenario("stop while automatic test waits leaves marker untouched", async () =>
{
    using var profile = new TestProfile();
    var service = new ControlledSpeedTest();
    await using var runtime = new DynexRuntime(profile.Path, service, _ => []);
    await runtime.SaveSettingsAsync(runtime.Settings with { Mode = GamingMode.On });
    await runtime.StartAsync().WaitAsync(timeout);
    await runtime.StopAsync().WaitAsync(timeout);
    Check(service.Calls == 0 && !runtime.IsSpeedTestWaiting && !HasWorker(runtime) && !HasPeers(runtime) &&
        !new SettingsStore(profile.Path).Load().AutomaticSpeedTestAttempted,
        "stop while waiting cleans up peers and worker without marking an unused attempt");
    await runtime.SaveSettingsAsync(runtime.Settings with { Mode = GamingMode.Off });
    await runtime.StartAsync().WaitAsync(timeout);
    var request = await service.NextAsync().WaitAsync(timeout);
    Check(service.Calls == 1 && runtime.Settings.AutomaticSpeedTestAttempted,
        "waiting automatic test remains available after stop and settings change");
    await runtime.StopAsync().WaitAsync(timeout);
    Check(request.Token.IsCancellationRequested && !CoreConfiguration(runtime).CapacityMeasured,
        "cancelled first measurement never unlocks download limits");
});

await Scenario("new local game cancels active measurement", async () =>
{
    using var profile = new TestProfile();
    var service = new ControlledSpeedTest();
    var playing = false;
    await using var runtime = new DynexRuntime(profile.Path, service,
        _ => Volatile.Read(ref playing) ? [new("Test game", "testgame", 1, null, true, "test")] : []);
    await runtime.StartAsync().WaitAsync(timeout);
    var request = await service.NextAsync().WaitAsync(timeout);
    Volatile.Write(ref playing, true);
    await Until(() => request.Token.IsCancellationRequested && !runtime.IsSpeedTestRunning);
    Check(service.Active == 0 && runtime.Settings.CapacityMeasuredAtUtc is null && HasWorker(runtime),
        "local game detection aborts active test while monitoring remains available");
    Check(runtime.Settings.AutomaticSpeedTestAttempted && !CoreConfiguration(runtime).CapacityMeasured,
        "gaming abort consumes only the begun attempt and leaves limits locked");
    Volatile.Write(ref playing, false);
    await runtime.SaveSettingsAsync(runtime.Settings with { BackgroundPercent = 70 });
    await Until(() => runtime.Snapshot?.Devices.Count == 1);
    Check(service.Calls == 1 && !runtime.IsSpeedTestWaiting,
        "closing the game and saving settings do not retry an aborted automatic test");
});

await Scenario("gaming mode settings cancel active measurement immediately", async () =>
{
    using var profile = new TestProfile(measured: true);
    var service = new ControlledSpeedTest();
    await using var runtime = new DynexRuntime(profile.Path, service, _ => []);
    await runtime.StartAsync().WaitAsync(timeout);
    var measurement = runtime.MeasureCapacityAsync();
    var request = await service.NextAsync().WaitAsync(timeout);
    var prior = runtime.Settings;
    await runtime.SaveSettingsAsync(prior with { Mode = GamingMode.On }).WaitAsync(timeout);
    await measurement.WaitAsync(timeout);
    Check(request.Token.IsCancellationRequested && runtime.Settings.Mode == GamingMode.On &&
        runtime.Settings.CapacityMeasuredAtUtc == prior.CapacityMeasuredAtUtc && runtime.Settings.CapacityMbps == prior.CapacityMbps,
        "activating gaming while measuring cancels load and preserves the last successful capacity");
});

await Scenario("peer game postpones and cancels speedtest without losing LAN discovery", async () =>
{
    using var profile = new TestProfile();
    var service = new ControlledSpeedTest();
    await using var runtime = new DynexRuntime(profile.Path, service, _ => []);
    var remoteSettings = CoreConfiguration(runtime) with
    {
        DeviceId = Guid.NewGuid(), DeviceName = "Remote test PC", Port = FreePort(),
        HeartbeatInterval = TimeSpan.FromMilliseconds(100),
        DirectPeers = [new(IPAddress.Loopback, runtime.PeerPort)], CapacityMeasured = false
    };
    await using var remote = new PeerService(remoteSettings);
    remote.Publish(true, game: "Remote game");
    await remote.StartAsync();
    await runtime.StartAsync().WaitAsync(timeout);
    await Until(() => LocalPeers(runtime).Snapshot().Any(p => p.IsGaming));
    // Observe a full discovery heartbeat while the peer is gaming.
    await Until(() => runtime.SpeedTestMessage.Contains("kein Spiel"));
    Check(service.Calls == 0 && runtime.IsSpeedTestWaiting && !runtime.Settings.AutomaticSpeedTestAttempted,
        "connected gaming peer postpones automatic measurement without consuming its marker");
    Check(LocalPeers(runtime).Snapshot().Single().BudgetProposal is null,
        "unmeasured PSK peer never advertises its numerical default capacity");
    await runtime.MeasureCapacityAsync().WaitAsync(timeout);
    Check(service.Calls == 0 && runtime.SpeedTestMessage.Contains("nicht gestartet"),
        "manual speedtest is rejected while a connected peer is gaming");
    remote.Publish(false);
    var request = await service.NextAsync().WaitAsync(timeout);
    remote.Publish(true, game: "Remote game");
    await Until(() => request.Token.IsCancellationRequested && !runtime.IsSpeedTestRunning);
    Check(service.Calls == 1 && service.Active == 0 && runtime.Settings.CapacityMeasuredAtUtc is null && HasPeers(runtime),
        "new peer gaming status cancels measurement without dropping LAN monitoring");
    Check(!CoreConfiguration(runtime).CapacityMeasured && runtime.Settings.AutomaticSpeedTestAttempted,
        "peer gaming abort leaves local limits locked and the begun attempt recorded");
});

await Scenario("failed first measurement keeps manual limits locked", async () =>
{
    using var profile = new TestProfile();
    var service = new ControlledSpeedTest();
    await using var runtime = new DynexRuntime(profile.Path, service, _ => []);
    await runtime.SaveSettingsAsync(runtime.Settings with { ManualLimitMbps = 25 });
    await runtime.StartAsync().WaitAsync(timeout);
    var request = await service.NextAsync().WaitAsync(timeout);
    var completion = runtime.MeasureCapacityAsync();
    request.Completion.TrySetException(new IOException("offline"));
    await completion.WaitAsync(timeout);
    await Until(() => runtime.Snapshot?.Devices.Count == 1);
    Check(!CoreConfiguration(runtime).CapacityMeasured && runtime.Snapshot is
        { LimitMbps: null, SharedDownloadBudgetMbps: null, SharedCapacityMbps: null, SharedBudgetComplete: false },
        "failed first measurement cannot enable manual or automatic download limits");
    await runtime.SaveSettingsAsync(runtime.Settings with { CapacityMeasuredAtUtc = DateTimeOffset.UtcNow, CapacityMbps = 900 });
    Check(runtime.Settings.CapacityMeasuredAtUtc is null && runtime.Settings.CapacityMbps == 80,
        "settings save cannot forge the successful-measurement timestamp");
});


await Scenario("game begins during pre-test helper release", async () =>
{
    using var profile = new TestProfile();
    var service = new ControlledSpeedTest();
    var playing = false;
    var intercepted = false;
    await using var runtime = new DynexRuntime(profile.Path, service,
        _ => Volatile.Read(ref playing) ? [new("Pre-test game", "testgame", 1, null, true, "test")] : []);
    runtime.Updated += _ =>
    {
        if (runtime.IsSpeedTestRunning && !intercepted)
        {
            intercepted = true;
            Volatile.Write(ref playing, true);
        }
    };
    await runtime.StartAsync().WaitAsync(timeout);
    await Until(() => intercepted && runtime.IsSpeedTestWaiting && !runtime.IsSpeedTestRunning);
    Check(service.Calls == 0 && !runtime.Settings.AutomaticSpeedTestAttempted && HasWorker(runtime) && HasPeers(runtime),
        "game appearing before actual load restores monitoring and retains an unused automatic attempt");
    Volatile.Write(ref playing, false);
    var request = await service.NextAsync().WaitAsync(timeout);
    Check(service.Calls == 1 && runtime.Settings.AutomaticSpeedTestAttempted,
        "pre-test gaming race resumes the same automatic request when the game closes");
    await runtime.StopAsync().WaitAsync(timeout);
    Check(request.Token.IsCancellationRequested, "resumed automatic request is cancellable during shutdown");
});

await Scenario("peer start failure leaves speedtests waiting", async () =>
{
    using var profile = new TestProfile();
    var service = new ControlledSpeedTest();
    await using var runtime = new DynexRuntime(profile.Path, service, _ => []);
    using var blockedPort = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    blockedPort.ExclusiveAddressUse = true;
    blockedPort.Bind(new IPEndPoint(IPAddress.Loopback, runtime.PeerPort));
    await runtime.StartAsync().WaitAsync(timeout);
    await Until(() => runtime.Snapshot?.Devices.Count == 1 && runtime.SpeedTestMessage.Contains("zuverlässig"));
    var manual = runtime.MeasureCapacityAsync();
    Check(service.Calls == 0 && runtime.IsSpeedTestWaiting && !runtime.Settings.AutomaticSpeedTestAttempted && !manual.IsCompleted,
        "failed LAN start never treats an unknown peer state as a safe idle network");
    await runtime.StopAsync().WaitAsync(timeout);
    await manual.WaitAsync(timeout);
    Check(service.Calls == 0 && !runtime.IsSpeedTestWaiting,
        "stop cancels manual and automatic requests waiting on unavailable discovery");
});

await Scenario("measurement persistence failure keeps capacity locked", async () =>
{
    using var profile = new TestProfile();
    var service = new ControlledSpeedTest();
    await using var runtime = new DynexRuntime(profile.Path, service, _ => []);
    await runtime.StartAsync().WaitAsync(timeout);
    var request = await service.NextAsync().WaitAsync(timeout);
    var measurement = runtime.MeasureCapacityAsync();
    using (var lockedSettings = new FileStream(System.IO.Path.Combine(profile.Path, "settings.json"), FileMode.Open,
        FileAccess.Read, FileShare.Read))
    {
        request.Complete(300);
        await measurement.WaitAsync(timeout);
    }
    Check(runtime.Settings.CapacityMeasuredAtUtc is null && runtime.Settings.CapacityMbps == 80 &&
        new SettingsStore(profile.Path).Load().CapacityMeasuredAtUtc is null && !CoreConfiguration(runtime).CapacityMeasured,
        "successful download with failed settings write does not unlock capacity or limits");
    Check(runtime.SpeedTestMessage.Contains("fehlgeschlagen") && HasWorker(runtime) && HasPeers(runtime),
        "capacity persistence failure visibly restores monitoring and peers");
});

await Scenario("attempt-marker persistence failure restores protection", async () =>
{
    using var profile = new TestProfile();
    var service = new ControlledSpeedTest();
    await using var runtime = new DynexRuntime(profile.Path, service, _ => []);
    using (var lockedSettings = new FileStream(System.IO.Path.Combine(profile.Path, "settings.json"), FileMode.Open,
        FileAccess.Read, FileShare.Read))
    {
        await runtime.StartAsync().WaitAsync(timeout);
        var originalWorker = Field(runtime, "worker");
        await Until(() => runtime.SpeedTestMessage.Contains("fehlgeschlagen"));
        Check(service.Calls == 0 && !runtime.Settings.AutomaticSpeedTestAttempted &&
            HasWorker(runtime) && !ReferenceEquals(originalWorker, Field(runtime, "worker")) && HasPeers(runtime),
            "failed attempt-marker save starts no download and restores the paused protection lifecycle");
    }
});

Console.WriteLine($"Runtime checks: {checks} passed, {failures.Count} scenarios failed.");
if (failures.Count != 0)
{
    foreach (var failure in failures) Console.Error.WriteLine(failure);
    Environment.ExitCode = 1;
}

static async Task Until(Func<bool> condition)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    while (!condition()) await Task.Delay(20, deadline.Token);
}
static int FreePort()
{
    using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
}
static DynexSettings CoreConfiguration(DynexRuntime runtime) => (DynexSettings)typeof(DynexRuntime)
    .GetMethod("CoreSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime, null)!;
static IPeerService LocalPeers(DynexRuntime runtime) => (IPeerService)Field(runtime, "peers")!;

static bool HasWorker(DynexRuntime runtime) => Field(runtime, "worker") is Task;
static bool HasPeers(DynexRuntime runtime) => Field(runtime, "peers") is not null;
static object? Field(DynexRuntime runtime, string name) => typeof(DynexRuntime)
    .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime);

static Task OnSerialContext(Func<Task> action)
{
    var prior = SynchronizationContext.Current;
    using var context = new SerialContext();
    SynchronizationContext.SetSynchronizationContext(context);
    try
    {
        var task = action();
        while (!task.IsCompleted) context.Pump();
        task.GetAwaiter().GetResult();
        return Task.CompletedTask;
    }
    finally { SynchronizationContext.SetSynchronizationContext(prior); }
}

sealed class SerialContext : SynchronizationContext, IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<Action> callbacks = new();
    public override void Post(SendOrPostCallback callback, object? state) => callbacks.Add(() => callback(state));
    public void Pump()
    {
        if (!callbacks.TryTake(out var callback, TimeSpan.FromSeconds(10)))
            throw new TimeoutException("Lifecycle test made no progress on its serial synchronization context.");
        callback();
    }
    public void Dispose() => callbacks.Dispose();
}

sealed class TestProfile : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Dynex.Runtime.Tests", Guid.NewGuid().ToString("N"));
    public TestProfile(bool measured = false)
    {
        Directory.CreateDirectory(Path);
        new SettingsStore(Path).Save(new UserSettings
        {
            CapacityMbps = 80,
            CapacityMeasuredAtUtc = measured ? new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero) : null,
            AutomaticSpeedTestAttempted = measured,
            AutoStart = false,
            DriverEnabled = false,
            AutoConnectLan = false,
            RouterTarget = "127.0.0.1",
            ExternalTarget = "127.0.0.1"
        });
    }
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

sealed class ControlledSpeedTest : ISpeedTestService
{
    private readonly System.Threading.Channels.Channel<MeasurementRequest> requests =
        System.Threading.Channels.Channel.CreateUnbounded<MeasurementRequest>();
    private int calls, active, maximumConcurrent;
    public int Calls => Volatile.Read(ref calls);
    public int Active => Volatile.Read(ref active);
    public int MaximumConcurrent => Volatile.Read(ref maximumConcurrent);
    public Task<MeasurementRequest> NextAsync() => requests.Reader.ReadAsync().AsTask();
    public async Task<SpeedTestResult> MeasureAsync(CancellationToken token)
    {
        Interlocked.Increment(ref calls);
        var running = Interlocked.Increment(ref active);
        var priorMaximum = MaximumConcurrent;
        while (running > priorMaximum)
        {
            var observed = Interlocked.CompareExchange(ref maximumConcurrent, running, priorMaximum);
            if (observed == priorMaximum) break;
            priorMaximum = observed;
        }
        var request = new MeasurementRequest(token);
        requests.Writer.TryWrite(request);
        try { return await request.Completion.Task.WaitAsync(token); }
        finally { Interlocked.Decrement(ref active); }
    }
}

sealed class MeasurementRequest(CancellationToken token)
{
    public CancellationToken Token { get; } = token;
    public TaskCompletionSource<SpeedTestResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Complete(double rate) => Completion.TrySetResult(new SpeedTestResult(rate, 12_000_000, TimeSpan.FromSeconds(2)));
}
