using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using Dynex.Shaper;

var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
var started = DateTimeOffset.UtcNow.AddDays(-1);
var now = 0d;
var flow = new SocketFlow(TransportProtocol.Tcp, IPAddress.Parse("10.0.0.5"), 50000, IPAddress.Parse("203.0.113.8"), 443);
OwnershipEntry[] owners = [new(flow, 501, started, "steam")];
OwnershipSnapshot Snapshot() => OwnershipSnapshot.Create(owners);
var policy = new BulkDownloadPolicy(Snapshot, [], () => now);

CapturedPacket Packet(SocketFlow f, int payload = 1400, bool ethernet = false, PacketDirection direction = PacketDirection.Inbound)
{
    var transportSize = f.Protocol == TransportProtocol.Tcp ? 20 : 8;
    var ipv6 = f.LocalAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
    var headerSize = ipv6 ? 40 : 20;
    var length = headerSize + transportSize + payload;
    var offset = ethernet ? 14 : 0;
    var rented = ArrayPool<byte>.Shared.Rent(length + offset);
    rented.AsSpan(0, length + offset).Clear();
    var p = rented.AsSpan(offset, length);
    if (ipv6)
    {
        p[0] = 0x60; p[6] = (byte)f.Protocol;
        BinaryPrimitives.WriteUInt16BigEndian(p[4..], (ushort)(transportSize + payload));
        f.RemoteAddress.GetAddressBytes().CopyTo(p[8..]); f.LocalAddress.GetAddressBytes().CopyTo(p[24..]);
    }
    else
    {
        p[0] = 0x45; p[9] = (byte)f.Protocol;
        BinaryPrimitives.WriteUInt16BigEndian(p[2..], (ushort)length);
        f.RemoteAddress.GetAddressBytes().CopyTo(p[12..]); f.LocalAddress.GetAddressBytes().CopyTo(p[16..]);
    }
    BinaryPrimitives.WriteUInt16BigEndian(p[headerSize..], f.RemotePort); BinaryPrimitives.WriteUInt16BigEndian(p[(headerSize + 2)..], f.LocalPort);
    if (f.Protocol == TransportProtocol.Tcp) p[headerSize + 12] = 0x50;
    else BinaryPrimitives.WriteUInt16BigEndian(p[(headerSize + 4)..], (ushort)(payload + 8));
    return new(rented, length + offset) { IpOffset = offset, Direction = direction };
}
bool Feed(BulkDownloadPolicy p, SocketFlow f, int count = 300, double step = .002, int payload = 1400)
{
    var limited = false;
    for (var i = 0; i < count; i++) { now += step; using var packet = Packet(f, payload); limited = p.ShouldLimit(packet); }
    return limited;
}
using (var packet = Packet(flow)) Check(!policy.ShouldLimit(packet), "first small request bypasses immediately");
Check(!Feed(policy, flow, 50), "short download burst bypasses");
Check(Feed(policy, flow), "sustained Steam download enters limiter");
using (var packet = Packet(flow, 0)) Check(!policy.ShouldLimit(packet), "TCP ACK bypasses even for large download");
policy.Protect([new(501, started.UtcTicks)]);
using (var packet = Packet(flow)) Check(!policy.ShouldLimit(packet), "game identity immediately bypasses existing bulk flow");
policy.Protect([new(501, started.AddMinutes(-1).UtcTicks)]);
using (var packet = Packet(flow)) Check(policy.ShouldLimit(packet), "reused PID does not inherit stale game exemption");
policy.Protect([]);
now += 6;
using (var packet = Packet(flow)) Check(!policy.ShouldLimit(packet), "idle connection returns to normal interactive classification");

