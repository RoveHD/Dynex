using System.Reflection;
using System.Text.Json;
using Dynex.Desktop;
using Dynex.Core;
using Dynex.Desktop.Games;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Dynex.Shaper;

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

if (args.Length == 0 || args.Contains("--speedtest-peers-only"))
    await Scenario("speedtest rejects competing peer transfers", () => SpeedTestPeerChecks.RunAsync(Check));
if (args.Contains("--speedtest-peers-only"))
{
    Console.WriteLine($"Speedtest peer checks: {checks} passed, {failures.Count} failed; simulated speedtest and loopback peers only.");
    foreach (var failure in failures) Console.Error.WriteLine(failure);
    Environment.ExitCode = failures.Count == 0 ? 0 : 1;
    return;
}

if (args.Contains("--download-activity-only"))
{
    await Scenario("classified download demand and active shared allocations", () => DownloadActivityChecks.RunAsync(Check));
    Console.WriteLine($"Download activity checks: {checks} passed, {failures.Count} failed; loopback peers and simulated capture status only.");
    foreach (var failure in failures) Console.Error.WriteLine(failure);
    Environment.ExitCode = failures.Count == 0 ? 0 : 1;
    return;
}

await Scenario("authenticated PC capacity consumer and source isolation", () => CapacitySharingChecks.RunAsync(Check));
if (args.Contains("--capacity-only"))
{
    Console.WriteLine($"Capacity sharing checks: {checks} passed, {failures.Count} failed.");
    foreach (var failure in failures) Console.Error.WriteLine(failure);
    Environment.ExitCode = failures.Count == 0 ? 0 : 1;
    return;
}

await Scenario("network-specific profiles and measurement isolation", () => NetworkProfileChecks.RunAsync(Check));
if (args.Contains("--profiles-only"))
{
    Console.WriteLine($"Network profile checks: {checks} passed, {failures.Count} failed.");
    foreach (var failure in failures) Console.Error.WriteLine(failure);
    Environment.ExitCode = failures.Count == 0 ? 0 : 1;
    return;
}

await Scenario("immediate network settings and direct peer probes", () => NetworkSettingsChecks.RunAsync(Check));
await Scenario("startup file access diagnostics", () => { StartupStorageChecks.Run(Check); return Task.CompletedTask; });
if (args.Contains("--network-only"))
{
    Console.WriteLine($"Network/startup checks: {checks} passed, {failures.Count} scenarios failed.");
    foreach (var failure in failures) Console.Error.WriteLine(failure);
    Environment.ExitCode = failures.Count == 0 ? 0 : 1;
    return;
}

await Scenario("explicit refresh preserves settings, discovery and monitor history", async () =>
{
    using var profile = new TestProfile(measured: true);
    await using var runtime = new DynexRuntime(profile.Path, new ControlledSpeedTest(), _ => []);
    await runtime.StartAsync().WaitAsync(timeout);
    await Until(() => runtime.Snapshot?.External.Count > 0);
    var prior = runtime.Snapshot!;
    var settings = runtime.Settings;
    var peer = LocalPeers(runtime);
    var worker = Field(runtime, "worker");
    var path = Path.Combine(profile.Path, "settings.json");
    var saved = File.ReadAllText(path);
    // Any accidental settings write fails rather than silently passing this regression.
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var refreshed = await runtime.RefreshStatusAsync().WaitAsync(timeout);
        Check(refreshed.History.Count > prior.History.Count && refreshed.External.Count > prior.External.Count &&
            refreshed.History.Take(prior.History.Count).SequenceEqual(prior.History),
            "explicit refresh performs a new probe and retains the existing monitor and history");
        Check(ReferenceEquals(peer, LocalPeers(runtime)) && ReferenceEquals(worker, Field(runtime, "worker")) &&
            ReferenceEquals(settings, runtime.Settings), "refresh retains the worker, peer session and settings instance");
    }
    Check(File.ReadAllText(path) == saved && new SettingsStore(profile.Path).Load() == settings,
        "refresh does not save or alter persisted settings");
    var check = await runtime.CheckNetworkAsync();
    Check(check.FirewallConfigured is null && ReferenceEquals(check, Field(runtime, "networkConnection")),
        "connection checks publish one complete immutable read-only result");
});

await Scenario("refresh queued during a probe waits for a new sequential probe", async () =>
{
    using var profile = new TestProfile(measured: true);
    using var firstRelease = new ManualResetEventSlim();
    using var secondRelease = new ManualResetEventSlim();
    var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var calls = 0;
    await using var runtime = new DynexRuntime(profile.Path, new ControlledSpeedTest(), _ =>
    {
        var call = Interlocked.Increment(ref calls);
        if (call == 1) { firstEntered.TrySetResult(); firstRelease.Wait(timeout); }
        if (call == 2) { secondEntered.TrySetResult(); secondRelease.Wait(timeout); }
        return [];
    });
    try
    {
        await runtime.StartAsync().WaitAsync(timeout);
        await firstEntered.Task.WaitAsync(timeout);
        var refreshed = runtime.RefreshStatusAsync();
        firstRelease.Set();
        await secondEntered.Task.WaitAsync(timeout);
        Check(!refreshed.IsCompleted && runtime.Snapshot?.History.Count == 1,
            "an in-flight sample cannot satisfy a newer refresh request or run probes concurrently");
        secondRelease.Set();
        Check((await refreshed.WaitAsync(timeout)).History.Count >= 2,
            "the queued refresh completes with the following published full sample");
    }
    finally { firstRelease.Set(); secondRelease.Set(); }
});

