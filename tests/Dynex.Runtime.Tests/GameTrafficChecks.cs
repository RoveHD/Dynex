using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Dynex.Core;
using Dynex.Desktop;
using Dynex.Desktop.Games;
using Dynex.Shaper;

internal static class GameTrafficChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        using var profile = new TestProfile(measured: true);
        using var fixture = await CaptureFixture.CreateAsync();
        // A bound socket establishes ownership only. All packets below are synthetic
        // and go exclusively into the fake device, never onto a real network.
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        using var process = Process.GetCurrentProcess();
        var started = new DateTimeOffset(process.StartTime.ToUniversalTime());
        GameDetectionResult[] games = [new("Packet test game", "packetgame", process.Id, started, true, "test")];
        ForegroundProcess? foreground = new(process.Id, "packetgame", started);
        await using var runtime = new DynexRuntime(profile.Path, new ControlledSpeedTest(),
            _ => Volatile.Read(ref games), () => Volatile.Read(ref foreground), fixture.Controller);
        // Keep real time/observations but allow this bounded synthetic session to
        // produce a summary without waiting the production two-minute minimum.
        typeof(DynexRuntime).GetField("sessions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(runtime, new SessionTracker(TimeSpan.Zero));
        await runtime.SaveSettingsAsync(runtime.Settings with
        {
            DriverEnabled = true, ManualLimitMbps = 20, UploadCapacityMbps = 40,
            UploadLimitEnabled = true, ManualUploadLimitMbps = 5
        });
        await runtime.StartAsync();
        await Until(() => runtime.Snapshot is { WaitingForGameTraffic: true, ObservingGameTraffic: true });
        check(runtime.Snapshot is { IsGaming: false, DriverActive: false, QosActive: false, UploadLimitMbps: null } &&
            runtime.PlannedDownloadLimitMbps is null && !fixture.Controller.CaptureReleased,
            "a focused game with a bound UDP socket but no packets only observes; download/upload/QoS stay off");

        await Until(() =>
        {
            fixture.Inject(Packet(port, PacketDirection.Inbound));
            return runtime.Snapshot is { IsGaming: true, DriverActive: false, QosActive: true, LimitMbps: null, UploadLimitMbps: null };
        });
        check(runtime.Snapshot!.Devices[0].Gaming && runtime.PlannedDownloadLimitMbps is null,
            "a real owner-table match for synthetic inbound game packets activates helper, IPC and runtime prioritization without caps");

        await Until(() => runtime.Snapshot is
            { WaitingForGameTraffic: true, ObservingGameTraffic: true, IsGaming: false, DriverActive: false, QosActive: false, UploadLimitMbps: null }, 20);
        check(runtime.PlannedDownloadLimitMbps is null && !runtime.Snapshot!.Devices[0].Gaming && !fixture.Controller.CaptureReleased,
            "ten seconds without game packets releases priority and Gaming publication but keeps passive observation");

        var settings = (DynexSettings)typeof(DynexRuntime)
            .GetMethod("CoreSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime, null)!;
        using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var remotePort = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Dispose();
        await using var remote = new PeerService(settings with
        {
            DeviceId = Guid.NewGuid(), DeviceName = "Packet test remote", Port = remotePort,
            HeartbeatInterval = TimeSpan.FromMilliseconds(100), DirectPeers = [new(IPAddress.Loopback, runtime.PeerPort)]
        });
        remote.Publish(true, game: "Network-active remote game");
        await remote.StartAsync();
        await Until(() => runtime.Snapshot is { IsGaming: false, GamingCount: 1, DriverActive: false, QosActive: true, LimitMbps: null });
        check(runtime.Snapshot!.WaitingForGameTraffic,
            "a network-idle local game does not suppress protection requested by an authenticated gaming peer");
        remote.Publish(false, game: "Idle remote game");
        await Until(() => runtime.Snapshot is { GamingCount: 0, DriverActive: false, QosActive: false, ObservingGameTraffic: true });
        check(runtime.PlannedDownloadLimitMbps is null && runtime.Snapshot!.UploadLimitMbps is null,
            "remote idle releases priority while the quiet local game continues to be observed");

        await Until(() =>
        {
            fixture.Inject(Packet(port, PacketDirection.Outbound));
            return runtime.Snapshot is { IsGaming: true, DriverActive: false, QosActive: true, UploadLimitMbps: null };
        });
        check(runtime.PlannedDownloadLimitMbps is null,
            "outbound game packets reactivate protection after an idle period without restarting the game");
        Volatile.Write(ref games, []);
        Volatile.Write(ref foreground, null);
        await Until(() => runtime.Snapshot is { ProtectionIdle: true, ObservingGameTraffic: false });
        check(fixture.Controller.CaptureReleased && fixture.Created == fixture.Disposed,
            "closing the game releases observation and the actual fake capture device completely");
        check(runtime.Snapshot!.LastSession is { LimitedFor: var limitedFor } && limitedFor == TimeSpan.Zero,
            "a session with active QoS records zero bandwidth-limited time");
        await runtime.StopAsync();
        check(await fixture.Helper.WaitAsync(TimeSpan.FromSeconds(8)) == 0,
            "the packet-observation integration helper shuts down cleanly without native capture or UAC");

        var early = new GameDetectionResult("Early game", "earlygame", 202, started, true, "test");
        var focused = new GameDetectionResult("Focused game", "focusedgame", 101, started, true, "test");
        var candidates = GameProtection.FindCandidates([focused, early], new(101, "focusedgame", started),
            GameDetector.ParseAdditionalNames("earlygame"));
        check(candidates.SequenceEqual([focused, early]),
            "a quiet focused game does not hide a second early-protection game from packet-based selection");
    }

    private static async Task Until(Func<bool> condition, int seconds = 10)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(20, deadline.Token);
    }

    private static CapturedPacket Packet(int localPort, PacketDirection direction)
    {
        const int length = 44;
        var bytes = ArrayPool<byte>.Shared.Rent(length);
        bytes.AsSpan(0, length).Clear();
        bytes[0] = 0x45;
        bytes[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), length);
        var local = IPAddress.Parse("192.168.9.2").GetAddressBytes();
        var remote = IPAddress.Parse("203.0.113.10").GetAddressBytes();
        (direction == PacketDirection.Inbound ? remote : local).CopyTo(bytes, 12);
        (direction == PacketDirection.Inbound ? local : remote).CopyTo(bytes, 16);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(20), (ushort)(direction == PacketDirection.Inbound ? 443 : localPort));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(22), (ushort)(direction == PacketDirection.Inbound ? localPort : 443));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(24), length - 20);
        return new(bytes, length) { Direction = direction };
    }
}