foreach (var name in new[] { "Discord", "discordcanary", "DiscordPTB", "ms-teams", "Zoom", "ts3client_win64", "mumble" })
{
    owners = [new(flow, 501, started, name)];
    Check(!Feed(new(Snapshot, [], () => now), flow, 1200), name + " bypasses even under heavy traffic");
}
owners = [new(flow, 501, started, "steam")];
var low = new BulkDownloadPolicy(Snapshot, [], () => now);
Check(!Feed(low, flow, 2000, .02, 200), "low-bandwidth sustained traffic remains free");
var udp = flow with { Protocol = TransportProtocol.Udp };
owners = [new(udp, 501, started, "browser")];
Check(Feed(new(Snapshot, [], () => now), udp), "large QUIC/UDP download also limited");
owners = [];
Check(!Feed(new(Snapshot, [], () => now), udp), "unknown socket owner bypasses");
owners = [new(udp, 501, null, "steam")];
Check(!Feed(new(Snapshot, [], () => now), udp), "unreadable process creation time bypasses");
owners = [new(udp, 501, started, "steam"), new(udp, 502, started, "game")];
Check(!Feed(new(Snapshot, [], () => now), udp), "ambiguous UDP bind bypasses");
owners = [new(flow, 501, started, "steam")];
var stale = OwnershipSnapshot.Create(owners, DateTimeOffset.UtcNow.AddSeconds(-2));
Check(!Feed(new(() => stale, [], () => now), flow), "stale ownership bypasses rather than throttling wrong process");
var lan = flow with { RemoteAddress = IPAddress.Parse("192.168.1.20") };
owners = [new(lan, 501, started, "steam")];
Check(!Feed(new(Snapshot, [], () => now), lan), "LAN transfer bypasses");
var publicLan = flow with { RemoteAddress = IPAddress.Parse("198.51.100.20") };
owners = [new(publicLan, 501, started, "steam")];
Check(!Feed(new(Snapshot, [(IPAddress.Parse("198.51.100.1"), 24)], () => now), publicLan), "on-link public subnet bypasses");
var dns = udp with { RemotePort = 53 };
owners = [new(dns, 501, started, "steam")];
Check(!Feed(new(Snapshot, [], () => now), dns), "DNS bypasses independently of transfer size");
owners = [new(flow, 501, started, "steam")];
var eth = new BulkDownloadPolicy(Snapshot, [], () => now);
Feed(eth, flow);
using (var packet = Packet(flow, ethernet: true)) Check(eth.ShouldLimit(packet), "Ethernet packet offset honored");
using (var packet = Packet(flow)) { packet.IpOffset = -1; Check(!eth.ShouldLimit(packet), "non-IP packet bypasses"); }

var manyFlows = Enumerable.Range(0, 24).Select(i => flow with { LocalPort = (ushort)(50000 + i) }).ToArray();
owners = manyFlows.Select(f => new OwnershipEntry(f, 501, started, "browser")).ToArray();
var parallel = new BulkDownloadPolicy(Snapshot, [], () => now);
var limitedCount = 0;
for (var round = 0; round < 60; round++)
{
    now += .01;
    foreach (var f in manyFlows) { using var packet = Packet(f); if (parallel.ShouldLimit(packet)) limitedCount++; }
}
Check(limitedCount > 0, "parallel smaller connections sharing a heavy downloader are limited");
Check(ControlProtocol.ParseCommand("{\"mbps\":4,\"protectedProcesses\":[{\"processId\":22,\"startedAtUtcTicks\":123}]} ").ProtectedProcesses.Single().ProcessId == 22,
    "game process identities cross control protocol");
try { ControlProtocol.ParseCommand("{\"mbps\":4,\"protectedProcesses\":[{\"processId\":-1}]}"); throw new Exception("bad PID accepted"); }
catch (FormatException) { Check(true, "invalid protected PID rejected"); }