await Scenario("stop and settings replacement settle pending refreshes", async () =>
{
    foreach (var save in new[] { false, true })
    {
        using var profile = new TestProfile(measured: true);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var runtime = new DynexRuntime(profile.Path, new ControlledSpeedTest(), _ =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); release.Wait(timeout); }
            return [];
        });
        try
        {
            await runtime.StartAsync().WaitAsync(timeout);
            await entered.Task.WaitAsync(timeout);
            var refreshed = runtime.RefreshStatusAsync();
            var transition = save ? runtime.SaveSettingsAsync(runtime.Settings with { DeviceName = "New settings" }) : runtime.StopAsync();
            await Until(() => Field(runtime, "refreshRequests") is null);
            release.Set();
            await transition.WaitAsync(timeout);
            var canceled = false;
            try { await refreshed.WaitAsync(timeout); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled, save ? "settings replacement cancels refreshes owned by the old worker" : "stop cancels queued refreshes without an endless wait");
            if (!save)
            {
                var rejected = false;
                try { await runtime.RefreshStatusAsync(); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "refresh on a stopped runtime fails explicitly");
                await runtime.StartAsync().WaitAsync(timeout);
            }
            Check((await runtime.RefreshStatusAsync().WaitAsync(timeout)).Devices.Count > 0,
                "a replacement worker accepts fresh requests after its predecessor is gone");
        }
        finally { release.Set(); }
    }
});

await Scenario("refresh remains available during a running speed test", async () =>
{
    using var profile = new TestProfile(measured: true);
    var service = new ControlledSpeedTest();
    await using var runtime = new DynexRuntime(profile.Path, service, _ => []);
    await runtime.StartAsync().WaitAsync(timeout);
    var measurement = runtime.MeasureCapacityAsync();
    var request = await service.NextAsync().WaitAsync(timeout);
    var peer = LocalPeers(runtime);
    var refreshed = await runtime.RefreshStatusAsync().WaitAsync(timeout);
    Check(runtime.IsSpeedTestRunning && !request.Completion.Task.IsCompleted && ReferenceEquals(peer, LocalPeers(runtime)) &&
        refreshed.State == "Internetgeschwindigkeit wird gemessen" && refreshed.History.Count > 0,
        "refresh gets a full live snapshot without waiting for the speed-test lifecycle gate");
    await runtime.StopAsync().WaitAsync(timeout);
    await measurement.WaitAsync(timeout);
});

await Scenario("refresh failures and caller cancellation settle explicitly", async () =>
{
    using var profile = new TestProfile(measured: true);
    var fail = false;
    await using var runtime = new DynexRuntime(profile.Path, new ControlledSpeedTest(), _ =>
        Volatile.Read(ref fail) ? throw new InvalidOperationException("Controlled probe failure") : []);
    await runtime.StartAsync().WaitAsync(timeout);
    await Until(() => runtime.Snapshot is not null);
    var staleRouter = new RouterTrafficSample(10, 2, "Previous router");
    typeof(DynexRuntime).GetField("lastRouterSample", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, staleRouter);
    typeof(DynexRuntime).GetProperty(nameof(DynexRuntime.Snapshot))!.SetValue(runtime, runtime.Snapshot! with { HomeTraffic = staleRouter });
    Volatile.Write(ref fail, true);
    var failed = false;
    try { await runtime.RefreshStatusAsync().WaitAsync(timeout); }
    catch (InvalidOperationException error) when (error.Message == "Controlled probe failure") { failed = true; }
    Check(failed && runtime.Snapshot?.State == "Messung unterbrochen", "failed probes publish the failure and reject their refresh request");
    Check(runtime.Snapshot?.HomeTraffic is null && Field(runtime, "lastRouterSample") is null,
        "measurement failures clear stale router throughput and guide identity");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var canceled = false;
    try { await runtime.RefreshStatusAsync(cancellation.Token); }
    catch (OperationCanceledException) { canceled = true; }
    Check(canceled, "caller cancellation does not wait for or restart the worker");
    Volatile.Write(ref fail, false);
    Check((await runtime.RefreshStatusAsync().WaitAsync(timeout)).State != "Messung unterbrochen", "monitoring and explicit refresh recover after a failed probe");
});

