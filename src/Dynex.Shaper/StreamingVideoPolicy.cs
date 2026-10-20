using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace Dynex.Shaper;

/// <summary>
/// Passive, per-process/per-connection media evidence. Never classifies a whole
/// browser, remote IP, or port. Unknown encrypted traffic remains best effort.
/// Only public handshake metadata is examined; media payload is not decrypted.
/// </summary>
internal sealed class StreamingVideoPolicy
{
    private const int MaximumFlows = 512;
    private const int MaximumHello = 32768;
    private readonly Dictionary<Identity, StreamState> _streams = new();
    private double _lastCleanup;

    internal bool Observe(SocketFlow flow, TrafficOwner? owner, PacketDirection direction,
        ReadOnlySpan<byte> ip, ReadOnlySpan<byte> payload, int payloadOffset, double now)
    {
        var owned = owner?.ProcessStartedAtUtc is not null;
        if (now - _lastCleanup > 5)
        {
            foreach (var expired in _streams.Where(p => now - p.Value.LastAt > (p.Key.Pid == 0 ? 2 : 120) || now - p.Value.CreatedAt > 1800)
                         .Select(p => p.Key).ToArray()) _streams.Remove(expired);
            _lastCleanup = now;
        }
        var key = new Identity(flow, owned ? owner!.ProcessId : 0, owned ? owner!.ProcessStartedAtUtc!.Value.UtcTicks : 0);
        _streams.TryGetValue(key, out var state);
        var tcpStart = flow.Protocol == TransportProtocol.Tcp ? TcpStart(ip) : -1;
        if (tcpStart >= 0 && ip.Length >= tcpStart + 20 && direction == PacketDirection.Outbound &&
            (ip[tcpStart + 13] & 2) != 0)
        {
            // A reused TCP tuple must prove its media identity again.
            foreach (var previous in _streams.Keys.Where(k => k.Flow == flow).ToArray()) _streams.Remove(previous);
            state = null;
        }
        if (owned && state is null && _streams.Remove(new(flow, 0, 0), out var pending) && now - pending.LastAt <= 2)
        {
            // The public ClientHello can precede the next OS socket-table
            // refresh. Join metadata only to this exact, freshly owned tuple.
            state = pending;
            _streams[key] = state;
        }
        if (flow.Protocol == TransportProtocol.Tcp && direction == PacketDirection.Inbound && payload.StartsWith("HTTP/1."u8))
        {
            var media = IsHttpMedia(payload);
            if (!media && state?.Http != true) return false;
            state ??= Add(key, now);
            if (state is null) return false;
            state.Http = true; state.Video = media; state.Rejected = false; state.LastAt = now;
            state.ReleaseBuffers();
            return owned && media;
        }
        if (state?.Http == true && direction == PacketDirection.Outbound &&
            (payload.StartsWith("GET "u8) || payload.StartsWith("POST "u8) || payload.StartsWith("HEAD "u8)))
            state.Video = false;
        var initial = flow.Protocol == TransportProtocol.Udp && direction == PacketDirection.Outbound && QuicClientHello.IsInitial(payload);
        if (initial && state is not null) state.Video = false;
        if (state is not null)
        {
            state.LastAt = now;
            if (state.Video && !initial) return owned;
            if (state.Rejected && !initial) return false;
        }
        if (flow.Protocol == TransportProtocol.Tcp && direction == PacketDirection.Inbound)
        {
            // MIME is useful for plain HTTP and local media servers, including
            // ELFIX. HTTPS headers are encrypted and never guessed from sizes.
            if (!IsHttpMedia(payload)) return false;
            state ??= Add(key, now);
            if (state is null) return false;
            state.Video = true;
            state.ReleaseBuffers();
            return owned;
        }
        if (direction != PacketDirection.Outbound || payload.Length == 0) return false;
        if (flow.Protocol == TransportProtocol.Tcp)
        {
            if (tcpStart < 0 || ip.Length < tcpStart + 20) return false;
            if (state is null && payload[0] != 22) return false;
            state ??= Add(key, now);
            if (state is null) return false;
            var sequence = BinaryPrimitives.ReadUInt32BigEndian(ip.Slice(tcpStart + 4, 4));
            if (!state.AppendTcp(sequence, payload)) return false;
            var result = MediaHandshake.ReadTlsRecords(state.TcpBytes.AsSpan(0, state.TcpLength), out var host);
            if (result != HelloResult.Incomplete)
            {
                state.Video = result == HelloResult.Complete && IsMediaHost(host);
                state.Rejected = !state.Video;
                state.ReleaseBuffers();
            }
        }
        else if (flow.Protocol == TransportProtocol.Udp)
        {
            if (!initial) return false;
            state ??= Add(key, now);
            if (state is null || ++state.QuicAttempts > 8) return false;
            if (!QuicClientHello.TryReadCrypto(payload, out var connectionId, out var crypto)) return false;
            if (state.QuicId is not null && !state.QuicId.AsSpan().SequenceEqual(connectionId))
            {
                // New connection on a reused UDP tuple; no inherited media claim.
                state.ReleaseBuffers();
                state.Video = state.Rejected = false;
            }
            state.QuicId = connectionId;
            foreach (var fragment in crypto)
                if (!state.AppendCrypto(fragment.Offset, fragment.Bytes)) return false;
            var result = MediaHandshake.ReadClientHello(state.CryptoBytes.AsSpan(0, state.CryptoLength), out var host);
            if (result != HelloResult.Incomplete)
            {
                state.Video = result == HelloResult.Complete && IsMediaHost(host);
                state.Rejected = !state.Video;
                state.ReleaseBuffers();
            }
        }
        return owned && state?.Video == true;
    }

