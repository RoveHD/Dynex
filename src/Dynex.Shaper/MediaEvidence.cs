using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dynex.Shaper;

internal sealed record MediaEvidenceProcess(int ProcessId, long StartedAtUtcTicks, string ImagePath);
internal readonly record struct MediaEvidenceEndpoint(IPAddress Address, ushort Port);

/// <summary>Authenticated, immutable application hints; never an IP-wide exemption.</summary>
internal sealed class MediaEvidenceSnapshot
{
    internal const double MaximumAgeSeconds = 15;
    internal static readonly MediaEvidenceSnapshot Empty = new([], [], 0);
    private readonly HashSet<(int Pid, long Started)> _processes;
    private readonly HashSet<MediaEvidenceEndpoint> _endpoints;
    private readonly double _receivedAt;

    internal MediaEvidenceSnapshot(IEnumerable<MediaEvidenceProcess> processes, IEnumerable<MediaEvidenceEndpoint> endpoints, double receivedAt)
    {
        _processes = processes.Select(p => (p.ProcessId, p.StartedAtUtcTicks)).ToHashSet();
        _endpoints = endpoints.ToHashSet();
        _receivedAt = receivedAt;
    }

    internal bool Matches(TrafficOwner owner, SocketFlow flow, double now) =>
        now >= _receivedAt && now - _receivedAt < MaximumAgeSeconds &&
        flow.Protocol is TransportProtocol.Tcp or TransportProtocol.Udp &&
        string.Equals(owner.ProcessName, "ELFIX", StringComparison.OrdinalIgnoreCase) &&
        owner.ProcessStartedAtUtc is { } started && _processes.Contains((owner.ProcessId, started.UtcTicks)) &&
        _endpoints.Contains(new(MediaEvidenceProtocol.Normalize(flow.RemoteAddress), flow.RemotePort));
}

internal static class MediaEvidenceProtocol
{
    internal const int MaximumFrameBytes = 16 * 1024;
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    internal static string PipeName(string localApplicationData)
    {
        var normalized = localApplicationData.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        return "dynex-media-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
    }

    internal static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>Pure validation. The resolver supplies OS identities, never identities claimed by JSON.</summary>
    internal static MediaEvidenceSnapshot Parse(ReadOnlySpan<byte> frame, MediaEvidenceProcess client,
        Func<int, MediaEvidenceProcess?> resolve, double now)
    {
        if (frame.Length is 0 or > MaximumFrameBytes) throw new FormatException("Invalid media frame size.");
        // JsonDocument alone accepts replacement handling in some string operations; decode strictly first.
        var json = StrictUtf8.GetString(frame);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        RequireProperties(root, "version", "processIds", "connections");
        if (!root.GetProperty("version").TryGetInt32(out var version) || version != 1) throw new FormatException("Invalid media version.");
        var ids = root.GetProperty("processIds");
        var connections = root.GetProperty("connections");
        if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > 32 ||
            connections.ValueKind != JsonValueKind.Array || connections.GetArrayLength() > 64 ||
            (connections.GetArrayLength() != 0 && ids.GetArrayLength() == 0)) throw new FormatException("Invalid media arrays.");

        var currentClient = resolve(client.ProcessId);
        if (currentClient != client || !IsElfix(client)) throw new FormatException("Media sender identity changed.");
        var processes = new List<MediaEvidenceProcess>();
        var seenIds = new HashSet<int>();
        foreach (var element in ids.EnumerateArray())
        {
            if (!element.TryGetInt32(out var id) || id <= 0 || !seenIds.Add(id)) throw new FormatException("Invalid media PID.");
            var process = resolve(id);
            if (process is null || !IsElfix(process) || !string.Equals(process.ImagePath, client.ImagePath, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Media PID is not part of this ELFIX executable.");
            processes.Add(process);
        }
        var endpoints = new List<MediaEvidenceEndpoint>();
        foreach (var connection in connections.EnumerateArray())
        {
            RequireProperties(connection, "address", "port");
            if (connection.GetProperty("address").ValueKind != JsonValueKind.String ||
                !connection.GetProperty("port").TryGetInt32(out var port) || port is < 1 or > ushort.MaxValue)
                throw new FormatException("Invalid media endpoint.");
            var addressText = connection.GetProperty("address").GetString()!;
            // Reject URI brackets, scope IDs, whitespace and permissive legacy IPv4 forms (e.g. 127.1).
            if (addressText.Length is 0 or > 45 || addressText.Any(c => !(char.IsAsciiHexDigit(c) || c is ':' or '.')) ||
                !IPAddress.TryParse(addressText, out var address)) throw new FormatException("Media address must be an IP literal.");
            if (address.AddressFamily == AddressFamily.InterNetwork &&
                (addressText.Split('.').Length != 4 || addressText.Split('.').Any(p => p.Length == 0 || p.Any(c => !char.IsAsciiDigit(c)))))
                throw new FormatException("Invalid IPv4 literal.");
            endpoints.Add(new(Normalize(address), (ushort)port));
        }
        return new(processes, endpoints, now);
    }

    internal static bool IsElfix(MediaEvidenceProcess process) => process.ProcessId > 0 && process.StartedAtUtcTicks > 0 &&
        Path.IsPathFullyQualified(process.ImagePath) && string.Equals(Path.GetFileName(process.ImagePath), "ELFIX.exe", StringComparison.OrdinalIgnoreCase);

    private static void RequireProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException("Expected media object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new FormatException("Unknown or duplicate media field.");
        if (seen.Count != names.Length) throw new FormatException("Missing media field.");
    }
}

/// <summary>Bounded byte framing preserves strict UTF-8 and rejects unfinished records.</summary>
internal sealed class MediaEvidenceFrameReader(Stream stream)
{
    private readonly byte[] _buffer = new byte[1024];
    private int _offset, _count;

    internal async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        using var frame = new MemoryStream();
        while (true)
        {
            if (_offset == _count)
            {
                _count = await stream.ReadAsync(_buffer.AsMemory(), cancellationToken);
                _offset = 0;
                if (_count == 0)
                {
                    if (frame.Length != 0) throw new EndOfStreamException("Unfinished media frame.");
                    return null;
                }
            }
            var end = Array.IndexOf(_buffer, (byte)'\n', _offset, _count - _offset);
            var length = (end < 0 ? _count : end) - _offset;
            if (frame.Length + length > MediaEvidenceProtocol.MaximumFrameBytes) throw new FormatException("Media frame too large.");
            frame.Write(_buffer, _offset, length);
            _offset += length;
            if (end >= 0) { _offset++; return frame.ToArray(); }
        }
    }
}

internal sealed class MediaEvidenceRateLimit
{
    private readonly Queue<double> _frames = new();
    internal bool Accept(double now)
    {
        while (_frames.TryPeek(out var at) && now - at >= 1) _frames.Dequeue();
        if (_frames.Count >= 4) return false;
        _frames.Enqueue(now);
        return true;
    }
}