await Scenario("network diagnostic subprocesses are asynchronous, bounded and cancelable", async () =>
{
    ProcessStartInfo Command(string text) => new("powershell.exe")
    {
        ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", text },
        RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
    };
    var output = await NetworkConnectionCheck.ReadProcessOutputAsync(Command("[Console]::Write('diagnostic-output')"), timeout, default).WaitAsync(timeout);
    Check(output == "diagnostic-output", "diagnostic process output is drained without synchronous ReadToEnd");
    var timer = Stopwatch.StartNew();
    output = await NetworkConnectionCheck.ReadProcessOutputAsync(Command("Start-Sleep -Seconds 30"), TimeSpan.FromMilliseconds(200), default).WaitAsync(timeout);
    Check(output is null && timer.Elapsed < timeout, "a child that never closes stdout is killed after its deadline");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
    var canceled = false;
    try { await NetworkConnectionCheck.ReadProcessOutputAsync(Command("Start-Sleep -Seconds 30"), timeout, cancellation.Token).WaitAsync(timeout); }
    catch (OperationCanceledException) { canceled = true; }
    Check(canceled, "caller cancellation interrupts and kills a diagnostic process");
});

await Scenario("export and import activate the same private-group transport", async () =>
{
    using var sourceProfile = new TestProfile(measured: true);
    using var targetProfile = new TestProfile(measured: true);
    await using var source = new DynexRuntime(sourceProfile.Path, new ControlledSpeedTest(), _ => []);
    await using var target = new DynexRuntime(targetProfile.Path, new ControlledSpeedTest(), _ => []);
    await source.SaveSettingsAsync(source.Settings with { AutoConnectLan = true });
    await target.SaveSettingsAsync(target.Settings with { AutoConnectLan = true });
    await source.StartAsync().WaitAsync(timeout);
    await target.StartAsync().WaitAsync(timeout);
    var originalKey = source.ExportPairingCode();
    var sourceIdentity = new SettingsStore(sourceProfile.Path).LoadIdentity();
    var code = await source.ActivatePairingCodeAsync().WaitAsync(timeout);
    Check(code.StartsWith("DYNEX2:", StringComparison.Ordinal) && source.ExportPairingCode() == originalKey &&
        new SettingsStore(sourceProfile.Path).LoadIdentity() == sourceIdentity,
        "new invitation adds direct addresses while preserving the source device and group identity");
    Check(!source.Settings.AutoConnectLan && !new SettingsStore(sourceProfile.Path).Load().AutoConnectLan && LocalPeers(source) is PeerService,
        "copying or revealing a code activates and persists private-group transport on the source PC");
    var sourcePeers = LocalPeers(source);
    await source.ActivatePairingCodeAsync().WaitAsync(timeout);
    Check(ReferenceEquals(sourcePeers, LocalPeers(source)), "copying an active group code again keeps discovery running");
    await target.ImportPairingCodeAsync(code).WaitAsync(timeout);
    Check(!target.Settings.AutoConnectLan && !new SettingsStore(targetProfile.Path).Load().AutoConnectLan &&
        LocalPeers(target) is PeerService && target.ExportPairingCode() == originalKey,
        "importing a copied code puts the receiver in the same persisted group and protocol");
    await using var probe = new PeerService(CoreConfiguration(source) with
    {
        DeviceId = Guid.NewGuid(), DeviceName = "Pairing transport test", Port = FreePort(),
        HeartbeatInterval = TimeSpan.FromMilliseconds(100),
        DirectPeers = [new(IPAddress.Loopback, source.PeerPort), new(IPAddress.Loopback, target.PeerPort)]
    });
    probe.Publish(false, 12.5);
    await probe.StartAsync();
    await Until(() => LocalPeers(source).Snapshot().Any(p => p.DeviceName == "Pairing transport test") &&
        LocalPeers(target).Snapshot().Any(p => p.DeviceName == "Pairing transport test"));
    Check(true, "source and receiver accept real encrypted UDP status after normal copy and import");
});