    private StreamState? Add(Identity key, double now)
    {
        if (_streams.Count >= MaximumFlows) return null;
        var state = new StreamState { CreatedAt = now, LastAt = now };
        _streams.Add(key, state);
        return state;
    }

    internal static bool IsMediaHost(string? host)
    {
        if (host is null) return false;
        // Media-specific CDN suffixes only. General youtube.com/netflix.com,
        // shared cloud/CDN addresses and ELFIX's app name are not evidence.
        return new[] { "googlevideo.com", "nflxvideo.net" }.Any(suffix =>
            host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsHttpMedia(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 12 or > 16384 || !payload.StartsWith("HTTP/1."u8)) return false;
        var end = payload.IndexOf("\r\n\r\n"u8);
        if (end < 0 || end > 8192) return false;
        var header = Encoding.ASCII.GetString(payload[..end]);
        var lines = header.Split("\r\n", StringSplitOptions.None);
        if (lines[0].Length < 12 || lines[0][9] != '2') return false;
        string? mime = null;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) return false;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase) &&
                value.Split(';')[0].Trim().Equals("attachment", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                if (mime is not null) return false;
                mime = value.Split(';')[0].Trim();
            }
        }
        return mime is not null && (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
            mime.Equals("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase) ||
            mime.Equals("application/x-mpegurl", StringComparison.OrdinalIgnoreCase) ||
            mime.Equals("application/dash+xml", StringComparison.OrdinalIgnoreCase));
    }

    private static int TcpStart(ReadOnlySpan<byte> ip)
    {
        if (ip.Length < 20) return -1;
        if (ip[0] >> 4 == 4) return (ip[0] & 15) * 4;
        if (ip[0] >> 4 != 6 || ip.Length < 40) return -1;
        var next = ip[6];
        var offset = 40;
        for (var i = 0; i < 8 && next != 6; i++)
        {
            if (next is not (0 or 43 or 60) || ip.Length < offset + 2) return -1;
            next = ip[offset];
            offset += (ip[offset + 1] + 1) * 8;
        }
        return next == 6 && offset <= ip.Length ? offset : -1;
    }

    private readonly record struct Identity(SocketFlow Flow, int Pid, long Started);
    private sealed class StreamState
    {
        internal double CreatedAt, LastAt;
        internal bool Video, Rejected, Http;
        internal byte[] TcpBytes = [], CryptoBytes = [];
        private bool[] _cryptoPresent = [];
        internal byte[]? QuicId;
        internal int TcpLength, CryptoLength, QuicAttempts;
        private uint? _tcpStart;

        internal bool AppendTcp(uint sequence, ReadOnlySpan<byte> payload)
        {
            _tcpStart ??= sequence;
            var offset = unchecked((int)(sequence - _tcpStart.Value));
            // Retransmitted/overlapping bytes must agree. Missing/out-of-order
            // segments are conservatively left unclassified until retransmit.
            if (offset < 0 || offset > TcpLength || payload.Length > MaximumHello - offset) return false;
            if (TcpBytes.Length == 0) TcpBytes = new byte[MaximumHello];
            var overlap = Math.Min(payload.Length, TcpLength - offset);
            if (!payload[..overlap].SequenceEqual(TcpBytes.AsSpan(offset, overlap)))
            { Rejected = true; ReleaseBuffers(); return false; }
            payload.CopyTo(TcpBytes.AsSpan(offset));
            TcpLength = Math.Max(TcpLength, offset + payload.Length);
            return true;
        }

        internal bool AppendCrypto(int offset, byte[] bytes)
        {
            if (offset < 0 || bytes.Length > MaximumHello - offset) return false;
            if (CryptoBytes.Length == 0)
            { CryptoBytes = new byte[MaximumHello]; _cryptoPresent = new bool[MaximumHello]; }
            for (var i = 0; i < bytes.Length; i++)
            {
                if (_cryptoPresent[offset + i] && CryptoBytes[offset + i] != bytes[i])
                { Rejected = true; ReleaseBuffers(); return false; }
                CryptoBytes[offset + i] = bytes[i];
                _cryptoPresent[offset + i] = true;
            }
            while (CryptoLength < MaximumHello && _cryptoPresent[CryptoLength]) CryptoLength++;
            return true;
        }

        internal void ReleaseBuffers()
        {
            TcpBytes = []; CryptoBytes = []; _cryptoPresent = [];
            TcpLength = CryptoLength = 0; _tcpStart = null;
        }
    }
}

