namespace Dynex.Mobile;

/// <summary>
/// Compatibility boundary for older callers. Bandwidth limiting has been removed;
/// even a legacy positive rate never installs a bucket, queue or delay.
/// </summary>
public sealed class DownloadRateLimiter : IDisposable
{
    private readonly int maximumBurstBytes;
    private bool disposed;

    public DownloadRateLimiter(int maximumBurstBytes = 65_536, int maximumWaiters = 1_024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBurstBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWaiters);
        this.maximumBurstBytes = maximumBurstBytes;
    }

    public long? BytesPerSecond => null;

    public void SetLimit(long? bytesPerSecond)
    {
        if (bytesPerSecond is <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    public ValueTask WaitAsync(int byteCount, CancellationToken cancellationToken = default)
    {
        if (byteCount < 0 || byteCount > maximumBurstBytes) throw new ArgumentOutOfRangeException(nameof(byteCount));
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        return ValueTask.CompletedTask;
    }

    public void Dispose() => disposed = true;
}
