using System.Diagnostics;

namespace Dynex.Shaper;

/// <summary>Fresh ingress-counter deltas; missing capture is unknown, never measured idle.</summary>
public sealed class DownloadActivityMeter(Func<double>? clock = null)
{
    private readonly Func<double> _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    private long? _previous;
    private double _at;

    public double? Sample(long? bytes)
    {
        var now = _clock();
        var previous = _previous;
        var seconds = now - _at;
        _previous = bytes is >= 0 ? bytes : null;
        _at = now;
        if (bytes is not { } current || current < 0 || previous is not { } prior || current < prior ||
            seconds <= 0 || seconds > 6) return null;
        var rate = (current - prior) * 8d / seconds / 1_000_000;
        return double.IsFinite(rate) && rate is >= 0 and <= 100000 ? rate : null;
    }
}
