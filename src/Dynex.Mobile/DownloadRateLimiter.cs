using System.Diagnostics;

namespace Dynex.Mobile;

/// <summary>A shared, FIFO token bucket. A null limit wakes all waiters; elapsed time is monotonic.</summary>
public sealed class DownloadRateLimiter : IDisposable
{
    private readonly object sync = new();
    private readonly LinkedList<object> queue = new();
    private readonly int maximumBurstBytes;
    private readonly int maximumWaiters;
    private TaskCompletionSource changed = NewSignal();
    private long lastTimestamp = Stopwatch.GetTimestamp();
    private long? limit;
    private double tokens;
    private bool disposed;

    public DownloadRateLimiter(int maximumBurstBytes = 65_536, int maximumWaiters = 1_024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBurstBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWaiters);
        this.maximumBurstBytes = maximumBurstBytes;
        this.maximumWaiters = maximumWaiters;
    }

    public long? BytesPerSecond { get { lock (sync) return limit; } }

    public void SetLimit(long? bytesPerSecond)
    {
        if (bytesPerSecond is <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Refill();
            if (limit == bytesPerSecond) return;
            // Do not carry credit from an unlimited period into a newly limited download.
            if (limit is null || bytesPerSecond is null) tokens = 0;
            limit = bytesPerSecond;
            Pulse();
        }
    }

    /// <summary>Reserve at most MaximumBurstBytes before forwarding that payload.</summary>
    public async ValueTask WaitAsync(int byteCount, CancellationToken cancellationToken = default)
    {
        if (byteCount < 0 || byteCount > maximumBurstBytes) throw new ArgumentOutOfRangeException(nameof(byteCount));
        cancellationToken.ThrowIfCancellationRequested();
        LinkedListNode<object> node;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (limit is null || byteCount == 0) return;
            if (queue.Count >= maximumWaiters) throw new InvalidOperationException("Download wait queue is full.");
            node = queue.AddLast(new object());
        }
        try
        {
            while (true)
            {
                Task signal;
                TimeSpan? delay = null;
                lock (sync)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    cancellationToken.ThrowIfCancellationRequested();
                    Refill();
                    if (limit is null || (queue.First == node && tokens >= byteCount))
                    {
                        if (limit is not null) tokens -= byteCount;
                        queue.Remove(node);
                        Pulse();
                        return;
                    }
                    signal = changed.Task;
                    if (queue.First == node)
                    {
                        var milliseconds = Math.Ceiling((byteCount - tokens) * 1_000d / limit.Value);
                        delay = TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 1, int.MaxValue - 1d));
                    }
                }
                if (delay is null)
                {
                    await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Cancel the losing timer, so rapid limit changes never accumulate pending timers.
                    using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var timer = Task.Delay(delay.Value, timerCancellation.Token);
                    await Task.WhenAny(signal, timer).ConfigureAwait(false);
                    await timerCancellation.CancelAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
        finally
        {
            lock (sync)
            {
                if (node.List is not null) { queue.Remove(node); Pulse(); }
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            Pulse();
        }
    }

    private void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        if (limit is not null)
            tokens = Math.Min(maximumBurstBytes, tokens + Stopwatch.GetElapsedTime(lastTimestamp, now).TotalSeconds * limit.Value);
        lastTimestamp = now;
    }

    private void Pulse()
    {
        var previous = changed;
        changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