await Scenario("failed source group activation preserves the current transport", async () =>
{
    using var profile = new TestProfile(measured: true);
    await using var runtime = new DynexRuntime(profile.Path, new ControlledSpeedTest(), _ => []);
    await runtime.SaveSettingsAsync(runtime.Settings with { AutoConnectLan = true });
    await runtime.StartAsync().WaitAsync(timeout);
    var prior = runtime.Settings;
    var priorPeers = LocalPeers(runtime);
    var priorCode = runtime.ExportPairingCode();
    using (var locked = new FileStream(Path.Combine(profile.Path, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.None))
    {
        var failed = false;
        try { await runtime.ActivatePairingCodeAsync().WaitAsync(timeout); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        Check(failed && runtime.Settings == prior && ReferenceEquals(priorPeers, LocalPeers(runtime)) && runtime.ExportPairingCode() == priorCode,
            "a settings write failure returns no invitation and leaves the source AutoLAN worker and identity intact");
    }
    Check(new SettingsStore(profile.Path).Load() == prior, "failed activation does not persist a different group mode");
});

await Scenario("bounded private-address invitations and legacy compatibility", async () =>
{
    using var profile = new TestProfile(measured: true);
    await using var runtime = new DynexRuntime(profile.Path, new ControlledSpeedTest(), _ => []);
    await runtime.StartAsync().WaitAsync(timeout);
    var prior = runtime.Settings;
    var priorPeers = LocalPeers(runtime);
    var priorCode = runtime.ExportPairingCode();
    var key = GroupKey.Generate();
    string Invitation(object? addresses, string? groupKey = null) => "DYNEX2:" + Convert.ToBase64String(
        JsonSerializer.SerializeToUtf8Bytes(new { Key = groupKey ?? key, Addresses = addresses }));
    foreach (var invalid in new[]
    {
        Invitation(new[] { "8.8.8.8" }), Invitation(new[] { "127.0.0.1" }), Invitation(new[] { "::1" }),
        Invitation(new string?[] { null }), Invitation(null), Invitation(Enumerable.Repeat("192.168.1.5", 17).ToArray()),
        Invitation(new[] { "192.168.1.5" }, "broken"), "DYNEX2:invalid", "DYNEX2:" + new string('A', 4096)
    })
    {
        var rejected = false;
        try { await runtime.ImportPairingCodeAsync(invalid).WaitAsync(timeout); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected && runtime.Settings == prior && ReferenceEquals(priorPeers, LocalPeers(runtime)) && runtime.ExportPairingCode() == priorCode,
            "invalid or oversized invitation is rejected before changing identity, settings or discovery");
    }
    // Parser/persistence fixtures must not send to physical LAN hosts from a loopback-only runtime.
    await runtime.StopAsync().WaitAsync(timeout);
    await runtime.ImportPairingCodeAsync(Invitation(new[] { "192.168.1.5", "192.168.1.5", "10.0.0.8" })).WaitAsync(timeout);
    Check(SettingsStore.Split(runtime.Settings.DirectPeers).SequenceEqual(new[] { "192.168.1.5", "10.0.0.8" }) &&
        new SettingsStore(profile.Path).Load().DirectPeers == runtime.Settings.DirectPeers && runtime.ExportPairingCode() == "DYNEX1:" + key,
        "private direct routes from an invitation are deduplicated and persisted with the imported group");
    var legacy = "DYNEX1:" + GroupKey.Generate();
    await runtime.ImportPairingCodeAsync(legacy).WaitAsync(timeout);
    Check(runtime.ExportPairingCode() == legacy && !runtime.Settings.AutoConnectLan &&
        SettingsStore.Split(runtime.Settings.DirectPeers).Length == 2,
        "legacy invitations remain importable and preserve configured private routes");
    await runtime.SaveSettingsAsync(runtime.Settings with
    { DirectPeers = string.Join(";", Enumerable.Range(1, 32).Select(n => "192.168.2." + n)) });
    var fullSettings = runtime.Settings;
    var overflowRejected = false;
    try { await runtime.ImportPairingCodeAsync(Invitation(new[] { "192.168.3.1" })).WaitAsync(timeout); }
    catch (ArgumentException) { overflowRejected = true; }
    Check(overflowRejected && runtime.Settings == fullSettings && runtime.ExportPairingCode() == legacy &&
        new SettingsStore(profile.Path).LoadIdentity().GroupKey == legacy[7..],
        "combined direct-route limit is checked before changing the saved group identity");
});

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
    var playing = true;
    await using var runtime = new DynexRuntime(profile.Path, service,
        _ => Volatile.Read(ref playing) ? [new("Waiting game", "testgame", 1, null, true, "test")] : []);
    await runtime.SaveSettingsAsync(runtime.Settings with { Mode = GamingMode.On });
    await runtime.StartAsync().WaitAsync(timeout);
    await runtime.StopAsync().WaitAsync(timeout);
    Check(service.Calls == 0 && !runtime.IsSpeedTestWaiting && !HasWorker(runtime) && !HasPeers(runtime) &&
        !new SettingsStore(profile.Path).Load().AutomaticSpeedTestAttempted,
        "stop while waiting cleans up peers and worker without marking an unused attempt");
    Volatile.Write(ref playing, false);
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
    await Until(() => request.Token.IsCancellationRequested && SpeedTestFinished(runtime));
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

await Scenario("newly recognized configured game cancels active measurement immediately", async () =>
{
    using var profile = new TestProfile(measured: true);
    var service = new ControlledSpeedTest();
    await using var runtime = new DynexRuntime(profile.Path, service,
        names => names == "testgame" ? [new("Configured game", "testgame", 1, null, true, "test")] : []);
    await runtime.StartAsync().WaitAsync(timeout);
    var measurement = runtime.MeasureCapacityAsync();
    var request = await service.NextAsync().WaitAsync(timeout);
    var prior = runtime.Settings;
    await runtime.SaveSettingsAsync(prior with { Mode = GamingMode.On, GameProcesses = "testgame" }).WaitAsync(timeout);
    await measurement.WaitAsync(timeout);
    Check(request.Token.IsCancellationRequested && runtime.Settings.Mode == GamingMode.On && runtime.Settings.GameProcesses == "testgame" &&
        runtime.Settings.CapacityMeasuredAtUtc == prior.CapacityMeasuredAtUtc && runtime.Settings.CapacityMbps == prior.CapacityMbps,
        "recognizing an open game through settings while measuring cancels load and preserves the last successful capacity");
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
    remote.Publish(false, downloadMbps: 0, uploadMbps: 0);
    var request = await service.NextAsync().WaitAsync(timeout);
    remote.Publish(true, game: "Remote game");
    await Until(() => request.Token.IsCancellationRequested && SpeedTestFinished(runtime));
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
    var originalWorker = Field(runtime, "worker");
    await Until(() => intercepted && runtime.IsSpeedTestWaiting && !runtime.IsSpeedTestRunning &&
        HasWorker(runtime) && HasPeers(runtime) && !ReferenceEquals(originalWorker, Field(runtime, "worker")));
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
    await Until(() => runtime.Snapshot?.Devices.Count == 1 && runtime.SpeedTestWaitingFor == SpeedTestWaitReason.Network);
    Check(runtime.SpeedTestMessage.StartsWith("Speedtest noch nicht möglich:") && runtime.Snapshot!.NetworkProblem is not null,
        "failed discovery reports a network problem instead of pretending a game is open");
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
        await Until(() => SpeedTestFinished(runtime) && runtime.SpeedTestMessage.Contains("fehlgeschlagen"));
        Check(service.Calls == 0 && !runtime.Settings.AutomaticSpeedTestAttempted &&
            HasWorker(runtime) && !ReferenceEquals(originalWorker, Field(runtime, "worker")) && HasPeers(runtime),
            "failed attempt-marker save starts no download and restores the paused protection lifecycle");
    }
});


await Scenario("actual game packet activity controls limits", () => GameTrafficChecks.Run(Check));

await Scenario("foreground focus gates manual and legacy always-on limits", async () =>
{
    using var profile = new TestProfile(measured: true);
    var service = new ControlledSpeedTest();
    var started = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    GameDetectionResult[] games =
    [
        new("First game", "firstgame", 101, started, true, "test"),
        new("Second game", "secondgame", 202, started.AddMinutes(1), true, "test")
    ];
    ForegroundProcess? foreground = null;
    await using var runtime = new DynexRuntime(profile.Path, service, _ => Volatile.Read(ref games),
        () => Volatile.Read(ref foreground), readGameTraffic: () => Volatile.Read(ref games)
            .Select(g => new GameTrafficIdentity(g.ProcessId!.Value, g.ProcessStartedAt!.Value.UtcTicks)).ToArray());
    await runtime.SaveSettingsAsync(runtime.Settings with { Mode = GamingMode.On, ManualLimitMbps = 25 });
    await runtime.StartAsync().WaitAsync(timeout);
    await Until(() => runtime.Snapshot?.Devices.Count == 1);
    Check(runtime.Snapshot is { IsGaming: false, GamingCount: 0 } && runtime.PlannedDownloadLimitMbps is null,
        "legacy Mode.On and a manual maximum cannot limit downloads while games are only in the background");
    var change = Stopwatch.StartNew();
    Volatile.Write(ref foreground, new(202, "secondgame", started.AddMinutes(1)));
    await Until(() => runtime.Snapshot is { IsGaming: true, Game: "Second game" } && runtime.PlannedDownloadLimitMbps == 25);
    Console.WriteLine($"Foreground acquisition observed after {change.Elapsed.TotalMilliseconds:F0} ms (existing 2-second monitor).");
    Check(runtime.Snapshot!.Devices[0] is { Gaming: true, Game: "Second game" } && PublishedGaming(runtime),
        "actual runtime plans a capped gaming budget and publishes the correctly focused second game");
    change.Restart();
    Volatile.Write(ref foreground, new(999, "Discord", started));
    await Until(() => runtime.Snapshot?.IsGaming == false && runtime.PlannedDownloadLimitMbps is null);
    Console.WriteLine($"Foreground loss observed after {change.Elapsed.TotalMilliseconds:F0} ms (existing 2-second monitor).");
    Check(!PublishedGaming(runtime) && PublishedGame(runtime) == "First game" && runtime.Snapshot!.GamingCount == 0,
        "Alt-Tab releases the actual planned limit and publishes idle gaming while retaining the open game for speedtest safety");
    await runtime.MeasureCapacityAsync().WaitAsync(timeout);
    Check(service.Calls == 0 && runtime.SpeedTestMessage.Contains("nicht gestartet"),
        "a game left open in the background still blocks manual speedtest load");
    Volatile.Write(ref foreground, new(101, "firstgame", started));
    await Until(() => runtime.Snapshot is { IsGaming: true, Game: "First game" } && runtime.PlannedDownloadLimitMbps == 25);
    Check(PublishedGaming(runtime), "returning focus to another running game restores the capped local gaming budget");
    await runtime.SaveSettingsAsync(runtime.Settings with { Mode = GamingMode.Off });
    await Until(() => runtime.Snapshot?.IsGaming == false && runtime.PlannedDownloadLimitMbps is null);
    Check(!PublishedGaming(runtime), "Mode.Off removes the local focus contribution even while the foreground game remains open");
    Volatile.Write(ref games, []);
    await runtime.SaveSettingsAsync(runtime.Settings with { Mode = GamingMode.On });
    await Until(() => runtime.Snapshot is { IsGaming: false, Game: "" });
    Check(runtime.PlannedDownloadLimitMbps is null,
        "legacy Mode.On cannot invent a focused game after all games close");
});

await Scenario("remote focus survives local Alt-Tab and inactive peers release manual caps", async () =>
{
    using var profile = new TestProfile(measured: true);
    var service = new ControlledSpeedTest();
    var started = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    ForegroundProcess? foreground = new(101, "localgame", started);
    await using var runtime = new DynexRuntime(profile.Path, service,
        _ => [new("Local game", "localgame", 101, started, true, "test")], () => Volatile.Read(ref foreground),
        readGameTraffic: () => [new(101, started.UtcTicks)]);
    await runtime.SaveSettingsAsync(runtime.Settings with { ManualLimitMbps = 20 });
    var remoteSettings = CoreConfiguration(runtime) with
    {
        DeviceId = Guid.NewGuid(), DeviceName = "Focused remote test PC", Port = FreePort(),
        HeartbeatInterval = TimeSpan.FromMilliseconds(100), DirectPeers = [new(IPAddress.Loopback, runtime.PeerPort)]
    };
    await using var remote = new PeerService(remoteSettings);
    remote.Publish(true, game: "Remote focused game");
    await remote.StartAsync();
    await runtime.StartAsync().WaitAsync(timeout);
    await Until(() => runtime.Snapshot is { IsGaming: true, GamingCount: 2 } && runtime.PlannedDownloadLimitMbps == 20);
    Volatile.Write(ref foreground, null);
    await Until(() => runtime.Snapshot is { IsGaming: false, GamingCount: 1 } && runtime.PlannedDownloadLimitMbps == 20);
    Check(runtime.Snapshot!.Devices.Single(p => p.Name == "Focused remote test PC").Gaming,
        "local Alt-Tab keeps the real planned download budget while an actual connected UDP peer is focused gaming");
    await runtime.SaveSettingsAsync(runtime.Settings with { Mode = GamingMode.Off });
    await Until(() => runtime.Snapshot is { IsGaming: false, GamingCount: 1 } && runtime.PlannedDownloadLimitMbps == 20);
    Check(runtime.PlannedDownloadLimitMbps == 20,
        "Mode.Off disables only the local focus contribution and still protects a focused peer");
    remote.Publish(false, game: "Remote background game");
    await Until(() => runtime.Snapshot is { GamingCount: 0 } && runtime.PlannedDownloadLimitMbps is null);
    Check(runtime.Snapshot!.SharedDownloadBudgetMbps is null && !PublishedGaming(runtime),
        "remote Alt-Tab releases both automatic and manual limits when no PC has a focused game");
    await runtime.MeasureCapacityAsync().WaitAsync(timeout);
    Check(service.Calls == 0, "background local and remote games retain the separate speedtest safety lock");
    remote.Publish(true, game: "Remote focused game");
    await Until(() => runtime.Snapshot?.GamingCount == 1 && runtime.PlannedDownloadLimitMbps == 20);
    Check(!runtime.Snapshot!.IsGaming, "remote focus regain restores limits while the local PC stays unfocused");
});

await Scenario("focused gaming cannot bypass the successful measurement gate", async () =>
{
    using var profile = new TestProfile();
    var store = new SettingsStore(profile.Path);
    store.Save(store.Load() with { AutomaticSpeedTestAttempted = true, ManualLimitMbps = 10 });
    var started = DateTimeOffset.UtcNow.AddMinutes(-1);
    await using var runtime = new DynexRuntime(profile.Path, new ControlledSpeedTest(),
        _ => [new("Unmeasured game", "testgame", 101, started, true, "test")], () => new(101, "testgame", started),
        readGameTraffic: () => [new(101, started.UtcTicks)]);
    await runtime.StartAsync().WaitAsync(timeout);
    await Until(() => runtime.Snapshot?.IsGaming == true);
    Check(runtime.PlannedDownloadLimitMbps is null && runtime.Snapshot is
        { SharedCapacityMbps: null, SharedDownloadBudgetMbps: null, SharedBudgetComplete: false },
        "focused gaming and manual maximum still cannot apply limits before a persisted measurement");
});


await Scenario("legacy On without open games allows initial measurement", async () =>
{
    using var profile = new TestProfile();
    var service = new ControlledSpeedTest();
    await using var runtime = new DynexRuntime(profile.Path, service, _ => [], () => null);
    await runtime.SaveSettingsAsync(runtime.Settings with { Mode = GamingMode.On });
    await runtime.StartAsync().WaitAsync(timeout);
    var request = await service.NextAsync().WaitAsync(timeout);
    var measurement = runtime.MeasureCapacityAsync();
    request.Complete(100);
    await measurement.WaitAsync(timeout);
    await Until(() => runtime.Snapshot?.Devices.Count == 1);
    Check(service.Calls == 1 && runtime.Settings.CapacityMeasuredAtUtc is not null && !runtime.Snapshot!.IsGaming &&
        runtime.PlannedDownloadLimitMbps is null,
        "legacy On alone neither blocks a safe initial speedtest nor activates a gaming download limit");
});

await Scenario("real helper capture follows local/remote focused gaming only", async () =>
{
    using var profile = new TestProfile(measured: true);
    using var fixture = await CaptureFixture.CreateAsync();
    var started = DateTimeOffset.UtcNow.AddMinutes(-1);
    GameDetectionResult[] games = [];
    ForegroundProcess? foreground = new(404, "Discord", started);
    await using var runtime = new DynexRuntime(profile.Path, new ControlledSpeedTest(),
        _ => Volatile.Read(ref games), () => Volatile.Read(ref foreground), fixture.Controller,
        readGameTraffic: () => Volatile.Read(ref games)
            .Select(g => new GameTrafficIdentity(g.ProcessId!.Value, g.ProcessStartedAt!.Value.UtcTicks)).ToArray());
    await runtime.SaveSettingsAsync(runtime.Settings with { DriverEnabled = true, ManualLimitMbps = 20 });
    await runtime.StartAsync().WaitAsync(timeout);
    await Until(() => runtime.Snapshot?.ProtectionIdle == true);
    Check(fixture.Created == 0 && !fixture.Controller.StartAttempted && runtime.PlannedDownloadLimitMbps is null,
        "idle/Discord-only startup never starts the elevated helper or opens capture, including a manual maximum");
    Volatile.Write(ref games, [new("Test game", "testgame", 101, started, true, "test")]);
    await Until(() => runtime.Snapshot?.Game == "Test game");
    Check(fixture.Created == 0 && runtime.Snapshot!.ProtectionIdle,
        "an open background game does not enable packet capture or QoS");
    Volatile.Write(ref foreground, new(101, "testgame", started));
    await Until(() => fixture.Created == 1 && runtime.Snapshot is { QosActive: true, DriverActive: true, LimitMbps: 20 });
    Check(!runtime.Snapshot!.ProtectionIdle && fixture.Controller.StartAttempted && fixture.Disposed == 0,
        "local foreground gaming enables the real helper capture with QoS and the measured/manual budget");
    await Until(() => runtime.Snapshot!.History.Count >= 4);
    Check(fixture.Created == 1, "steady focused gaming preserves one capture session across heartbeats");
    Volatile.Write(ref foreground, new(404, "Discord", started));
    await Until(() => runtime.Snapshot?.ProtectionIdle == true && fixture.Disposed == 1);
    Check(runtime.Snapshot is { DriverActive: false, QosActive: false } && runtime.PlannedDownloadLimitMbps is null,
        "Alt-Tab disposes the actual capture device, confirms release and removes QoS/caps while the game remains open");
    Volatile.Write(ref foreground, new(101, "testgame", started));
    await Until(() => fixture.Created == 2 && runtime.Snapshot?.QosActive == true);
    Check(fixture.Disposed == 1, "returning to gaming acquires a fresh capture on the existing passive helper");
    var remoteSettings = CoreConfiguration(runtime) with
    {
        DeviceId = Guid.NewGuid(), DeviceName = "Capture remote PC", Port = FreePort(),
        HeartbeatInterval = TimeSpan.FromMilliseconds(100), DirectPeers = [new(IPAddress.Loopback, runtime.PeerPort)]
    };
    await using var remote = new PeerService(remoteSettings);
    remote.Publish(true, game: "Remote focused game");
    await remote.StartAsync();
    await Until(() => runtime.Snapshot?.GamingCount == 2);
    Volatile.Write(ref foreground, null);
    await Until(() => runtime.Snapshot is { IsGaming: false, GamingCount: 1, QosActive: true });
    Check(fixture.Created == 2 && fixture.Disposed == 1 && !runtime.Snapshot!.ProtectionIdle,
        "a real connected focused gaming peer keeps capture active through local Alt-Tab");
    remote.Publish(false, game: "Remote background game");
    await Until(() => runtime.Snapshot?.ProtectionIdle == true && fixture.Disposed == 2);
    Check(runtime.Snapshot!.GamingCount == 0 && !runtime.Snapshot.QosActive,
        "remote Alt-Tab with only background games releases the actual device and QoS");
    remote.Publish(true, game: "Remote focused game");
    await Until(() => fixture.Created == 3 && runtime.Snapshot?.QosActive == true);
    await remote.DisposeAsync();
    // Peer TTL is ten seconds; allow expiry plus one monitor/ack cycle.
    using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(18)))
        while (!(runtime.Snapshot?.ProtectionIdle == true && fixture.Disposed == 3)) await Task.Delay(20, deadline.Token);
    Check(runtime.Snapshot!.GamingCount == 0 && fixture.Controller.CaptureReleased,
        "expired focused peer releases the actual capture without requiring a local game to close");
    Volatile.Write(ref foreground, new(101, "testgame", started));
    await Until(() => fixture.Created == 4 && runtime.Snapshot?.QosActive == true);
    await runtime.StopAsync().WaitAsync(timeout);
    Check(fixture.Disposed == 4 && fixture.Controller.CaptureReleased && await fixture.Helper.WaitAsync(timeout) == 0,
        "app shutdown releases the final capture and consumes the helper's final release acknowledgement");
});

