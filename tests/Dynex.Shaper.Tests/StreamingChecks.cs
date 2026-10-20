using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dynex.Shaper;

internal static class StreamingChecks
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var host in new[] { "r1---sn.example.googlevideo.com", "ipv4-c001.nflxvideo.net" })
            check(StreamingVideoPolicy.IsMediaHost(host), "media CDN suffix: " + host);
        foreach (var host in new[] { "youtube.com", "netflix.com", "evilgooglevideo.com", "googlevideo.com.evil.test", "cdn.example", "github.com" })
            check(!StreamingVideoPolicy.IsMediaHost(host), "general/shared/spoofed host stays unmarked: " + host);
        foreach (var rfc in new[] { "9001", "9369" })
        {
            var vector = Convert.FromHexString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", $"rfc{rfc}-client-initial.hex")).Trim());
            check(QuicClientHello.TryReadCrypto(vector, out _, out var fragments) && fragments.Count == 1 &&
                MediaHandshake.ReadClientHello(fragments[0].Bytes, out var host) == HelloResult.Complete && host == "example.com",
                $"independent RFC {rfc} authenticated Initial vector decodes example.com");
            vector[^1] ^= 1;
            check(!QuicClientHello.TryReadCrypto(vector, out _, out _), $"RFC {rfc} tampered AEAD tag rejected");
        }
        foreach (var ipv6 in new[] { false, true })
        foreach (var protocol in new[] { TransportProtocol.Tcp, TransportProtocol.Udp })
        foreach (var app in new[] { "brave", "ELFIX" })
        foreach (var ech in new[] { false, true })
        {
            var flow = QosChecks.Flow(ipv6) with { Protocol = protocol, RemotePort = 443 };
            var started = DateTimeOffset.UtcNow.AddDays(-1);
            var entries = new[] { new OwnershipEntry(flow, 901, started, app) };
            var now = 1d;
            OwnershipSnapshot Snapshot() => OwnershipSnapshot.Create(entries);
            var policy = new RealtimeQosPolicy(Snapshot, () => now);
            var hello = Hello("r2---sn-test.googlevideo.com", ech);
            var payload = protocol == TransportProtocol.Tcp ? Record(hello) : Initial(hello, false);
            using var first = QosChecks.Packet(flow, PacketDirection.Outbound, payload);
            check(policy.Classify(first) == PacketPriority.Video, $"IPv{(ipv6 ? 6 : 4)} {app} {protocol} media outer SNI classified, ECH GREASE={ech}");
            using var ack = QosChecks.Packet(flow, PacketDirection.Outbound, protocol == TransportProtocol.Tcp ? [] : [0x40, 1]);
            check(policy.Classify(ack) == PacketPriority.Video && QosPacketMarker.TryMark(ack, PacketPriority.Video), "subsequent ACK/data gets AF41 for router restoration");
            using var other = QosChecks.Packet(flow with { LocalPort = 50002 }, PacketDirection.Outbound, new byte[100]);
            check(policy.Classify(other) == PacketPriority.None, "same browser and remote address do not lend priority to another connection");
            var reused = protocol == TransportProtocol.Tcp ? Record(Hello("download.example")) : Initial(Hello("download.example"), false, dcidTail: 9);
            using var newHello = QosChecks.Packet(flow, PacketDirection.Outbound, reused);
            if (protocol == TransportProtocol.Tcp) newHello.Buffer[newHello.IpOffset + (ipv6 ? 40 : 20) + 13] = 2;
            check(policy.Classify(newHello) == PacketPriority.None, "new TCP SYN/QUIC DCID clears previous media identity");
            using var later = QosChecks.Packet(flow, PacketDirection.Outbound, new byte[80]);
            check(policy.Classify(later) == PacketPriority.None, "new nonmedia connection cannot inherit video claim");
        }

        var tcp = QosChecks.Flow(false) with { Protocol = TransportProtocol.Tcp, RemotePort = 443 };
        var began = DateTimeOffset.UtcNow.AddDays(-1);
        var owner = new TrafficOwner(902, began, "ELFIX");
        var time = 0d;
        var source = OwnershipSnapshot.Create([new(tcp, 902, began, "ELFIX")]);
        var fragmented = new RealtimeQosPolicy(() => source, () => time);
        var record = Record(Hello("ipv4-c001.nflxvideo.net"));
        using (var part = QosChecks.Packet(tcp, PacketDirection.Outbound, record[..20]))
            check(fragmented.Classify(part) == PacketPriority.None, "partial TLS record is not evidence yet");
        using (var part = QosChecks.Packet(tcp, PacketDirection.Outbound, record[20..]))
        {
            SetSequence(part, 20);
            check(fragmented.Classify(part) == PacketPriority.Video, "bounded contiguous TCP ClientHello reassembly proves video");
        }
        var late = new RealtimeQosPolicy(() => source, () => time);
        source = OwnershipSnapshot.Create([]);
        using (var first = QosChecks.Packet(tcp, PacketDirection.Outbound, record))
            check(late.Classify(first) == PacketPriority.None, "public handshake without fresh owner remains unmarked");
        source = OwnershipSnapshot.Create([new(tcp, 902, began, "ELFIX")]); time = .25;
        using (var ack = QosChecks.Packet(tcp, PacketDirection.Outbound, []))
            check(late.Classify(ack) == PacketPriority.Video, "metadata survives first socket table refresh for exact newly owned tuple");

        foreach (var variant in new[] { Hello("r1.googlevideo.com")[..10], new byte[32769], Hello("public.ech.example", ech: true), Hello(null, ech: true) })
        {
            var p = new RealtimeQosPolicy(() => source, () => time);
            using var packet = QosChecks.Packet(tcp, PacketDirection.Outbound, variant.Length > 32768 ? variant : Record(variant));
            check(p.Classify(packet) == PacketPriority.None, "general/absent ECH outer SNI and truncated/oversized hello remain unmarked");
        }
        foreach (var v2 in new[] { false, true })
        {
            var udp = tcp with { Protocol = TransportProtocol.Udp };
            var state = new StreamingVideoPolicy();
            var hello = Hello("r1.googlevideo.com");
            var tail = Initial(hello[30..], v2, cryptoOffset: 30);
            using var tailPacket = QosChecks.Packet(udp, PacketDirection.Outbound, tail);
            check(!state.Observe(udp, owner, PacketDirection.Outbound, tailPacket.Buffer.AsSpan(tailPacket.IpOffset), tail, 28, time), "out-of-order QUIC CRYPTO waits for missing prefix");
            var head = Initial(hello[..30], v2);
            using var headPacket = QosChecks.Packet(udp, PacketDirection.Outbound, head);
            check(state.Observe(udp, owner, PacketDirection.Outbound, headPacket.Buffer.AsSpan(headPacket.IpOffset), head, 28, time), "QUIC v1/v2 CRYPTO offset reassembly completes media ClientHello");
        }

        var plain = tcp with { RemotePort = 80 };
        var httpSource = OwnershipSnapshot.Create([new(plain, 902, began, "ELFIX")]);
        var httpPolicy = new RealtimeQosPolicy(() => httpSource, () => time);
        using (var media = QosChecks.Packet(plain, PacketDirection.Inbound, Encoding.ASCII.GetBytes("HTTP/1.1 206 Partial Content\r\nContent-Type: video/mp4\r\n\r\n")))
            check(httpPolicy.Classify(media) == PacketPriority.Video, "ELFIX plain HTTP actual video MIME proves media response");
        using (var attachment = QosChecks.Packet(plain, PacketDirection.Inbound, Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: video/mp4\r\nContent-Disposition: attachment; filename=x.mp4\r\n\r\n")))
            check(httpPolicy.Classify(attachment) == PacketPriority.None, "HTTP keep-alive attachment invalidates previous media response");
        using (var ack = QosChecks.Packet(plain, PacketDirection.Outbound, []))
            check(httpPolicy.Classify(ack) == PacketPriority.None, "attachment ACK does not retain video priority");
        foreach (var response in new[] { "HTTP/1.1 404 Missing\r\nContent-Type: video/mp4\r\n\r\n", "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\n\r\n", "HTTP/1.1 200 OK\r\nContent-Type: video/mp4\r\nContent-Type: application/octet-stream\r\n\r\n" })
            check(!StreamingVideoPolicy.IsHttpMedia(Encoding.ASCII.GetBytes(response)), "HTTP nonmedia/error/ambiguous MIME not promoted");
        // Unknown encrypted ELFIX hosters are deliberately not classified by name.
        var unknown = new RealtimeQosPolicy(() => source, () => time);
        using (var packet = QosChecks.Packet(tcp, PacketDirection.Outbound, Record(Hello("unknown-elfix-hoster.example"))))
            check(unknown.Classify(packet) == PacketPriority.None, "ELFIX name alone never promotes encrypted unknown hoster/download");
        var rand = new Random(441);
        var parserSafe = true;
        for (var i = 0; i < 2500; i++)
        {
            var data = new byte[rand.Next(0, 4096)]; rand.NextBytes(data);
            try
            {
                MediaHandshake.ReadClientHello(data, out _);
                MediaHandshake.ReadTlsRecords(data, out _);
                QuicClientHello.TryReadCrypto(data, out _, out _);
                StreamingVideoPolicy.IsHttpMedia(data);
            }
            catch { parserSafe = false; break; }
        }
        check(parserSafe, "2500 malformed/random protocol inputs cannot crash packet classification");
        var bounded = new StreamingVideoPolicy();
        var limitHeld = true;
        for (var i = 0; i < 513; i++)
        {
            var target = tcp with { LocalPort = (ushort)(10000 + i) };
            using var packet = QosChecks.Packet(target, PacketDirection.Outbound, record);
            var classified = bounded.Observe(target, owner, PacketDirection.Outbound,
                packet.Buffer.AsSpan(packet.IpOffset, packet.Length - packet.IpOffset), record, 40, 1);
            limitHeld &= classified == (i < 512);
        }
        check(limitHeld, "media cache has a hard 512-flow bound; excess flows stay best effort");
        using (var packet = QosChecks.Packet(tcp with { LocalPort = 11000 }, PacketDirection.Outbound, record))
            check(bounded.Observe(tcp with { LocalPort = 11000 }, owner, PacketDirection.Outbound,
                packet.Buffer.AsSpan(packet.IpOffset), record, 40, 130), "idle media cache entries expire and free capacity");
    }

    private static void SetSequence(CapturedPacket packet, uint sequence) => BinaryPrimitives.WriteUInt32BigEndian(packet.Buffer.AsSpan(packet.IpOffset + 24, 4), sequence);
    private static byte[] Hello(string? host, bool ech = false)
    {
        using var ext = new MemoryStream();
        if (host is not null)
        {
            var name = Encoding.ASCII.GetBytes(host);
            U16(ext, 0); U16(ext, name.Length + 5); U16(ext, name.Length + 3); ext.WriteByte(0); U16(ext, name.Length); ext.Write(name);
        }
        if (ech)
        {
            // Well-formed outer extension with a GREASE-style opaque payload.
            // No test assumes we can distinguish GREASE from actual ECH.
            using var echData = new MemoryStream();
            echData.WriteByte(0); U16(echData, 1); U16(echData, 1); echData.WriteByte(7);
            U16(echData, 32); echData.Write(new byte[32]); U16(echData, 128); echData.Write(new byte[128]);
            U16(ext, 0xfe0d); U16(ext, (int)echData.Length); ext.Write(echData.ToArray());
        }
        using var body = new MemoryStream(); body.Write([3, 3]); body.Write(new byte[32]); body.WriteByte(0);
        body.Write([0, 2, 0x13, 1, 1, 0]); U16(body, (int)ext.Length); body.Write(ext.ToArray());
        var bytes = body.ToArray(); return [1, (byte)(bytes.Length >> 16), (byte)(bytes.Length >> 8), (byte)bytes.Length, .. bytes];
    }
    private static byte[] Record(byte[] hello) => [22, 3, 1, (byte)(hello.Length >> 8), (byte)hello.Length, .. hello];
    private static void U16(Stream output, int value) { output.WriteByte((byte)(value >> 8)); output.WriteByte((byte)value); }
    private static void Var(Stream output, int value)
    {
        if (value < 64) output.WriteByte((byte)value);
        else U16(output, value | 0x4000);
    }
    private static byte[] Initial(byte[] hello, bool v2, int cryptoOffset = 0, byte dcidTail = 8)
    {
        using var headerStream = new MemoryStream();
        headerStream.WriteByte(v2 ? (byte)0xd3 : (byte)0xc3);
        var version = v2 ? 0x6b3343cfu : 1u;
        Span<byte> versionBytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(versionBytes, version); headerStream.Write(versionBytes);
        byte[] dcid = [0x83, 0x94, 0xc8, 0xf0, 0x3e, 0x51, 0x57, dcidTail];
        headerStream.WriteByte(8); headerStream.Write(dcid); headerStream.WriteByte(0); headerStream.WriteByte(0);
        var plaintextLength = 1200 - (int)headerStream.Length - 2 - 4 - 16;
        Var(headerStream, plaintextLength + 20);
        var pnOffset = (int)headerStream.Length; headerStream.Write([0, 0, 0, 0]);
        var header = headerStream.ToArray();
        using var frames = new MemoryStream(); frames.WriteByte(6); Var(frames, cryptoOffset); Var(frames, hello.Length); frames.Write(hello);
        var plaintext = new byte[plaintextLength]; frames.ToArray().CopyTo(plaintext, 0);
        var salt = Convert.FromHexString(v2 ? "0dede3def700a6db819381be6e269dcbf9bd2ed9" : "38762cf7f55934b34d179ae6a4c80cadccbb7f0a");
        var initial = HKDF.Extract(HashAlgorithmName.SHA256, dcid, salt);
        var client = Expand(initial, "client in", 32);
        var prefix = v2 ? "quicv2 " : "quic ";
        var key = Expand(client, prefix + "key", 16); var iv = Expand(client, prefix + "iv", 12); var hp = Expand(client, prefix + "hp", 16);
        var ciphertext = new byte[plaintext.Length]; var tag = new byte[16];
        using var gcm = new AesGcm(key, 16); gcm.Encrypt(iv, plaintext, ciphertext, tag, header);
        byte[] packet = [.. header, .. ciphertext, .. tag];
        using var aes = Aes.Create(); aes.Key = hp; var mask = aes.EncryptEcb(packet.AsSpan(pnOffset + 4, 16), PaddingMode.None);
        packet[0] ^= (byte)(mask[0] & 15);
        for (var i = 0; i < 4; i++) packet[pnOffset + i] ^= mask[i + 1];
        return packet;
    }
    private static byte[] Expand(byte[] secret, string label, int size)
    {
        var name = Encoding.ASCII.GetBytes("tls13 " + label); var info = new byte[name.Length + 4];
        BinaryPrimitives.WriteUInt16BigEndian(info, (ushort)size); info[2] = (byte)name.Length; name.CopyTo(info, 3);
        return HKDF.Expand(HashAlgorithmName.SHA256, secret, size, info);
    }
}
