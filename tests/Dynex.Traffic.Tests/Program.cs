using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dynex.Shaper;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("PASS: " + name); checks++;
}
byte[] Packet4(bool tcp, int payload = 0, int options = 0)
{
    var ipHeader = 20 + options;
    var packet = new byte[ipHeader + (tcp ? 20 : 8) + payload];
    packet[0] = (byte)(0x40 | ipHeader / 4);
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
    packet[8] = 64; packet[9] = tcp ? (byte)6 : (byte)17;
    IPAddress.Parse("203.0.113.4").GetAddressBytes().CopyTo(packet, 12);
    IPAddress.Parse("192.168.1.8").GetAddressBytes().CopyTo(packet, 16);
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(ipHeader), 443);
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(ipHeader + 2), 51000);
    if (tcp) { packet[ipHeader + 12] = 0x50; packet[ipHeader + 13] = 0x10; }
    else BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(ipHeader + 4), (ushort)(8 + payload));
    return packet;
}
byte[] Packet6(byte[] extensions, byte nextHeader = 17, bool tcp = false, int payload = 0)
{
    var packet = new byte[40 + extensions.Length + (tcp ? 20 : 8) + payload];
    packet[0] = 0x60; packet[6] = nextHeader; packet[7] = 64;
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)(packet.Length - 40));
    IPAddress.Parse("2001:db8::4").GetAddressBytes().CopyTo(packet, 8);
    IPAddress.Parse("2001:db8::8").GetAddressBytes().CopyTo(packet, 24);
    extensions.CopyTo(packet, 40);
    var offset = 40 + extensions.Length;
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset), 443);
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset + 2), 51000);
    if (tcp) packet[offset + 12] = 0x50;
    else BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset + 4), (ushort)(8 + payload));
    return packet;
}

var tcp4 = Packet4(true, 1200);
Check(InboundPacketParser.TryParse(tcp4, out var flow, out var length) && length == 1200 && flow.Protocol == TransportProtocol.Tcp &&
    flow.LocalAddress.Equals(IPAddress.Parse("192.168.1.8")) && flow.RemoteAddress.Equals(IPAddress.Parse("203.0.113.4")) &&
    flow.LocalPort == 51000 && flow.RemotePort == 443, "IPv4 TCP inbound orientation, big-endian ports and payload length");
Check(InboundPacketParser.TryParse(Packet4(true), out _, out length) && length == 0, "pure TCP ACK has zero transport payload for caller bypass");
Check(InboundPacketParser.TryParse(Packet4(true, 20, 12), out _, out length) && length == 20, "IPv4 options move the transport header correctly");
Check(InboundPacketParser.TryParse(Packet4(false, 1400), out var udpFlow, out length) && length == 1400 && udpFlow.Protocol == TransportProtocol.Udp,
    "IPv4 UDP payload excludes its eight-byte header");