// Integrate the real classifier and queue, with two competing downloads plus
// IPv4/IPv6 game and Discord packets. No network adapter or kernel filter opens.
{
    var download2 = flow with { LocalPort = 50100 };
    var game4 = flow with { Protocol = TransportProtocol.Udp, LocalPort = 51000, RemotePort = 27015 };
    var game6 = game4 with { LocalAddress = IPAddress.Parse("2001:db8:1::5"), RemoteAddress = IPAddress.Parse("2001:db8:2::8"), LocalPort = 51001 };
    var voice4 = game4 with { LocalPort = 52000, RemotePort = 50000 };
    var voice6 = game6 with { LocalPort = 52001, RemotePort = 50000 };
    owners = [new(flow, 501, started, "steam"), new(download2, 502, started, "browser"),
        new(game4, 601, started, "Marvel-Win64-Shipping"), new(game6, 601, started, "Marvel-Win64-Shipping"),
        new(voice4, 701, started, "Discord"), new(voice6, 701, started, "Discord")];
    var combined = new BulkDownloadPolicy(Snapshot, [], () => now);
    combined.Protect([new(601, started.UtcTicks)]);
    var realtime = new RealtimeQosPolicy(Snapshot, () => now);
    realtime.Protect([new(601, started.UtcTicks)]);
    Check(Feed(combined, flow) && Feed(combined, download2), "two distinct download processes are classified as bulk");
    var delivered = new ConcurrentDictionary<ushort, int>();
    var interactiveDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var interactiveCount = 0;
    var device = new PacketTestDevice(packet =>
    {
        if (!InboundPacketParser.TryParse(packet.Buffer.AsSpan(packet.IpOffset, packet.Length - packet.IpOffset), out var sentFlow, out _))
            throw new Exception("Invalid integrated packet fixture.");
        if (sentFlow.LocalPort is 51000 or 51001 or 52000 or 52001)
        {
            delivered.AddOrUpdate(sentFlow.LocalPort, 1, (_, count) => count + 1);
            if (Interlocked.Increment(ref interactiveCount) == 128) interactiveDone.TrySetResult();
        }
    });
    for (var i = 0; i < 100; i++) { device.Enqueue(Packet(flow)); device.Enqueue(Packet(download2)); }
    await using var pump = new PacketPump(device, .1, combined.ShouldLimit, realtime.Classify);
    var deadline = DateTime.UtcNow.AddSeconds(2);
    while (pump.ReceivedBytes < 288000 && DateTime.UtcNow < deadline) await Task.Delay(5);
    Check(pump.ReceivedBytes >= 288000 && pump.DroppedPackets > 0, "two bulk flows fill the single shared bounded queue");
    long interactiveBytes = 0;
    for (var i = 0; i < 32; i++)
        foreach (var interactive in new[] { game4, game6, voice4, voice6 })
        {
            var packet = Packet(interactive, 200);
            interactiveBytes += packet.Length;
            device.Enqueue(packet);
        }
    await interactiveDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
    // BypassedBytes is incremented immediately after Send returns.
    deadline = DateTime.UtcNow.AddSeconds(1);
    while (pump.BypassedBytes < interactiveBytes && DateTime.UtcNow < deadline) await Task.Delay(5);
    Check(new ushort[] { 51000, 51001, 52000, 52001 }.All(port => delivered.GetValueOrDefault(port) == 32),
        "all IPv4/IPv6 game and Discord packets pass while both downloads are saturated");
    Check(pump.BypassedBytes == interactiveBytes && pump.SentBytes < 288000,
        "protected traffic bypasses the download backlog instead of sharing its cap");
    Check(pump.GamePriorityPackets == 64 && pump.VoicePriorityPackets > 0,
        "real QoS classifier counts successful v4/v6 game/voice priority delivery with two saturated downloads");
}
owners = [new(flow, 501, started, "WhatsApp")];
Check(Feed(new(Snapshot, [], () => now), flow), "additional voice-app QoS eligibility does not exempt its TCP file downloads");
// Uploads: same flow seen from this PC. The packet's source is the local socket.
CapturedPacket Out(SocketFlow f, int payload = 1400) =>
    Packet(new SocketFlow(f.Protocol, f.RemoteAddress, f.RemotePort, f.LocalAddress, f.LocalPort), payload, direction: PacketDirection.Outbound);
bool FeedUpload(BulkDownloadPolicy p, SocketFlow f, int count = 300, double step = .002, int payload = 1400)
{
    var limited = false;
    for (var i = 0; i < count; i++) { now += step; using var packet = Out(f, payload); limited = p.ShouldLimitUpload(packet); }
    return limited;
}
owners = [new(flow, 601, started, "OneDrive")];
var uploads = new BulkDownloadPolicy(Snapshot, [], () => now);
using (var packet = Out(flow)) Check(!uploads.ShouldLimitUpload(packet), "first small upload request bypasses immediately");
Check(FeedUpload(uploads, flow), "sustained cloud-sync upload enters the upload limiter");
using (var packet = Out(flow, 0)) Check(!uploads.ShouldLimitUpload(packet), "TCP ACKs of downloads are never upload-limited");
using (var packet = Out(flow)) Check(!uploads.ShouldLimit(packet), "outbound packets never enter the download limiter");
using (var packet = Packet(flow)) Check(!uploads.ShouldLimitUpload(packet), "inbound packets never enter the upload limiter");
uploads.Protect([new(601, started.UtcTicks)]);
using (var packet = Out(flow)) Check(!uploads.ShouldLimitUpload(packet), "a game's own uploads are never limited, even as a bulk flow");
uploads.Protect([]);
owners = [new(flow, 602, started, "Discord")];
Check(!FeedUpload(new(Snapshot, [], () => now), flow, 1200), "voice chat uploads are never limited");
owners = [];
Check(!FeedUpload(new(Snapshot, [], () => now), flow), "uploads of unknown owners stay free");

