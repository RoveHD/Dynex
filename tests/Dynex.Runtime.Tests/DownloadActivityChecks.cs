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
        store.Save(store.Load() with { ManualLimitMbps = 25, Adaptive = true, ManualUploadLimitMbps = 3,
            ApplicationRules = [new("steam", 12, 2)], DriverEnabled = false });
        await using var runtime = new DynexRuntime(profile.Path, new ControlledSpeedTest(), _ => []);
        var local = (DynexSettings)typeof(DynexRuntime).GetMethod("CoreSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime, null)!;
        check(!local.CapacityMeasured && !local.AdaptiveEnabled && local.GamingReserveMbps == 0 &&
            local.ManualDownloadLimitMbps is null, "priority-only runtime advertises no capacity pool or adaptive shaping policy");
        using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Dispose();
        await using var peer = new PeerService(local with
        {
            DeviceId = Guid.NewGuid(), DeviceName = "Legacy gamer", Port = port,
            HeartbeatInterval = TimeSpan.FromMilliseconds(100), DirectPeers = [new(IPAddress.Loopback, runtime.PeerPort)],
            CapacityMeasured = true, LineMbps = 80, HeadroomPercent = 35, GamingReserveMbps = 4
        });
        peer.Publish(true, downloadMbps: 20, uploadMbps: 1, shaperActive: true, appliedLimitMbps: 24, limitedDownloadMbps: 20);
        await peer.StartAsync();
        await runtime.StartAsync();
        await Until(() => runtime.Snapshot is { GamingCount: 1, ParticipantCount: 2 });
        check(runtime.PlannedDownloadLimitMbps is null && runtime.Snapshot is
            { SharedDownloadBudgetMbps: null, DriverActive: false, LimitMbps: null, UploadLimitMbps: null },
            "old peer budget and applied cap never activate a local download/upload limit");
        await Until(() => peer.Snapshot().Count == 1);
        check(peer.Snapshot().Single() is { BudgetProposal: null, AppliedLimitMbps: null, ShaperActive: false, LimitedDownloadMbps: null },
            "authenticated publication contains traffic and game status without budget or applied cap");
        foreach (var demand in new double?[] { 0, 10, 80, null })
        {
            peer.Publish(true, downloadMbps: demand, uploadMbps: 1, shaperActive: true, appliedLimitMbps: 1, limitedDownloadMbps: demand);
            await Until(() => runtime.Snapshot!.Devices.Any(d => d.Name == "Legacy gamer" && d.DownloadMbps == demand));
            check(runtime.PlannedDownloadLimitMbps is null && runtime.Snapshot!.SharedDownloadBudgetMbps is null &&
                runtime.Snapshot.Devices.All(d => d.LimitMbps is null),
                $"peer download demand {demand?.ToString() ?? "unknown"} stays visible without changing a cap");
        }
        peer.Publish(false, downloadMbps: 0, uploadMbps: 0);
        await Until(() => runtime.Snapshot is { GamingCount: 0 });
        check(runtime.PlannedDownloadLimitMbps is null, "gaming end leaves all transfers unbounded");
        await runtime.StopAsync();
        check(runtime.Snapshot is { SharedDownloadBudgetMbps: null, DownloadActivityComplete: false },
            "stopping monitoring retains no stale shaping state");
    }

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, deadline.Token);
    }
}