var df = Packet4(true); df[6] = 0x40;
Check(InboundPacketParser.TryParse(df, out _), "IPv4 don't-fragment flag remains classifiable");
foreach (var fragment in new ushort[] { 0x2000, 1, 0x8000 })
{
    var bad = Packet4(true); BinaryPrimitives.WriteUInt16BigEndian(bad.AsSpan(6), fragment);
    Check(!InboundPacketParser.TryParse(bad, out _), "IPv4 fragmented/reserved flags fail open: " + fragment);
}
Check(!InboundPacketParser.TryParse(tcp4.AsSpan(0, tcp4.Length - 1), out _), "truncated IPv4 capture is unknown");
var badIhl = Packet4(true); badIhl[0] = 0x44;
Check(!InboundPacketParser.TryParse(badIhl, out _), "IPv4 IHL shorter than twenty bytes is unknown");
var badTcp = Packet4(true); badTcp[32] = 0x40;
Check(!InboundPacketParser.TryParse(badTcp, out _), "TCP data offset below twenty bytes is unknown");
var oversizeTcp = Packet4(true); oversizeTcp[32] = 0xF0;
Check(!InboundPacketParser.TryParse(oversizeTcp, out _), "TCP option length beyond the IP packet is unknown");
var badUdp = Packet4(false); BinaryPrimitives.WriteUInt16BigEndian(badUdp.AsSpan(24), 100);
Check(!InboundPacketParser.TryParse(badUdp, out _), "UDP length beyond the IP payload is unknown");
var shortUdp = Packet4(false); BinaryPrimitives.WriteUInt16BigEndian(shortUdp.AsSpan(24), 7);
Check(!InboundPacketParser.TryParse(shortUdp, out _), "UDP length below its header is unknown");
var icmp = Packet4(false); icmp[9] = 1;
Check(!InboundPacketParser.TryParse(icmp, out _), "ICMP is deliberately not mapped to an application flow");
Check(InboundPacketParser.TryParse(Packet6([], payload: 100), out var flow6, out length) && length == 100 &&
    flow6.LocalAddress.Equals(IPAddress.Parse("2001:db8::8")) && flow6.RemotePort == 443, "IPv6 direct UDP inbound parsing");
var ext = new byte[24]; ext[0] = 43; ext[8] = 60; ext[16] = 6;
Check(InboundPacketParser.TryParse(Packet6(ext, 0, true, 77), out _, out length) && length == 77,
    "IPv6 hop-by-hop, inactive routing and destination extension chain");
var ah = new byte[12]; ah[0] = 17; ah[1] = 1;
Check(InboundPacketParser.TryParse(Packet6(ah, 51, payload: 12), out _, out length) && length == 12, "IPv6 AH length is four-byte based");
var activeRouting = new byte[8]; activeRouting[0] = 17; activeRouting[3] = 1;
Check(!InboundPacketParser.TryParse(Packet6(activeRouting, 43), out _), "IPv6 active routing header is not a final socket destination");
var truncatedExt = new byte[8]; truncatedExt[0] = 17; truncatedExt[1] = 255;
Check(!InboundPacketParser.TryParse(Packet6(truncatedExt, 0), out _), "IPv6 overlong extension header fails open");
foreach (var protocol in new byte[] { 44, 50, 59, 132 })
    Check(!InboundPacketParser.TryParse(Packet6(new byte[8], protocol), out _), "IPv6 fragment/ESP/no-next/unsupported protocol fails open: " + protocol);
var jumbo = Packet6([]); jumbo[4] = jumbo[5] = 0;
Check(!InboundPacketParser.TryParse(jumbo, out _), "IPv6 jumbograms are explicitly unknown");
var tooMany = new byte[17 * 8];
for (var i = 0; i < 17; i++) tooMany[i * 8] = i == 16 ? (byte)17 : (byte)60;
Check(!InboundPacketParser.TryParse(Packet6(tooMany, 60), out _), "IPv6 extension chain work is bounded");
for (var i = 0; i < tcp4.Length; i++)
    if (InboundPacketParser.TryParse(tcp4.AsSpan(0, i), out _)) throw new Exception("Truncated TCP packet accepted at " + i);
Check(true, "every truncation of the declared IPv4 TCP packet is rejected without exceptions");
var random = new Random(17);
for (var i = 0; i < 10000; i++)
{
    var bytes = new byte[random.Next(0, 300)]; random.NextBytes(bytes);
    InboundPacketParser.TryParse(bytes, out _);
}
Check(true, "ten thousand deterministic malformed packets never cause a bounds exception");

