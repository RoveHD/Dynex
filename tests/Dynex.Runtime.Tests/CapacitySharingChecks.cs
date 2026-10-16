using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Dynex.Core;
using Dynex.Desktop;

internal static class CapacitySharingChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        using var profile = new TestProfile();
        var store = new SettingsStore(profile.Path);
        store.Save(store.Load() with { AutomaticSpeedTestAttempted = true, DriverEnabled = true, UploadCapacityMbps = 8 });
        var saved = File.ReadAllText(Path.Combine(profile.Path, "settings.json"));
        using var fixture = await CaptureFixture.CreateAsync();
        await using var runtime = new DynexRuntime(profile.Path, new ControlledSpeedTest(), _ => [],
            shaper: fixture.Controller);
        var local = (DynexSettings)typeof(DynexRuntime).GetMethod("CoreSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime, null)!;
        check(!local.CapacityMeasured && local.UploadCapacityMbps is null,
            "unmeasured desktop publishes neither numerical download defaults nor an unverified stored upload");
        using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Dispose();
        await using var remote = new PeerService(local with
        {
            DeviceId = Guid.NewGuid(), DeviceName = "Capacity source PC", Port = port,
            HeartbeatInterval = TimeSpan.FromMilliseconds(200), DirectPeers = [new(IPAddress.Loopback, runtime.PeerPort)],
            CapacityMeasured = true, LineMbps = 800, UploadCapacityMbps = 120, HeadroomPercent = 20, GamingReserveMbps = 10
        });
        remote.Publish(true, game: "Remote active game");
        await remote.StartAsync();
        await runtime.StartAsync();
        await Until(() => runtime.Snapshot is { CapacityFromPeer: true, SharedCapacityMbps: 800,
            SharedUploadCapacityMbps: 120, DriverActive: true, LimitMbps: 315, UploadLimitMbps: 48 });
        check(runtime.PlannedDownloadLimitMbps == 315 && !runtime.Snapshot!.LimitReason.Contains("Speedtest gespeichert") &&
            runtime.Snapshot.Detail.Contains("verbundenem PC"),
            "fresh authenticated PC capacity enables both real fake-helper caps and truthful peer-source explanation without local measurement");
        await Until(() => remote.Snapshot().Count == 1);
        check(remote.Snapshot().Single().BudgetProposal is null && !runtime.Settings.CapacityMeasuredAtUtc.HasValue &&
            runtime.Settings.CapacityMbps == 80 && runtime.Settings.UploadCapacityMbps == 8 &&
            File.ReadAllText(Path.Combine(profile.Path, "settings.json")) == saved,
            "effective synchronization never writes remote capacity into local settings, invents a timestamp or re-advertises it");
        remote.Publish(false);
        await Until(() => runtime.Snapshot is { GamingCount: 0, DriverActive: false, UploadLimitMbps: null, CapacityFromPeer: true });
        check(runtime.PlannedDownloadLimitMbps is null && runtime.Snapshot!.SharedCapacityMbps == 800,
            "game end releases both directions while fresh remote line metadata remains visible");
        await remote.StopAsync();
        await Until(() => runtime.Snapshot is { CapacityFromPeer: false, SharedCapacityMbps: null, SharedUploadCapacityMbps: null }, 20);
        check(runtime.PlannedDownloadLimitMbps is null && runtime.Snapshot!.UploadLimitMbps is null &&
            runtime.Settings.CapacityMeasuredAtUtc is null && File.ReadAllText(Path.Combine(profile.Path, "settings.json")) == saved,
            "expired authenticated peer removes effective down/up capacity without restoring a fake numeric default or persisting stale values");
        remote.Publish(false);
        await remote.StartAsync();
        await Until(() => runtime.Snapshot is { CapacityFromPeer: true, SharedCapacityMbps: 800 });
        await runtime.StopAsync();
        check(runtime.Snapshot is { CapacityFromPeer: false, SharedCapacityMbps: null, SharedUploadCapacityMbps: null },
            "stopping a live peer session discards effective capacity before a different network can start");
        check(await fixture.Helper.WaitAsync(TimeSpan.FromSeconds(8)) == 0,
            "capacity-sharing helper releases cleanly without native capture, UAC, LAN or speedtest traffic");
    }

    private static async Task Until(Func<bool> condition, int seconds = 10)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(20, deadline.Token);
    }
}
