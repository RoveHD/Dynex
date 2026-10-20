using System.Diagnostics;
using System.Reflection;
using Dynex.Desktop;
using Dynex.Desktop.Games;

internal static class NetworkProfileRaceChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var a = new NetworkIdentity(Guid.NewGuid().ToString("D"), "Home");
        var b = new NetworkIdentity(Guid.NewGuid().ToString("D"), "Hotspot");
        var c = new NetworkIdentity(Guid.NewGuid().ToString("D"), "Office");
        foreach (var reconnect in new[] { true, false })
        {
            using var profile = new TestProfile(measured: true);
            new SettingsStore(profile.Path).Save(Configured([a, b, c], measured: true));
            NetworkIdentity current = a;
            var probe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var signal = false;
            await using var runtime = new DynexRuntime(profile.Path, new ControlledSpeedTest(), _ => [], networkIdentityReader: () =>
            {
                var observed = current;
                if (Volatile.Read(ref signal)) probe.TrySetResult();
                return observed;
            });
            await runtime.StartAsync().WaitAsync(Timeout);
            SuppressPeriodicCheck(runtime);
            var gate = Field<SemaphoreSlim>(runtime, "lifecycle");
            await gate.WaitAsync();
            Task refreshing;
            try
            {
                current = b; Volatile.Write(ref signal, true);
                refreshing = Refresh(runtime, reconnect);
                await probe.Task.WaitAsync(Timeout);
                current = c;
            }
            finally { gate.Release(); }
            await refreshing.WaitAsync(Timeout);
            check(runtime.Settings.ActiveNetworkProfileId == c.Id, $"{reconnect}: fresh probe after lifecycle gate selects C instead of stale B");
            check(runtime.Settings.NetworkProfiles.Single(p => p.Id == b.Id).Policy.CapacityMbps == 81,
                $"{reconnect}: stale B identity never captures another network's policy");
        }

        foreach (var reconnect in new[] { true, false })
        {
            using var profile = new TestProfile();
            new SettingsStore(profile.Path).Save(Configured([a, b], measured: false));
            var speed = new ControlledSpeedTest();
            NetworkIdentity current = a;
            await using var runtime = new DynexRuntime(profile.Path, speed, _ => [], networkIdentityReader: () => current);
            await runtime.StartAsync().WaitAsync(Timeout);
            SuppressPeriodicCheck(runtime);
            check(Field<Task?>(runtime, "speedTestTask") is null && !runtime.IsSpeedTestWaiting,
                $"{reconnect}: unmeasured startup does not schedule automatic calibration");
            current = b;
            await Refresh(runtime, reconnect).WaitAsync(Timeout);
            check(runtime.Settings.ActiveNetworkProfileId == b.Id && speed.Calls == 0 &&
                Field<Task?>(runtime, "speedTestTask") is null && !runtime.IsSpeedTestWaiting,
                $"{reconnect}: network switch changes policy without creating replacement test load");
            var measurement = runtime.MeasureCapacityAsync();
            var request = await speed.NextAsync().WaitAsync(Timeout);
            request.Complete(45);
            await measurement.WaitAsync(Timeout);
            check(runtime.Settings.CapacityMbps == 45 && runtime.Settings.ActiveNetworkProfileId == b.Id &&
                runtime.Settings.NetworkProfiles.Single(p => p.Id == a.Id).Policy.CapacityMeasuredAtUtc is null,
                $"{reconnect}: explicit diagnostic result is stored only in the positively identified current network");
        }

        // A loaded test holds lifecycle. Refresh must cancel/join it outside the gate.
        {
            using var profile = new TestProfile();
            new SettingsStore(profile.Path).Save(Configured([a, b], measured: false));
            var speed = new ControlledSpeedTest(); NetworkIdentity current = a;
            await using var runtime = new DynexRuntime(profile.Path, speed, _ => [], networkIdentityReader: () => current);
            await runtime.StartAsync().WaitAsync(Timeout);
            SuppressPeriodicCheck(runtime);
            var measurement = runtime.MeasureCapacityAsync();
            var oldRequest = await speed.NextAsync().WaitAsync(Timeout);
            var previous = Field<Task>(runtime, "speedTestTask");
            current = b;
            await runtime.ReconnectNetworkAsync().WaitAsync(Timeout);
            check(oldRequest.Token.IsCancellationRequested && previous.IsCompleted && runtime.Settings.ActiveNetworkProfileId == b.Id,
                "loaded test is canceled and joined before reconnect obtains lifecycle; no deadlock");
            check(speed.Calls == 1 && !runtime.IsSpeedTestWaiting, "network reconnect does not retry canceled diagnostics automatically");
            var replacement = runtime.MeasureCapacityAsync();
            var request = await speed.NextAsync().WaitAsync(Timeout); request.Complete(46);
            await replacement.WaitAsync(Timeout);
            check(runtime.Settings.CapacityMbps == 46 && runtime.Settings.NetworkProfiles.Single(p => p.Id == a.Id).Policy.CapacityMeasuredAtUtc is null,
                "canceled loaded result cannot consume or corrupt new network measurement");
        }

        foreach (var unidentified in new[] { false, true })
        {
            using var profile = new TestProfile(measured: true);
            new SettingsStore(profile.Path).Save(Configured([a, b], measured: true));
            var speed = new ControlledSpeedTest(); NetworkIdentity? current = a;
            await using var runtime = new DynexRuntime(profile.Path, speed, _ => [], networkIdentityReader: () => current);
            await runtime.StartAsync().WaitAsync(Timeout);
            SuppressPeriodicCheck(runtime);
            current = unidentified ? null : b;
            await runtime.MeasureCapacityAsync().WaitAsync(Timeout);
            check(speed.Calls == 0 && runtime.Settings.CapacityMbps == 80 && runtime.Settings.ActiveNetworkProfileId == a.Id,
                $"{unidentified}: unknown/mismatched network at load start never measures or overwrites active policy");
        }
        {
            using var profile = new TestProfile();
            new SettingsStore(profile.Path).Save(Configured([a], measured: false));
            var speed = new ControlledSpeedTest();
            NetworkIdentity? current = null;
            var game = new GameDetectionResult("Synthetic", "Synthetic", 4321, DateTimeOffset.UtcNow.AddMinutes(-1), true, "Configured");
            await using var runtime = new DynexRuntime(profile.Path, speed, _ => [game], networkIdentityReader: () => current);
            await runtime.StartAsync().WaitAsync(Timeout);
            check(runtime.Settings.ActiveNetworkProfileId == a.Id && !runtime.Settings.AutomaticSpeedTestAttempted &&
                typeof(DynexRuntime).GetField("speedTestTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime) is null,
                "unknown startup network preserves saved profiles without assigning/consuming an automatic measurement");
            current = a;
            await Refresh(runtime, reconnect: false).WaitAsync(Timeout);
            check(Field<Task?>(runtime, "speedTestTask") is null && !runtime.IsSpeedTestWaiting,
                "positive identity after unknown startup restores monitoring without scheduling an automatic test");
        }
        {
            using var profile = new TestProfile();
            new SettingsStore(profile.Path).Save(Configured([a, b], measured: false));
            var step = 0;
            var game = new GameDetectionResult("Synthetic", "Synthetic", 4321, DateTimeOffset.UtcNow.AddMinutes(-1), true, "Configured");
            await using var runtime = new DynexRuntime(profile.Path, new ControlledSpeedTest(), _ => [game],
                networkIdentityReader: () => Interlocked.CompareExchange(ref step, 2, 1) == 1 ? b : a);
            await runtime.StartAsync().WaitAsync(Timeout);
            SuppressPeriodicCheck(runtime);
            var previous = Field<Task?>(runtime, "speedTestTask");
            Volatile.Write(ref step, 1);
            await Refresh(runtime, reconnect: false).WaitAsync(Timeout);
            check(previous is null && runtime.Settings.ActiveNetworkProfileId == a.Id &&
                Field<Task?>(runtime, "speedTestTask") is null && !runtime.IsSpeedTestWaiting,
                "A-to-B-to-A identity race preserves A without stale policy or automatic diagnostic load");
        }
    }
    private static UserSettings Configured(NetworkIdentity[] networks, bool measured)
    {
        var now = DateTimeOffset.UtcNow;
        var settings = new UserSettings { DriverEnabled = false, AutoStart = false };
        for (var i = 0; i < networks.Length; i++)
            settings = NetworkProfiles.Activate(settings, networks[i], now) with
            {
                CapacityMbps = 80 + i, CapacityMeasuredAtUtc = measured ? now : null, AutomaticSpeedTestAttempted = measured,
                AutoConnectLan = false, RouterTarget = "127.0.0.1", ExternalTarget = "127.0.0.1"
            };
        return SettingsStore.Normalize(NetworkProfiles.Activate(settings, networks[0], now));
    }
    private static void SuppressPeriodicCheck(DynexRuntime runtime) => typeof(DynexRuntime)
        .GetField("profileCheckAt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, Stopwatch.GetTimestamp());
    private static T Field<T>(DynexRuntime runtime, string name) => (T)typeof(DynexRuntime)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
    private static Task Refresh(DynexRuntime runtime, bool reconnect) => (Task)typeof(DynexRuntime)
        .GetMethod("RefreshNetworkProfileAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime, [reconnect])!;
}