var now = DateTimeOffset.UtcNow;
var start = now.AddMinutes(-5);
OwnershipEntry Entry(SocketFlow f, int pid = 123, DateTimeOffset? created = null) => new(f, pid, created ?? start, "example");
var exact = OwnershipSnapshot.Create([Entry(flow)]);
Check(exact.Classify(flow)?.ProcessId == 123 && exact.Classify(flow)?.ProcessStartedAtUtc == start, "TCP exact tuple resolves PID and process creation identity");
Check(exact.Classify(flow with { RemotePort = 444 }) is null, "TCP remote endpoint must match exactly");
Check(exact.Classify(flow with { LocalPort = 51001 }) is null, "TCP local port must match exactly");
Check(OwnershipSnapshot.Create([Entry(flow), Entry(flow)]).Classify(flow)?.ProcessId == 123, "duplicate identical rows agree on one owner");
Check(OwnershipSnapshot.Create([Entry(flow), Entry(flow, 456), Entry(flow)]).Classify(flow) is null, "conflicting TCP owners remain sticky-ambiguous");
Check(OwnershipSnapshot.Create([Entry(flow), Entry(flow, created: start.AddSeconds(1))]).Classify(flow) is null,
    "same PID with different creation identity is ambiguous");
Check(OwnershipSnapshot.Create([Entry(flow, created: now.AddMinutes(1))]).Classify(flow) is null, "process created after table capture cannot validate old PID rows");
Check(OwnershipSnapshot.Create([Entry(flow with { RemotePort = 0, RemoteAddress = IPAddress.Any })]).Classify(flow) is null,
    "TCP listener rows do not classify established traffic");
var udpExact = OwnershipSnapshot.Create([Entry(udpFlow)]);
Check(udpExact.Classify(udpFlow with { RemotePort = 7654, RemoteAddress = IPAddress.Parse("198.51.100.8") })?.ProcessId == 123,
    "UDP remote endpoint is not falsely inferred from local-only OS tables");
var wildcard = udpFlow with { LocalAddress = IPAddress.Any };
Check(OwnershipSnapshot.Create([Entry(wildcard)]).Classify(udpFlow)?.ProcessId == 123, "single unique IPv4 wildcard UDP owner applies to its local port");
Check(OwnershipSnapshot.Create([Entry(wildcard), Entry(udpFlow, 456)]).Classify(udpFlow) is null,
    "UDP exact bind plus competing wildcard owner is ambiguous");
Check(OwnershipSnapshot.Create([Entry(wildcard), Entry(udpFlow)]).Classify(udpFlow)?.ProcessId == 123,
    "UDP matching exact and wildcard rows of the same identity agree");
Check(OwnershipSnapshot.Create([Entry(udpFlow), Entry(udpFlow, 456)]).Classify(udpFlow) is null, "UDP shared-port multiple owners fail open");
var wildcard6 = flow6 with { LocalAddress = IPAddress.IPv6Any };
Check(OwnershipSnapshot.Create([Entry(wildcard6)]).Classify(flow6)?.ProcessId == 123, "IPv6 UDP wildcard matching");
Check(OwnershipSnapshot.Create([Entry(udpFlow), Entry(wildcard6)]).Classify(udpFlow) is null,
    "unknown IPV6_V6ONLY on a wildcard cannot misassign an IPv4 UDP owner");
var scoped1 = flow6 with { LocalAddress = IPAddress.Parse("fe80::1%4"), RemoteAddress = IPAddress.Parse("fe80::2%4") };
var scoped2 = scoped1 with { LocalAddress = IPAddress.Parse("fe80::1%5"), RemoteAddress = IPAddress.Parse("fe80::2%5") };
Check(OwnershipSnapshot.Create([Entry(scoped1), Entry(scoped2, 456)]).Classify(scoped1 with { LocalAddress = IPAddress.Parse("fe80::1") }) is null,
    "packet headers lacking IPv6 zone IDs cannot choose among different scoped owners");
var mapped = flow with { LocalAddress = flow.LocalAddress.MapToIPv6(), RemoteAddress = flow.RemoteAddress.MapToIPv6() };
Check(OwnershipSnapshot.Create([Entry(mapped)]).Classify(flow)?.ProcessId == 123, "IPv4-mapped IPv6 TCP endpoints normalize consistently");
Check(!OwnershipSnapshot.Create([Entry(flow)], now.AddSeconds(-2)).IsFresh &&
    OwnershipSnapshot.Create([Entry(flow)], now.AddSeconds(-2)).Classify(flow) is null, "backdated snapshots expire after one second and return unknown");
