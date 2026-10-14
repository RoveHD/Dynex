using System.Reflection;
using System.Text.Json;
using Dynex.Desktop;

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
    await using (var runtime = new DynexRuntime(profile.Path, service))
    {
        await runtime.StartAsync().WaitAsync(timeout);
        var request = await service.NextAsync().WaitAsync(timeout);
        Check(runtime.IsSpeedTestRunning && !request.Completion.Task.IsCompleted,
            "first start returns while automatic measurement is pending");
        Check(!HasWorker(runtime), "monitor worker is paused during initial measurement");
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
    await using var restarted = new DynexRuntime(profile.Path, restartedService);
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
    await using var runtime = new DynexRuntime(profile.Path, service);
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
    await using var runtime = new DynexRuntime(profile.Path, new ControlledSpeedTest());
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
    await using var runtime = new DynexRuntime(profile.Path, service);
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
    Check(service.Calls == 1 && service.MaximumConcurrent == 1 && !HasWorker(runtime),
        "concurrent requests share one test and pause monitoring");
    request.Complete(321.09);
    await Task.WhenAll(measurements).WaitAsync(timeout);
    Check(service.Calls == 1 && service.MaximumConcurrent == 1,
        "all duplicate callers finish without a second measurement");
});

await Scenario("failure preserves prior measured settings", async () =>
{
    using var profile = new TestProfile(measured: true);
    var service = new ControlledSpeedTest();
    await using var runtime = new DynexRuntime(profile.Path, service);
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
        await using var runtime = new DynexRuntime(profile.Path, service);
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
    await using (var runtime = new DynexRuntime(profile.Path, service))
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
    await using var restarted = new DynexRuntime(profile.Path, restartedService);
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
    await using (var runtime = new DynexRuntime(profile.Path, service))
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
    await using var restarted = new DynexRuntime(profile.Path, restartedService);
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
    await using var runtime = new DynexRuntime(profile.Path, service);
    await runtime.StartAsync().WaitAsync(timeout);
    var stale = runtime.Settings with { Mode = GamingMode.On };
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
    Check(saved.Mode == GamingMode.On && Math.Abs(saved.ReservePerGamerMbps - 3.7625) < 0.000001,
        "queued stale save applies mode and recalculates reserve from current capacity");
});

await Scenario("stop cancels measurement and prevents worker restart", () => OnSerialContext(async () =>
{
    using var profile = new TestProfile();
    var service = new ControlledSpeedTest();
    await using var runtime = new DynexRuntime(profile.Path, service);
    await runtime.StartAsync().WaitAsync(timeout);
    var request = await service.NextAsync().WaitAsync(timeout);
    var measurement = runtime.MeasureCapacityAsync();
    var save = runtime.SaveSettingsAsync(runtime.Settings with { Mode = GamingMode.On });
    // This continuation runs inline when Save completes. Stop's semaphore continuation is
    // posted to the serial context and cannot race ahead of this observation.
    var workerRestarted = false;
    var observeSave = save.ContinueWith(_ => workerRestarted = HasWorker(runtime) || HasPeers(runtime),
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

Console.WriteLine($"Runtime checks: {checks} passed, {failures.Count} scenarios failed.");
if (failures.Count != 0)
{
    foreach (var failure in failures) Console.Error.WriteLine(failure);
    Environment.ExitCode = 1;
}

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
