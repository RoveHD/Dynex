using System.Net;
using System.Net.Sockets;

namespace Dynex.Shaper;

public sealed record ProgramRate(string Name, double DownMbps, double UpMbps);

/// <summary>
/// Read-only per-program byte counters for "who is using the line right now" and the address of the
/// server the focused game talks to. Counting never changes, delays or holds any packet.
/// </summary>
public sealed class TrafficAccounting(Func<double>? clock = null)
{
    private readonly Func<double> _clock = clock ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
    private readonly object _gate = new();
    private Dictionary<string, (long Down, long Up)> _current = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<IPAddress, long> _servers = new();
    private double _bucketStart = double.NaN;
    private ProgramRate[] _top = [];
    private IPAddress? _gameServer;

    public void Observe(string program, bool inbound, int bytes, bool game, IPAddress remote)
    {
        var now = _clock();
        lock (_gate)
        {
            if (double.IsNaN(_bucketStart)) _bucketStart = now;
            if (now - _bucketStart >= 2) Rotate(now);
            if (_current.Count < 512 || _current.ContainsKey(program))
            {
                var value = _current.GetValueOrDefault(program);
                _current[program] = inbound ? (value.Down + bytes, value.Up) : (value.Down, value.Up + bytes);
            }
            if (game && IsPublic(remote) && (_servers.Count < 64 || _servers.ContainsKey(remote)))
                _servers[remote] = _servers.GetValueOrDefault(remote) + 1;
        }
    }

    public (ProgramRate[] Top, IPAddress? GameServer) Snapshot()
    {
        lock (_gate)
        {
            // A quiet period still ages out the previous bucket.
            if (!double.IsNaN(_bucketStart) && _clock() - _bucketStart >= 4) Rotate(_clock());
            return (_top, _gameServer);
        }
    }

    private void Rotate(double now)
    {
        var seconds = Math.Max(0.5, now - _bucketStart);
        _top = _current.Select(p => new ProgramRate(p.Key, Math.Round(p.Value.Down * 8 / seconds / 1e6, 2), Math.Round(p.Value.Up * 8 / seconds / 1e6, 2)))
            .Where(p => p.DownMbps + p.UpMbps >= 0.05).OrderByDescending(p => p.DownMbps + p.UpMbps).Take(5).ToArray();
        _gameServer = _servers.Count == 0 ? null : _servers.MaxBy(p => p.Value).Key;
        _current = new(StringComparer.OrdinalIgnoreCase);
        _servers = new();
        _bucketStart = now;
    }

    private static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 172 && b[1] is >= 16 and <= 31 ||
                 b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254 || b[0] == 100 && b[1] is >= 64 and <= 127);
    }
}