var mutable = IPAddress.Parse("fe80::1%4");
var immutableSnapshot = OwnershipSnapshot.Create([Entry(scoped1 with { LocalAddress = mutable })]);
mutable.ScopeId = 99;
Check(immutableSnapshot.Classify(scoped1)?.ProcessId == 123, "external mutable IPAddress scope changes cannot corrupt immutable snapshot keys");

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Live ownership tests require Windows.");
using var tcpListener4 = new TcpListener(IPAddress.Loopback, 0); tcpListener4.Start();
using var client4 = new TcpClient(AddressFamily.InterNetwork);
await client4.ConnectAsync((IPEndPoint)tcpListener4.LocalEndpoint);
using var accepted4 = await tcpListener4.AcceptTcpClientAsync();
using var udp4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
using var tcpListener6 = new TcpListener(IPAddress.IPv6Loopback, 0); tcpListener6.Server.DualMode = false; tcpListener6.Start();
using var client6 = new TcpClient(AddressFamily.InterNetworkV6);
await client6.ConnectAsync((IPEndPoint)tcpListener6.LocalEndpoint);
using var accepted6 = await tcpListener6.AcceptTcpClientAsync();
using var udp6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
using var ownership = new TrafficOwnership();
Check(ownership.Refresh(), "read-only Windows IPv4/IPv6 TCP/UDP table refresh: " + (ownership.LastError ?? "success"));
var snapshot = ownership.Snapshot;
Check(snapshot.IsComplete && snapshot.IsFresh && snapshot.EntryCount >= 6, "native table view is complete and fresh across all four families/transports");
SocketFlow Connected(TcpClient client)
{
    var local = (IPEndPoint)client.Client.LocalEndPoint!; var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
    return new(TransportProtocol.Tcp, local.Address, (ushort)local.Port, remote.Address, (ushort)remote.Port);
}
SocketFlow Bound(UdpClient client)
{
    var local = (IPEndPoint)client.Client.LocalEndPoint!;
    return new(TransportProtocol.Udp, local.Address, (ushort)local.Port,
        local.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback, 443);
}
using var self = Process.GetCurrentProcess();
foreach (var (live, label) in new[] { (Connected(client4), "IPv4 TCP client"), (Connected(accepted4), "IPv4 TCP accepted"),
    (Connected(client6), "IPv6 TCP client"), (Connected(accepted6), "IPv6 TCP accepted"), (Bound(udp4), "IPv4 UDP"), (Bound(udp6), "IPv6 UDP") })
{
    var owner = snapshot.Classify(live);
    Check(owner?.ProcessId == Environment.ProcessId && owner.ProcessStartedAtUtc == new DateTimeOffset(self.StartTime.ToUniversalTime()) &&
        owner.ProcessName == self.ProcessName.ToLowerInvariant(), "live loopback ownership includes correct PID, creation time and process name: " + label);
}
var saved = ownership.Snapshot;
ownership.Dispose();
Check(!ownership.Snapshot.IsComplete && ownership.Snapshot.Classify(Connected(client4)) is null, "dispose atomically invalidates the published ownership view");
try { ownership.Refresh(); Check(false, "refresh after dispose"); } catch (ObjectDisposedException) { Check(true, "refresh after dispose is rejected"); }
await Task.Delay(TimeSpan.FromMilliseconds(1100));
Check(!saved.IsFresh && saved.Classify(Connected(client4)) is null, "a previously saved snapshot cannot remain valid indefinitely");
Console.WriteLine($"{checks} traffic ownership checks passed. Live read-only loopback sockets only; no packet filtering changed.");