internal enum HelloResult { Incomplete, Invalid, Complete }

internal static class MediaHandshake
{
    internal static HelloResult ReadTlsRecords(ReadOnlySpan<byte> data, out string? host)
    {
        host = null;
        Span<byte> handshake = stackalloc byte[32768];
        var length = 0;
        var offset = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < 5) return HelloResult.Incomplete;
            if (data[offset] != 22 || data[offset + 1] != 3) return HelloResult.Invalid;
            var recordLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 3, 2));
            if (recordLength is 0 or > 18432 || recordLength > handshake.Length - length) return HelloResult.Invalid;
            if (data.Length - offset - 5 < recordLength) return HelloResult.Incomplete;
            data.Slice(offset + 5, recordLength).CopyTo(handshake[length..]);
            length += recordLength;
            var result = ReadClientHello(handshake[..length], out host);
            if (result != HelloResult.Incomplete) return result;
            offset += recordLength + 5;
        }
        return HelloResult.Incomplete;
    }

    internal static HelloResult ReadClientHello(ReadOnlySpan<byte> data, out string? host)
    {
        host = null;
        if (data.Length < 4) return HelloResult.Incomplete;
        if (data[0] != 1) return HelloResult.Invalid;
        var length = data[1] * 65536 + data[2] * 256 + data[3];
        if (length is < 40 or > 32764) return HelloResult.Invalid;
        if (data.Length - 4 < length) return HelloResult.Incomplete;
        var body = data.Slice(4, length);
        var p = 34; // legacy_version + random
        if (p >= body.Length || !Skip(body, ref p, 1)) return HelloResult.Invalid;
        if (p + 2 > body.Length || !Skip(body, ref p, 2)) return HelloResult.Invalid;
        if (p >= body.Length || !Skip(body, ref p, 1)) return HelloResult.Invalid;
        if (p + 2 > body.Length) return HelloResult.Invalid;
        var extensions = BinaryPrimitives.ReadUInt16BigEndian(body[p..]); p += 2;
        if (extensions != body.Length - p) return HelloResult.Invalid;
        var seen = new HashSet<ushort>();
        while (p < body.Length)
        {
            if (body.Length - p < 4) return HelloResult.Invalid;
            var type = BinaryPrimitives.ReadUInt16BigEndian(body[p..]);
            var size = BinaryPrimitives.ReadUInt16BigEndian(body[(p + 2)..]); p += 4;
            if (size > body.Length - p || !seen.Add(type)) return HelloResult.Invalid;
            var extension = body.Slice(p, size); p += size;
            // ECH can be GREASE even without encrypted inner metadata. Treat it
            // as opaque: only the separately visible, media-specific outer SNI
            // may establish a CDN classification; never infer the inner name.
            if (type != 0) continue;
            if (extension.Length < 5 || BinaryPrimitives.ReadUInt16BigEndian(extension) != extension.Length - 2 ||
                extension[2] != 0) return HelloResult.Invalid;
            var hostLength = BinaryPrimitives.ReadUInt16BigEndian(extension[3..]);
            if (hostLength is 0 or > 253 || hostLength != extension.Length - 5) return HelloResult.Invalid;
            var name = extension[5..];
            foreach (var b in name)
                if (!(b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or
                    >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)'-')) return HelloResult.Invalid;
            host = Encoding.ASCII.GetString(name).ToLowerInvariant();
            if (host.Split('.').Any(label => label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-'))
                return HelloResult.Invalid;
        }
        return HelloResult.Complete;
    }

    private static bool Skip(ReadOnlySpan<byte> data, ref int offset, int sizeBytes)
    {
        if (offset + sizeBytes > data.Length) return false;
        var length = sizeBytes == 1 ? data[offset] : BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += sizeBytes;
        if (length > data.Length - offset) return false;
        offset += length;
        return true;
    }
}
