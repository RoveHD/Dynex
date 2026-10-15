namespace Dynex.Shaper;

public sealed record ProgramRate(string Name, double DownMbps, double UpMbps);

/// <summary>
/// Read-only per-program byte counters for "who is using the line right now".
/// Counting never changes, delays or holds any packet.
/// </summary>
public sealed class TrafficAccounting(Func<double>? clock = null)
{
    private readonly Func<double> _clock = clock ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
    private readonly object _gate = new();
    private Dictionary<string, (long Down, long Up)> _current = new(StringComparer.OrdinalIgnoreCase);
    private double _bucketStart = double.NaN;
    private ProgramRate[] _top = [];

    public void Observe(string program, bool inbound, int bytes)
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
        }
    }

    public ProgramRate[] Snapshot()
    {
        lock (_gate)
        {
            // A quiet period still ages out the previous bucket.
            if (!double.IsNaN(_bucketStart) && _clock() - _bucketStart >= 4) Rotate(_clock());
            return _top;
        }
    }

    private void Rotate(double now)
    {
        var seconds = Math.Max(0.5, now - _bucketStart);
        _top = _current.Select(p => new ProgramRate(p.Key, Math.Round(p.Value.Down * 8 / seconds / 1e6, 2), Math.Round(p.Value.Up * 8 / seconds / 1e6, 2)))
            .Where(p => p.DownMbps + p.UpMbps >= 0.05).OrderByDescending(p => p.DownMbps + p.UpMbps).Take(5).ToArray();
        _current = new(StringComparer.OrdinalIgnoreCase);
        _bucketStart = now;
    }

}