await Scenario("unmeasured focused gaming uses QoS only and still releases on Alt-Tab", async () =>
{
    using var profile = new TestProfile();
    var store = new SettingsStore(profile.Path);
    store.Save(store.Load() with { AutomaticSpeedTestAttempted = true, DriverEnabled = true, ManualLimitMbps = 10 });
    using var fixture = await CaptureFixture.CreateAsync();
    var started = DateTimeOffset.UtcNow.AddMinutes(-1);
    ForegroundProcess? foreground = new(101, "testgame", started);
    await using var runtime = new DynexRuntime(profile.Path, new ControlledSpeedTest(),
        _ => [new("Unmeasured game", "testgame", 101, started, true, "test")],
        () => Volatile.Read(ref foreground), fixture.Controller,
        readGameTraffic: () => [new(101, started.UtcTicks)]);
    await runtime.StartAsync().WaitAsync(timeout);
    await Until(() => fixture.Created == 1 && runtime.Snapshot?.QosActive == true);
    Check(runtime.Snapshot is { DriverActive: false, LimitMbps: null } && runtime.PlannedDownloadLimitMbps is null,
        "real helper QoS during focused gaming cannot bypass the successful-speedtest cap gate");
    Volatile.Write(ref foreground, null);
    await Until(() => fixture.Disposed == 1 && runtime.Snapshot?.ProtectionIdle == true);
    Check(!runtime.Snapshot!.QosActive, "QoS-only capture is fully removed after unmeasured gaming loses focus");
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

static object Publication(DynexRuntime runtime) => LocalPeers(runtime).GetType()
    .GetField("_local", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(LocalPeers(runtime))!;
static bool PublishedGaming(DynexRuntime runtime)
{
    var publication = Publication(runtime);
    return (bool)publication.GetType().GetProperty("Gaming")!.GetValue(publication)!;
}
static string? PublishedGame(DynexRuntime runtime)
{
    var publication = Publication(runtime);
    return (string?)publication.GetType().GetProperty("Game")!.GetValue(publication);
}

static bool SpeedTestFinished(DynexRuntime runtime) => Field(runtime, "speedTestTask") is Task { IsCompleted: true };
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

// The real desktop controller and helper exchange commands/status over a real
// named pipe. Only the native packet device is replaced; no UAC or kernel access.
sealed class CaptureFixture : IDisposable
{
    private readonly NamedPipeServerStream server;
    private readonly NamedPipeClientStream client;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Process parent = Process.GetCurrentProcess();
    private readonly ConcurrentBag<CaptureTestDevice> devices = [];
    public ShaperController Controller { get; } = new();
    public Task<int> Helper { get; private set; } = null!;
    public int Created => devices.Count;
    public int Disposed => devices.Count(d => d.Disposed);
    public void Inject(CapturedPacket packet)
    {
        var device = devices.FirstOrDefault(d => !d.Disposed);
        if (device is null || !device.TryInject(packet)) packet.Dispose();
    }
    private CaptureFixture(string name)
    {
        server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        client = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
    }
    public static async Task<CaptureFixture> CreateAsync()
    {
        var fixture = new CaptureFixture("dynex-runtime-capture-" + Guid.NewGuid().ToString("N"));
        var accept = fixture.server.WaitForConnectionAsync();
        await fixture.client.ConnectAsync(5000); await accept;
        void Set(string field, object value) => typeof(ShaperController).GetField(field,
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Controller, value);
        Set("pipe", fixture.server);
        Set("writer", new StreamWriter(fixture.server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true });
        var desktopLifetime = new CancellationTokenSource(); // Owned/disposed by the actual controller.
        Set("cancellation", desktopLifetime);
        var read = (Task)typeof(ShaperController).GetMethod("ReadStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Controller, [fixture.server, desktopLifetime.Token])!;
        Set("reader", read);
        Func<IPacketDevice> factory = () =>
        {
            var device = new CaptureTestDevice(); fixture.devices.Add(device); return device;
        };
        fixture.Helper = (Task<int>)typeof(PacketPump).Assembly.GetType("Dynex.Shaper.Program")!
            .GetMethod("RunConnectedAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [fixture.parent, new StreamReader(fixture.client, leaveOpen: true),
                new StreamWriter(fixture.client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true },
                fixture.lifetime, factory])!;
        return fixture;
    }
    public void Dispose()
    {
        lifetime.Cancel(); client.Dispose(); server.Dispose(); parent.Dispose(); lifetime.Dispose();
    }
}

sealed class CaptureTestDevice : IPacketDevice
{
    private readonly BlockingCollection<CapturedPacket> packets = [];
    public bool Disposed { get; private set; }
    public CapturedPacket Receive() => packets.Take();
    public void Send(CapturedPacket packet) { }
    public bool TryInject(CapturedPacket packet)
    {
        try { return packets.TryAdd(packet); }
        catch (InvalidOperationException) { return false; }
    }
    public void Dispose() { Disposed = true; packets.CompleteAdding(); }
}
