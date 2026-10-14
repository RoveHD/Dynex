namespace Dynex.Shaper;

/// <summary>Monotonic, byte-based pacing. One sender owns this scheduler.</summary>
public sealed class PacketPacer
{
    private readonly double _bytesPerSecond;
    private readonly double _burstSeconds;
    private double _next;

    public PacketPacer(double mbps)
    {
        ValidateRate(mbps);
        _bytesPerSecond = mbps * 1_000_000 / 8;
        // At most 10 ms of credit, capped at 64 KiB. An idle connection cannot
        // accumulate an arbitrarily large download burst.
        _burstSeconds = Math.Min(.010, 65536 / _bytesPerSecond);
        _next = double.NegativeInfinity;
    }

    public static void ValidateRate(double mbps)
    {
        if (!double.IsFinite(mbps) || mbps < .1 || mbps > 100_000)
            throw new ArgumentOutOfRangeException(nameof(mbps), "Downloadlimit muss zwischen 0,1 und 100000 Mbit/s liegen.");
    }

    /// <returns>Absolute monotonic deadline, in seconds.</returns>
    public double Schedule(int packetBytes, double nowSeconds)
    {
        if (packetBytes <= 0 || packetBytes > 65575)
            throw new ArgumentOutOfRangeException(nameof(packetBytes));
        if (!double.IsFinite(nowSeconds)) throw new ArgumentOutOfRangeException(nameof(nowSeconds));
        _next = Math.Max(_next, nowSeconds - _burstSeconds) + packetBytes / _bytesPerSecond;
        return Math.Max(nowSeconds, _next);
    }
}
