using System.Buffers.Binary;
using System.Diagnostics;

namespace Dynex.Shaper;

/// <summary>
/// Receiver-thread-only real-time classification. Unknown ownership/protocols
/// remain unmarked. No payload decryption or browser-wide exemptions.
/// </summary>
public sealed class RealtimeQosPolicy(Func<OwnershipSnapshot> snapshot, Func<double>? clock = null, TrafficAccounting? accounting = null)
{
    // Additional low-volume UDP candidates only; these names do not exempt
    // their file transfers or other traffic from BulkDownloadPolicy.
    private static readonly HashSet<string> AdditionalVoiceApplications = new(StringComparer.OrdinalIgnoreCase)
    { "Signal", "Telegram", "WhatsApp", "Skype", "SkypeApp", "Slack", "Guilded", "TeamSpeak" };
    private readonly Func<double> _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    private ControlProtocol.ProtectedProcess[] _protected = [];
    private readonly Dictionary<StreamKey, RtpState> _rtp = new();
    private readonly Dictionary<OwnerKey, VoiceBudget> _voice = new();
    private double _lastCleanup;

    public void Protect(IEnumerable<ControlProtocol.ProtectedProcess> processes) =>
        Volatile.Write(ref _protected, processes.Take(128).ToArray());

    public PacketPriority Classify(CapturedPacket packet)
    {
        if (packet.IpOffset < 0 || packet.IpOffset >= packet.Length) return PacketPriority.None;
        var ip = packet.Buffer.AsSpan(packet.IpOffset, packet.Length - packet.IpOffset);
        if (!InboundPacketParser.TryParse(ip, out var flow, out var payloadLength, out var payloadOffset)) return PacketPriority.None;
        if (packet.Direction == PacketDirection.Outbound)
            flow = new(flow.Protocol, flow.RemoteAddress, flow.RemotePort, flow.LocalAddress, flow.LocalPort);
        if (flow.LocalPort is 53 or 5353 or 5355 or 39274 || flow.RemotePort is 53 or 5353 or 5355 or 39274)
            return PacketPriority.None;
        var owner = snapshot().Classify(flow);
        if (owner?.ProcessStartedAtUtc is not { } started || string.IsNullOrEmpty(owner.ProcessName)) return PacketPriority.None;
        var isGame = Volatile.Read(ref _protected).Any(p => p.ProcessId == owner.ProcessId && p.StartedAtUtcTicks == started.UtcTicks);
        accounting?.Observe(owner.ProcessName, packet.Direction == PacketDirection.Inbound, packet.Length - packet.IpOffset, isGame, flow.RemoteAddress);
        if (isGame)
        {
            // Game asset downloads must not automatically receive CS4. TCP
            // controls and small interactive payloads, plus standard UDP gaming.
            return payloadLength <= (flow.Protocol == TransportProtocol.Tcp ? 768 : 1400)
                ? PacketPriority.Game : PacketPriority.None;
        }
        if (flow.Protocol != TransportProtocol.Udp || payloadLength is < 1 or > 600) return PacketPriority.None;
        var now = _clock();
        if (now - _lastCleanup > 5)
        {
            foreach (var key in _rtp.Where(p => now - p.Value.LastAt > 2).Select(p => p.Key).ToArray()) _rtp.Remove(key);
            foreach (var key in _voice.Where(p => now - p.Value.LastAt > 5).Select(p => p.Key).ToArray()) _voice.Remove(key);
            _lastCleanup = now;
        }
        var identity = new OwnerKey(owner.ProcessId, started.UtcTicks, packet.Direction);
        var knownApplication = BulkDownloadPolicy.IsVoiceApplication(owner.ProcessName) || AdditionalVoiceApplications.Contains(owner.ProcessName);
        if (!knownApplication && !ObserveAudioRtp(new(flow, identity), ip.Slice(payloadOffset, payloadLength), now)) return PacketPriority.None;
        // EF is metered, not an unlimited whole-application privilege. This
        // includes small video/control packets that cannot be decrypted here.
        if (!_voice.TryGetValue(identity, out var budget))
        {
            if (_voice.Count >= 4096) return PacketPriority.None;
            _voice.Add(identity, budget = new VoiceBudget { LastAt = now, Tokens = 4096 });
        }
        budget.Tokens = Math.Min(4096, budget.Tokens + Math.Max(0, now - budget.LastAt) * 65536);
        budget.LastAt = now;
        if (budget.Tokens < packet.Length) return PacketPriority.None;
        budget.Tokens -= packet.Length;
        return PacketPriority.Voice;
    }

