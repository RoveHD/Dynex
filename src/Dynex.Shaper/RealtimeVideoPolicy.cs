using System.Buffers.Binary;

namespace Dynex.Shaper;

/// <summary>
/// Native-client media evidence, never a whole-application exemption. Teams
/// publishes separate local media port ranges. Discord does not publish fixed
/// video payload types, so only coherent, multi-packet video frame sequences
/// establish a claim; a dynamic RTP payload number alone is not evidence.
/// </summary>
internal sealed class RealtimeVideoPolicy
{
    private const int MaximumStreams = 1024;
    private readonly Dictionary<StreamKey, FrameState> _streams = new();
    private double _lastCleanup;

    // https://learn.microsoft.com/en-us/microsoftteams/qos-in-teams-clients
    // These are Microsoft's default LOCAL ports, not remote-server ports.
    internal PacketPriority Classify(SocketFlow flow, TrafficOwner owner, PacketDirection direction,
        ReadOnlySpan<byte> payload, double now, out bool excludeVoice)
    {
        excludeVoice = false;
        if (owner.ProcessStartedAtUtc is not { } started) return PacketPriority.None;
        var teams = owner.ProcessName is { } name &&
            (name.Equals("ms-teams", StringComparison.OrdinalIgnoreCase) || name.Equals("Teams", StringComparison.OrdinalIgnoreCase));
        if (teams && flow.LocalPort is >= 50020 and <= 50059)
        {
            excludeVoice = true;
            // Microsoft's app/source-port QoS policy also covers native TCP
            // fallback. UDP must actually contain RTP; ICE, DTLS and RTCP are
            // control traffic and must not inflate the video counter.
            return flow.Protocol == TransportProtocol.Tcp ||
                flow.Protocol == TransportProtocol.Udp && TryReadRtp(payload, out _)
                ? PacketPriority.Video : PacketPriority.None;
        }
        if (flow.Protocol != TransportProtocol.Udp || !IsDiscord(owner.ProcessName)) return PacketPriority.None;
        if (!TryReadRtp(payload, out var rtp))
        {
            excludeVoice = IsControl(payload) || payload.Length > 0 && payload[0] >> 6 == 2;
            return PacketPriority.None;
        }
        // Discord documents Opus as PT 0x78. Its RTP headers stay visible even
        // with transport encryption and DAVE; encrypted payload is never read.
        // https://docs.discord.com/developers/topics/voice-connections
        if (rtp.PayloadType == 120) return PacketPriority.None;
        excludeVoice = true;
        if (rtp.PayloadType < 96) return PacketPriority.None;
        if (now - _lastCleanup >= 1 || now < _lastCleanup)
        {
            foreach (var key in _streams.Where(p => now < p.Value.LastAt || now - p.Value.LastAt > 3)
                         .Select(p => p.Key).ToArray()) _streams.Remove(key);
            _lastCleanup = now;
        }
        var identity = new StreamKey(flow, owner.ProcessId, started.UtcTicks, direction, rtp.Ssrc, rtp.PayloadType);
        if (!_streams.TryGetValue(identity, out var state))
        {
            if (_streams.Count >= MaximumStreams) return PacketPriority.None;
            _streams.Add(identity, state = new FrameState());
        }
        return state.Observe(rtp, now) ? PacketPriority.Video : PacketPriority.None;
    }

    private static bool IsDiscord(string? name) => name is not null &&
        (name.Equals("Discord", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("DiscordCanary", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("DiscordPTB", StringComparison.OrdinalIgnoreCase));

    private static bool IsControl(ReadOnlySpan<byte> payload) =>
        payload.Length >= 8 && payload[0] < 4 && BinaryPrimitives.ReadUInt32BigEndian(payload[4..]) == 0x2112a442 ||
        payload.Length >= 2 && payload[0] >> 6 == 2 && payload[1] is >= 192 and <= 223 ||
        payload.Length >= 13 && payload[0] is >= 20 and <= 63 && payload[1] == 254;

    private static bool TryReadRtp(ReadOnlySpan<byte> data, out RtpHeader header)
    {
        header = default;
        if (data.Length < 13 || data[0] >> 6 != 2 || data[1] is >= 192 and <= 223) return false;
        var length = 12 + (data[0] & 15) * 4;
        if (length >= data.Length) return false;
        if ((data[0] & 16) != 0)
        {
            if (data.Length - length < 4) return false;
            length += 4 + BinaryPrimitives.ReadUInt16BigEndian(data[(length + 2)..]) * 4;
            if (length >= data.Length) return false;
        }
        // SRTP padding and header-extension data may be encrypted. Do not read
        // a padding count or attempt to interpret a codec inside that data.
        header = new((byte)(data[1] & 127), (data[1] & 128) != 0,
            BinaryPrimitives.ReadUInt16BigEndian(data[2..]), BinaryPrimitives.ReadUInt32BigEndian(data[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(data[8..]));
        return true;
    }

    private readonly record struct StreamKey(SocketFlow Flow, int Pid, long Started, PacketDirection Direction, uint Ssrc, byte PayloadType);
    private readonly record struct RtpHeader(byte PayloadType, bool Marker, ushort Sequence, uint Timestamp, uint Ssrc);

    private sealed class FrameState
    {
        internal double LastAt;
        private double _firstAt;
        private ushort _sequence;
        private uint _timestamp;
        private int _packetsInFrame, _frames, _segmentedFrames;
        private bool _ended, _confirmed;

        internal bool Observe(RtpHeader header, double now)
        {
            var delta = (ushort)(header.Sequence - _sequence);
            var timestampDelta = unchecked(header.Timestamp - _timestamp);
            if (_packetsInFrame == 0 || now < LastAt || now - LastAt > 3)
                Start(header, now);
            else if (delta == 0 || delta > 32768)
                return false; // duplicate/reordered packets establish no evidence
            else if (delta > 64 || timestampDelta > 180000 ||
                     timestampDelta == 0 && _ended || timestampDelta != 0 && (!_ended || timestampDelta < 450))
                Start(header, now);
            else
            {
                if (timestampDelta != 0) { _packetsInFrame = 0; _ended = false; }
                _packetsInFrame = Math.Min(2, _packetsInFrame + 1);
                _sequence = header.Sequence; _timestamp = header.Timestamp; LastAt = now;
                if (header.Marker)
                {
                    _ended = true;
                    _frames = Math.Min(3, _frames + 1);
                    if (_packetsInFrame >= 2) _segmentedFrames = Math.Min(2, _segmentedFrames + 1);
                    _confirmed |= _frames >= 3 && _segmentedFrames >= 2 && now - _firstAt >= .03;
                }
            }
            return _confirmed;
        }

        private void Start(RtpHeader header, double now)
        {
            _sequence = header.Sequence; _timestamp = header.Timestamp; LastAt = _firstAt = now;
            _packetsInFrame = 1; _ended = header.Marker; _frames = header.Marker ? 1 : 0;
            _segmentedFrames = 0; _confirmed = false;
        }
    }
}
