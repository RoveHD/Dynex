using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Dynex.Core;
using Dynex.Desktop;

internal static class SpeedTestPeerChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var peer = new PeerAdvertisement(Guid.NewGuid(), "Peer", false, 0, null, DateTimeOffset.UtcNow,
            LatencySampleSequence: 1, UploadMbps: 0);
        check(!DynexRuntime.PeerTrafficBlocksSpeedTest([]) && !DynexRuntime.PeerTrafficBlocksSpeedTest([peer]),
            "an empty authenticated peer set and measured idle traffic permit measurement");
        check(DynexRuntime.PeerTrafficBlocksSpeedTest([peer with { DownloadMbps = null }]) &&
            DynexRuntime.PeerTrafficBlocksSpeedTest([peer with { UploadMbps = null }]) &&
            DynexRuntime.PeerTrafficBlocksSpeedTest([peer with { LatencySampleSequence = null }]),
            "missing throughput or measurement sequence never means an idle peer");
        check(DynexRuntime.PeerTrafficBlocksSpeedTest([peer with { DownloadMbps = 25 }]) &&
            DynexRuntime.PeerTrafficBlocksSpeedTest([peer with { UploadMbps = 1 }]) &&
            DynexRuntime.PeerTrafficBlocksSpeedTest([peer with { DownloadMbps = .6 }, peer with { DownloadMbps = .6 }]),
            "competing download, upload and aggregate peer traffic block capacity calibration");
        check(!DynexRuntime.PeerTrafficBlocksSpeedTest([peer with { AppliedLimitMbps = 25,
            BudgetProposal = new(100, 35, 5), ShaperActive = true }]),
            "allocated limits and advertised line capacity are not traffic evidence");

        using var profile = new TestProfile();
        var service = new ControlledSpeedTest();
        await using var runtime = new DynexRuntime(profile.Path, service, _ => []);
        var local = (DynexSettings)typeof(DynexRuntime).GetMethod("CoreSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime, null)!;
        using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Dispose();
        await using var remote = new PeerService(local with
        {
            DeviceId = Guid.NewGuid(), DeviceName = "Downloading phone", Port = port,
            HeartbeatInterval = TimeSpan.FromMilliseconds(100), DirectPeers = [new(IPAddress.Loopback, runtime.PeerPort)],
            CapacityMeasured = false
        });
        remote.Publish(false, downloadMbps: 25, uploadMbps: 0);
        await remote.StartAsync();
        await runtime.StartAsync();
        await Until(() => runtime.SpeedTestWaitingFor == SpeedTestWaitReason.PeerTraffic);
        check(service.Calls == 0 && !runtime.Settings.AutomaticSpeedTestAttempted,
            "automatic calibration waits for a downloading peer without consuming its one automatic attempt");
        remote.Publish(false);
        await Until(() => Peers(runtime).SingleOrDefault()?.DownloadMbps is null);
        check(service.Calls == 0 && runtime.IsSpeedTestWaiting,
            "unknown peer throughput cannot start the waiting calibration");
        remote.Publish(false, downloadMbps: 0, uploadMbps: 0);
        var first = await service.NextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        check(runtime.IsSpeedTestRunning && runtime.Settings.AutomaticSpeedTestAttempted,
            "a fresh measured idle peer allows the automatic test");
        first.Complete(120);
        await Task.Delay(200);
        check(runtime.Settings.CapacityMeasuredAtUtc is null,
            "a fast result is not saved before another actual peer measurement closes the heartbeat gap");
        remote.Publish(false, downloadMbps: 0, uploadMbps: 0);
        await Until(() => !runtime.IsSpeedTestRunning && runtime.Settings.CapacityMbps == 120);
        check(new SettingsStore(profile.Path).Load().CapacityMbps == 120,
            "uncontended capacity is persisted after fresh final peer evidence");
        var storedAt = runtime.Settings.CapacityMeasuredAtUtc;

        foreach (var transfer in new[] { "download", "upload", "unknown" })
        {
            remote.Publish(false, downloadMbps: 0, uploadMbps: 0);
            await Until(() => Peers(runtime).SingleOrDefault() is { DownloadMbps: 0, UploadMbps: 0 });
            var measuring = runtime.MeasureCapacityAsync();
            var request = await service.NextAsync().WaitAsync(TimeSpan.FromSeconds(10));
            remote.Publish(false, downloadMbps: transfer == "download" ? 30 : transfer == "unknown" ? null : 0,
                uploadMbps: transfer == "upload" ? 3 : 0);
            await measuring.WaitAsync(TimeSpan.FromSeconds(10));
            check(request.Token.IsCancellationRequested && runtime.SpeedTestMessage.Contains("anderer DYNEX-Geräte") &&
                runtime.Settings.CapacityMbps == 120 && runtime.Settings.CapacityMeasuredAtUtc == storedAt &&
                new SettingsStore(profile.Path).Load().CapacityMbps == 120,
                $"mid-test peer {transfer} discards the result and preserves the original measured capacity");
        }
        var calls = service.Calls;
        await runtime.RefreshStatusAsync();
        check(service.Calls == calls && !runtime.IsSpeedTestRunning && !runtime.IsSpeedTestWaiting,
            "a contaminated measurement does not enter a retry or capacity reduction loop");
        await runtime.StopAsync();
    }

    private static IReadOnlyList<PeerAdvertisement> Peers(DynexRuntime runtime) =>
        ((IPeerService)typeof(DynexRuntime).GetField("peers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!).Snapshot();

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!condition()) await Task.Delay(20, deadline.Token);
    }
}
