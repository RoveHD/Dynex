using System.Buffers.Binary;
using Dynex.Shaper;

internal static class RealtimeVideoChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var started = DateTimeOffset.UtcNow.AddHours(-1);
        var now = 10d;
        var flow = QosChecks.Flow(false);
        var name = "Discord";
        var pid = 801;
        DateTimeOffset? processStart = started;
        OwnershipSnapshot Snapshot() => OwnershipSnapshot.Create([new(flow, pid, processStart, name)]);
        RealtimeQosPolicy Policy() => new(Snapshot, () => now);
        PacketPriority Send(RealtimeQosPolicy policy, byte[] payload, PacketDirection direction = PacketDirection.Outbound)
        {
            using var packet = QosChecks.Packet(flow, direction, payload);
            return policy.Classify(packet);
        }
        bool VideoFrames(RealtimeQosPolicy policy, PacketDirection direction = PacketDirection.Outbound,
            uint ssrc = 77, byte pt = 101, ushort sequence = 300, uint timestamp = 90000)
        {
            var initialUnknown = true;
            PacketPriority last = PacketPriority.None;
            for (var frame = 0; frame < 3; frame++)
            for (var fragment = 0; fragment < 2; fragment++)
            {
                now += .02;
                last = Send(policy, Rtp(pt, unchecked((ushort)(sequence + frame * 2 + fragment)),
                    unchecked(timestamp + (uint)frame * 3000), ssrc, fragment == 1, fragment == 1 ? 80 : 1200), direction);
                if (frame < 2 || fragment == 0) initialUnknown &= last == PacketPriority.None;
            }
            return initialUnknown && last == PacketPriority.Video;
        }

        foreach (var ipv6 in new[] { false, true })
        foreach (var direction in new[] { PacketDirection.Inbound, PacketDirection.Outbound })
        foreach (var app in new[] { "Discord", "DiscordCanary", "DiscordPTB" })
        {
            name = app;
            flow = QosChecks.Flow(ipv6);
            var policy = Policy();
            check(VideoFrames(policy, direction, sequence: 65532, timestamp: uint.MaxValue - 3000),
                $"{app} IPv{(ipv6 ? 6 : 4)} {direction} coherent encrypted video frames pass probation and sequence/timestamp wrap");
            check(Send(policy, Rtp(101, 2, 5999, 77, true, 80), direction) == PacketPriority.Video,
                "confirmed small video packets remain AF41 instead of receiving voice EF");
            check(Send(policy, Rtp(120, 1, 960, 88, false, 172), direction) == PacketPriority.Voice,
                "Discord interleaved Opus stays voice and does not inherit video classification");
            check(Send(policy, Rtp(101, 2, 5999, 99, false, 1200), direction) == PacketPriority.None,
                "a new Discord SSRC needs its own frame evidence");
            check(Send(policy, Rtp(102, 2, 5999, 77, false, 1200), direction) == PacketPriority.None,
                "a new Discord payload type/retransmission stream needs its own evidence");
        }
        flow = QosChecks.Flow(false);
        name = "Discord";
        foreach (var invalid in new[] { "audio", "single-packet-frames", "no-marker", "constant-timestamp", "constant-sequence", "changing-ssrc", "burst", "malformed-extension", "truncated-csrc", "fixed-video-pt", "rtcp" })
        {
            var policy = Policy();
            var safe = true;
            for (var i = 0; i < 12; i++)
            {
                if (invalid != "burst") now += .02;
                var data = Rtp(invalid == "audio" ? (byte)120 : invalid == "fixed-video-pt" ? (byte)34 : (byte)101,
                    invalid == "constant-sequence" ? (ushort)1 : (ushort)i,
                    invalid == "constant-timestamp" ? 90000 : 90000 + (uint)(invalid == "single-packet-frames" ? i : i / 2) * 3000,
                    invalid == "changing-ssrc" ? (uint)i : 77,
                    invalid != "no-marker" && (invalid == "single-packet-frames" || i % 2 == 1), 172);
                if (invalid == "malformed-extension") { data[0] |= 16; data[14] = 255; data[15] = 255; }
                if (invalid == "truncated-csrc") { data = data[..13]; data[0] |= 15; }
                if (invalid == "rtcp") data[1] = 200;
                var priority = Send(policy, data);
                safe &= invalid == "audio" ? priority == PacketPriority.Voice : priority == PacketPriority.None;
            }
            check(safe, "Discord video rejects " + invalid);
        }
        foreach (var unknownApp in new[] { "chrome", "msedge", "msedgewebview2", "notDiscord", "DiscordUpdater" })
        {
            name = unknownApp;
            var policy = Policy();
            check(!VideoFrames(policy), "RTP video shape does not grant unknown application " + unknownApp + " media identity");
        }
        name = "Discord";
        foreach (var control in new[] { Stun(), Dtls(), new byte[200] })
        {
            var policy = Policy();
            check(Send(policy, control) != PacketPriority.Video, "Discord control/non-media UDP never proves video");
        }
        {
            var policy = Policy();
            check(VideoFrames(policy), "Discord baseline before duplicate/reordering checks");
            check(Send(policy, Rtp(101, 305, 96000, 77, true, 80)) == PacketPriority.None &&
                Send(policy, Rtp(101, 303, 93000, 77, true, 80)) == PacketPriority.None,
                "Discord duplicates/reordered video never fall back to EF voice");
            using var packet = QosChecks.Packet(flow, PacketDirection.Outbound, Rtp(101, 306, 99000, 77, false, 1200));
            packet.Buffer[packet.IpOffset + 1] = 3;
            var transportBefore = packet.Buffer.AsSpan(packet.IpOffset + 20, packet.Length - packet.IpOffset - 20).ToArray();
            var priority = policy.Classify(packet);
            check(priority == PacketPriority.Video && QosPacketMarker.TryMark(packet, priority) &&
                packet.Buffer[packet.IpOffset + 1] >> 2 == 34 && (packet.Buffer[packet.IpOffset + 1] & 3) == 3 &&
                packet.Buffer.AsSpan(packet.IpOffset + 20, packet.Length - packet.IpOffset - 20).SequenceEqual(transportBefore),
                "confirmed Discord classification marks AF41 while preserving ECN and encrypted transport bytes");
        }
        {
            var policy = Policy();
            check(VideoFrames(policy), "Discord baseline before expiry");
            now += 4;
            check(Send(policy, Rtp(101, 306, 99000, 77, false, 1200)) == PacketPriority.None,
                "idle Discord video evidence expires");
            check(VideoFrames(Policy()), "Discord baseline before ownership changes");
            policy = Policy();
            VideoFrames(policy);
            processStart = started.AddMinutes(1);
            check(Send(policy, Rtp(101, 306, 99000, 77, false, 1200)) == PacketPriority.None,
                "PID reuse cannot inherit Discord frame evidence");
            processStart = null;
            check(Send(policy, Rtp(101, 307, 99000, 77, true, 80)) == PacketPriority.None,
                "missing process creation time cannot establish video");
            processStart = started;
            policy.Protect([new(pid, started.UtcTicks)]);
            check(Send(policy, Rtp(101, 308, 102000, 77, false, 1200)) == PacketPriority.Game,
                "explicit game protection outranks realtime video");
        }
        foreach (var ipv6 in new[] { false, true })
        foreach (var direction in new[] { PacketDirection.Inbound, PacketDirection.Outbound })
        foreach (var app in new[] { "Teams", "ms-teams" })
        foreach (var protocol in new[] { TransportProtocol.Tcp, TransportProtocol.Udp })
        foreach (var port in new ushort[] { 50020, 50039, 50040, 50059 })
        {
            name = app;
            flow = QosChecks.Flow(ipv6) with { Protocol = protocol, LocalPort = port };
            check(Send(Policy(), Rtp(111, 1, 12345, 77, true, 1200), direction) == PacketPriority.Video,
                $"{app} IPv{(ipv6 ? 6 : 4)} {direction} {protocol} native video/sharing local port {port}");
        }
        name = "ms-teams";
        foreach (var port in new ushort[] { 50019, 50060, 50070, 60000 })
        {
            flow = QosChecks.Flow(false) with { LocalPort = port };
            check(Send(Policy(), Rtp(111, 1, 12345, 77, true, 1200)) == PacketPriority.None,
                "Teams non-video/custom local port " + port + " is not guessed to be video");
        }
        flow = QosChecks.Flow(false) with { LocalPort = 49000, RemotePort = 50020 };
        check(Send(Policy(), Rtp(111, 1, 12345, 77, true, 1200)) == PacketPriority.None,
            "Teams remote port alone is not Microsoft's client source-port evidence");
        flow = QosChecks.Flow(false) with { LocalPort = 50020 };
        foreach (var control in new[] { Stun(), Dtls(), new byte[200], Rtp(72, 1, 12345, 77, true, 172) })
            check(Send(Policy(), control) == PacketPriority.None, "Teams video port control packets are neither video nor voice");
        name = "chrome";
        check(Send(Policy(), Rtp(111, 1, 12345, 77, true, 1200)) == PacketPriority.None,
            "another process on Teams ports is not video");
        name = "ms-teams";
        flow = flow with { LocalPort = 50000 };
        check(Send(Policy(), Rtp(111, 1, 12345, 77, false, 172)) == PacketPriority.Voice,
            "Teams default audio source port remains EF voice");
        {
            name = "Discord";
            flow = QosChecks.Flow(false);
            var policy = Policy();
            for (uint i = 0; i < 1024; i++) Send(policy, Rtp(101, 1, 90000, i, false, 1200));
            check(!VideoFrames(policy, ssrc: 9999), "Discord probation state has a bounded stream capacity");
            now += 4;
            check(VideoFrames(policy, ssrc: 9999), "expired Discord probation entries release capacity");
        }
        {
            var random = new Random(7163);
            var policy = Policy();
            for (var i = 0; i < 2500; i++)
            {
                now += .001;
                var payload = new byte[random.Next(0, 1501)];
                random.NextBytes(payload);
                if (i % 2 == 0 && payload.Length > 0) payload[0] = (byte)(128 | (payload[0] & 63));
                Send(policy, payload, i % 2 == 0 ? PacketDirection.Outbound : PacketDirection.Inbound);
            }
            check(true, "2500 bounded random/truncated RTP, CSRC, extension and control inputs do not crash realtime classification");
        }
    }

    private static byte[] Rtp(byte pt, ushort sequence, uint timestamp, uint ssrc, bool marker, int size)
    {
        var result = new byte[size]; result[0] = 128; result[1] = (byte)(pt | (marker ? 128 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), ssrc);
        return result;
    }
    private static byte[] Stun()
    {
        var result = new byte[20]; result[1] = 1; BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), 0x2112a442); return result;
    }
    private static byte[] Dtls()
    {
        var result = new byte[40]; result[0] = 22; result[1] = 254; result[2] = 253; return result;
    }
}