    private bool ObserveAudioRtp(StreamKey key, ReadOnlySpan<byte> payload, double now)
    {
        if (!TryReadAudioRtp(payload, out var pt, out var sequence, out var timestamp, out var ssrc))
        {
            _rtp.Remove(key);
            return false;
        }
        if (!_rtp.TryGetValue(key, out var state))
        {
            if (_rtp.Count >= 4096) return false;
            _rtp.Add(key, state = new RtpState());
        }
        var sequenceDelta = (ushort)(sequence - state.Sequence);
        var timestampDelta = unchecked(timestamp - state.Timestamp);
        var gap = now - state.LastAt;
        var continues = state.Count > 0 && state.Ssrc == ssrc && state.PayloadType == pt && sequenceDelta is >= 1 and <= 3 &&
            timestampDelta >= 80 * sequenceDelta && timestampDelta <= 960 * sequenceDelta && gap >= .005 && gap <= .12 * sequenceDelta;
        state.Count = continues ? Math.Min(4, state.Count + 1) : 1;
        state.PayloadType = pt; state.Sequence = sequence; state.Timestamp = timestamp; state.Ssrc = ssrc; state.LastAt = now;
        return state.Count >= 4;
    }

    private static bool TryReadAudioRtp(ReadOnlySpan<byte> payload, out byte pt, out ushort sequence, out uint timestamp, out uint ssrc)
    {
        pt = 0; sequence = 0; timestamp = 0; ssrc = 0;
        if (payload.Length < 13 || payload[0] >> 6 != 2) return false;
        pt = (byte)(payload[1] & 127);
        // RFC3551 fixed 8-kHz audio mappings. Dynamic PTs (including Opus)
        // require SDP/negotiation that this read-only classifier does not have.
        if (pt is not (0 or 8 or 18)) return false;
        var header = 12 + (payload[0] & 15) * 4;
        if (header >= payload.Length) return false;
        if ((payload[0] & 16) != 0)
        {
            if (header + 4 > payload.Length) return false;
            header += 4 + BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(header + 2, 2)) * 4;
            if (header >= payload.Length) return false;
        }
        if ((payload[0] & 32) != 0 && (payload[^1] == 0 || payload[^1] >= payload.Length - header)) return false;
        sequence = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2));
        timestamp = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4));
        ssrc = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(8));
        return true;
    }

    private readonly record struct OwnerKey(int Pid, long Started, PacketDirection Direction);
    private readonly record struct StreamKey(SocketFlow Flow, OwnerKey Owner);
    private sealed class RtpState { internal byte PayloadType; internal ushort Sequence; internal uint Timestamp, Ssrc; internal int Count; internal double LastAt; }
    private sealed class VoiceBudget { internal double LastAt, Tokens; }
}

/// <summary>RFC2474 DS field, RFC3168 ECN preservation, RFC1071 IPv4 header checksum.</summary>
public static class QosPacketMarker
{
    /// <summary>
    /// Only voice packets are ever marked. Game packets are prioritized (they skip every queue) but are
    /// never modified: they leave this PC byte-for-byte as the game sent them. There is no option to change that.
    /// </summary>
    public static bool TryMark(CapturedPacket packet, PacketPriority priority)
    {
        if (priority != PacketPriority.Voice) return false;
        if (packet.Direction != PacketDirection.Outbound ||
            packet.IpOffset < 0 || packet.IpOffset >= packet.Length) return false;
        var ip = packet.Buffer.AsSpan(packet.IpOffset, packet.Length - packet.IpOffset);
        const int dscp = 46; // Expedited Forwarding for voice.
        if (ip[0] >> 4 == 4)
        {
            if (ip.Length < 20) return false;
            var headerLength = (ip[0] & 15) * 4;
            var total = BinaryPrimitives.ReadUInt16BigEndian(ip.Slice(2));
            if (headerLength < 20 || headerLength > ip.Length || total < headerLength || total > ip.Length) return false;
            ip[1] = (byte)((dscp << 2) | (ip[1] & 3));
            ip[10] = 0; ip[11] = 0;
            uint sum = 0;
            for (var i = 0; i < headerLength; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(ip.Slice(i, 2));
            while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
            BinaryPrimitives.WriteUInt16BigEndian(ip.Slice(10), (ushort)~sum);
            return true;
        }
        if (ip[0] >> 4 == 6 && ip.Length >= 40)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(ip.Slice(4));
            if (length == 0 || length + 40 > ip.Length) return false;
            var trafficClass = ((ip[0] & 15) << 4) | (ip[1] >> 4);
            trafficClass = (dscp << 2) | (trafficClass & 3);
            ip[0] = (byte)((ip[0] & 0xf0) | (trafficClass >> 4));
            ip[1] = (byte)((ip[1] & 15) | ((trafficClass & 15) << 4));
            return true;
        }
        return false;
    }
}
