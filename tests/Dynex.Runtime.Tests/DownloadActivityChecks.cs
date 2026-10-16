using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Dynex.Core;
using Dynex.Desktop;

internal static class DownloadActivityChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        using var profile = new TestProfile(measured: true);
        var store = new SettingsStore(profile.Path);
        store.Save(store.Load() with { SharedDownloadMaximumMbps = 25, ManualLimitMbps = null, Adaptive = false });
        var controller = new ShaperController();
        void Capture(double? demand, double applied = 25)
        {
            typeof(ShaperController).GetField("status", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(controller, new ShaperController.ShaperStatus(true, applied, "Isolated capture status", LimitedDownloadMbps: demand));
            typeof(ShaperController).GetField("lastStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(controller, Stopwatch.GetTimestamp());
        }
        Capture(20);
        await using var runtime = new DynexRuntime(profile.Path, new ControlledSpeedTest(), _ => [], shaper: controller);
        var local = (DynexSettings)typeof(DynexRuntime).GetMethod("CoreSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime, null)!;
        check(local.SharedDownloadMaximumMbps == 25 && local.ManualDownloadLimitMbps is null,
            "runtime maps the shared maximum independently of the local manual cap");
        using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Dispose();
        await using var peer = new PeerService(local with
        {
            DeviceId = Guid.NewGuid(), DeviceName = "Idle gamer", Port = port,
            HeartbeatInterval = TimeSpan.FromMilliseconds(200), DirectPeers = [new(IPAddress.Loopback, runtime.PeerPort)]
        });
        peer.Publish(true, shaperActive: true, appliedLimitMbps: 12.5, limitedDownloadMbps: 0);
        await peer.StartAsync();
        await runtime.StartAsync();
        await Until(() => runtime.Snapshot is { DownloadActivityComplete: true, SharedDownloadBudgetMbps: 25, ConfirmedParticipantCount: 2 }
            && runtime.PlannedDownloadLimitMbps == 25);
        check(runtime.Snapshot!.ParticipantCount == 2 && runtime.Snapshot.GamingCount == 1,
            "idle authenticated gamer stays connected without taking half the active downloader's budget");
        await Until(() => peer.Snapshot().Any(p => p.LimitedDownloadMbps == 20));
        check(peer.Snapshot().Single().LimitedDownloadMbps == 20,
            "runtime publishes classified capture ingress over authenticated LAN rather than adapter throughput");

        Capture(0, 12.5);
        peer.Publish(true, shaperActive: true, appliedLimitMbps: 25, limitedDownloadMbps: 20);
        await Until(() => runtime.PlannedDownloadLimitMbps == 12.5 && runtime.Snapshot is
            { DownloadActivityComplete: true, ConfirmedParticipantCount: 2 });
        check(runtime.Snapshot!.SharedDownloadBudgetMbps == 25,
            "local idle capture confirms a remote downloader's full 25 Mbit/s rather than falsely warning about its share");

        Capture(10, 12.5);
        peer.Publish(true, shaperActive: true, appliedLimitMbps: 12.5, limitedDownloadMbps: 10);
        await Until(() => runtime.PlannedDownloadLimitMbps == 12.5 && runtime.Snapshot is
            { DownloadActivityComplete: true, ConfirmedParticipantCount: 2 });
        check(runtime.Snapshot!.SharedDownloadBudgetMbps == 25,
            "two active downloads split the same 25 Mbit/s pool and both applied shares are confirmed");

        Capture(20);
        peer.Publish(true, shaperActive: true, appliedLimitMbps: 12.5, limitedDownloadMbps: 0);
        await Until(() => runtime.PlannedDownloadLimitMbps == 25 && runtime.Snapshot is
            { DownloadActivityComplete: true, ConfirmedParticipantCount: 2 });
        check(runtime.Snapshot!.SharedDownloadBudgetMbps == 25,
            "a downloader becoming idle restores 25 Mbit/s without disconnecting its gaming peer");

        Capture(20, 12.5);
        peer.Publish(true, shaperActive: true, appliedLimitMbps: 12.5);
        await Until(() => runtime.PlannedDownloadLimitMbps == 12.5 && runtime.Snapshot is { DownloadActivityComplete: false });
        check(runtime.Snapshot!.ConfirmedParticipantCount == 2,
            "legacy unknown demand uses conservative all-device shares without claiming complete activity");

        typeof(ShaperController).GetField("lastStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(controller, Stopwatch.GetTimestamp() - 7 * Stopwatch.Frequency);
        await runtime.RefreshStatusAsync();
        await Until(() => peer.Snapshot().Any(p => p.LimitedDownloadMbps is null));
        check(!runtime.Snapshot!.DownloadActivityComplete,
            "stale helper ingress is published as unknown instead of retained activity or measured idle");
        peer.Publish(false, limitedDownloadMbps: 0);
        await Until(() => runtime.Snapshot is { GamingCount: 0 } && runtime.PlannedDownloadLimitMbps is null);
        check(runtime.PlannedDownloadLimitMbps is null, "shared maximum preserves the rule that no gaming means no download cap");
        await runtime.StopAsync();
        check(runtime.Snapshot is { DownloadActivityComplete: false }, "stopping the network session clears complete download activity");
    }

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, deadline.Token);
    }
}