// Pump: a saturated upload is paced while game packets and inbound traffic pass immediately.
var game = new SocketFlow(TransportProtocol.Udp, IPAddress.Parse("10.0.0.5"), 52010, IPAddress.Parse("198.51.100.20"), 27015);
owners = [new(flow, 601, started, "OneDrive"), new(game, 700, started, "game")];
var pumpPolicy = new BulkDownloadPolicy(Snapshot, []);
pumpPolicy.Protect([new(700, started.UtcTicks)]);
var sentOut = new ConcurrentDictionary<ushort, int>();
byte[] gameTemplate;
using (var template = Out(game, 200)) gameTemplate = template.Buffer.AsSpan(0, template.Length).ToArray();
var modifiedGamePackets = 0;
using (var device = new PacketTestDevice(packet =>
{
    var port = BinaryPrimitives.ReadUInt16BigEndian(packet.Buffer.AsSpan(packet.IpOffset + 20));
    if (port == 52010 && !packet.Buffer.AsSpan(0, packet.Length).SequenceEqual(gameTemplate)) Interlocked.Increment(ref modifiedGamePackets);
    sentOut.AddOrUpdate(port, 1, (_, n) => n + 1);
}))
{
    var qos = new RealtimeQosPolicy(Snapshot);
    qos.Protect([new(700, started.UtcTicks)]);
    await using var pump = new PacketPump(device, null, pumpPolicy.ShouldLimit, qos.Classify, uploadMbps: 1, shouldLimitUpload: pumpPolicy.ShouldLimitUpload);
    Check(pump.HasUploadLimit && !pump.HasDownloadLimit, "upload-only limit is active without a download limit");
    for (var i = 0; i < 600; i++) device.Enqueue(Out(flow));
    for (var i = 0; i < 40; i++) device.Enqueue(Out(game, 200));
    for (var i = 0; i < 40; i++) device.Enqueue(Packet(flow));
    var deadline = DateTime.UtcNow.AddSeconds(2);
    while (sentOut.GetValueOrDefault((ushort)52010) < 40 && DateTime.UtcNow < deadline) await Task.Delay(5);
    await Task.Delay(300);
    Check(sentOut.GetValueOrDefault((ushort)52010) == 40, "all game packets leave immediately while an upload is saturated");
    Check(modifiedGamePackets == 0, "prioritized game packets leave byte-for-byte unchanged");
    Check(sentOut.GetValueOrDefault((ushort)443) == 40, "inbound download packets are untouched by the upload limit");
    Check(pump.UploadSentBytes < 600 * 1440 / 2, "bulk upload is paced to the configured rate instead of leaving at once");
    pump.UpdateUploadLimit(null);
    Check(!pump.HasUploadLimit, "upload limit can be lifted without restarting capture");
}
// "Limit at once" list: listed programs skip the bulk threshold, games and voice never.
owners = [new(flow, 801, started, "steam")];
var immediate = new BulkDownloadPolicy(Snapshot, [], () => now);
immediate.AlwaysLimit(["Steam"]);
using (var packet = Packet(flow)) Check(immediate.ShouldLimit(packet), "a listed program is limited from its first data packet");
using (var packet = Packet(flow, 0)) Check(!immediate.ShouldLimit(packet), "ACKs of a listed program still pass");
immediate.Protect([new(801, started.UtcTicks)]);
using (var packet = Packet(flow)) Check(!immediate.ShouldLimit(packet), "the list can never limit a protected game");
Check(ControlProtocol.ParseCommand("{\"mbps\":5,\"alwaysLimit\":[\"steam\",\"EpicGamesLauncher\"]}").AlwaysLimit!.Length == 2,
    "always-limit list travels over IPC");

// Accounting: read-only per-program rates and the game's server.
var clockNow = 0d;
var accounting = new TrafficAccounting(() => clockNow);
for (var i = 0; i < 100; i++) accounting.Observe("steam", true, 25_000, false, IPAddress.Parse("203.0.113.8"));
for (var i = 0; i < 50; i++) accounting.Observe("game", false, 2000, true, IPAddress.Parse("198.51.100.20"));
accounting.Observe("game", false, 200, true, IPAddress.Parse("192.168.1.9"));
clockNow = 2;
accounting.Observe("steam", true, 1, false, IPAddress.Parse("203.0.113.8"));
var (top, server) = accounting.Snapshot();
Check(top.Length == 2 && top[0] is { Name: "steam", DownMbps: 10 } && top[1].Name == "game", "per-program rates rank the busiest program first");
Check(server?.ToString() == "198.51.100.20", "the game's public server is identified; LAN addresses are ignored");
Console.WriteLine($"{passed}/{passed} bulk checks passed. No kernel filter installed or activated.");

sealed class PacketTestDevice(Action<CapturedPacket> send) : IPacketDevice
{
    private readonly BlockingCollection<CapturedPacket> source = new();
    private int disposed;
    public void Enqueue(CapturedPacket packet) => source.Add(packet);
    public CapturedPacket Receive() => source.Take();
    public void Send(CapturedPacket packet) => send(packet);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        source.CompleteAdding();
        while (source.TryTake(out var packet)) packet.Dispose();
    }
}
