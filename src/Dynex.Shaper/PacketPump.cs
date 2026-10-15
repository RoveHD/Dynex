using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;

namespace Dynex.Shaper;

/// <summary>Source/sink boundary permits lifecycle and pacing tests without a driver.</summary>
public interface IPacketDevice : IDisposable
{
    CapturedPacket Receive();
    void Send(CapturedPacket packet);
    string? CleanupError => null;
}

public sealed class PacketReleaseException(string message, Exception? inner = null) : IOException(message, inner);
public enum PacketDirection { Inbound, Outbound }
public enum PacketPriority { None, Game, Voice }

public sealed class CapturedPacket : IDisposable
{
    private byte[]? _buffer;
    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(CapturedPacket));
    public int Length { get; }
    public long CapturedAt { get; } = Stopwatch.GetTimestamp();
    internal Native.Address Address;
    internal object? BackendMetadata { get; set; }
    public int IpOffset { get; set; }
    public PacketDirection Direction { get; init; }

    public CapturedPacket(byte[] rentedBuffer, int length)
    {
        _buffer = rentedBuffer;
        Length = length;
    }
    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
    }
}

public sealed class PacketPump : IAsyncDisposable
{
    /// <summary>One paced direction: its own bounded queue, pacer and sender, so a full upload backlog
    /// never delays downloads (or vice versa). Interactive traffic never enters either lane.</summary>
    private sealed class Lane
    {
        internal readonly Channel<CapturedPacket> Queue = Channel.CreateBounded<CapturedPacket>(new BoundedChannelOptions(256)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        internal PacketPacer? Pacer;
        internal long QueuedBytes;
        internal long MaxQueuedBytes;
        internal void Update(double? mbps)
        {
            Interlocked.Exchange(ref MaxQueuedBytes, (long)Math.Clamp((mbps ?? 0) * 125000 * .25, 65575, 2 * 1024 * 1024));
            Volatile.Write(ref Pacer, mbps.HasValue ? new PacketPacer(mbps.Value) : null);
        }
    }

    private readonly IPacketDevice _device;
    private readonly Lane _download = new(), _upload = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<CapturedPacket, bool>? _shouldLimit;
    private readonly Func<CapturedPacket, bool>? _shouldLimitUpload;
    private readonly Func<CapturedPacket, PacketPriority>? _priority;
    private readonly Task _receiver;
    private readonly Task _sender;
    private readonly Task _uploadSender;
    private long _receivedBytes;
    private long _sentBytes;
    private long _uploadSentBytes;
    private long _droppedPackets;
    private long _bypassedBytes;
    private long _gamePriorityPackets, _voicePriorityPackets;
    private Exception? _failure;
    private string? _cleanupError;
    private int _closed;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    public PacketPump(IPacketDevice device, double? mbps, Func<CapturedPacket, bool>? shouldLimit = null,
        Func<CapturedPacket, PacketPriority>? priority = null, double? uploadMbps = null, Func<CapturedPacket, bool>? shouldLimitUpload = null)
    {
        _download.Update(mbps);
        _upload.Update(uploadMbps);
        _device = device;
        _shouldLimit = shouldLimit;
        _shouldLimitUpload = shouldLimitUpload;
        _priority = priority;
        _receiver = Task.Factory.StartNew(ReceiveLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _sender = Task.Run(() => SendLoopAsync(_download, false));
        _uploadSender = Task.Run(() => SendLoopAsync(_upload, true));
    }

    public bool Active => Volatile.Read(ref _closed) == 0;
    public bool HasDownloadLimit => Volatile.Read(ref _download.Pacer) is not null;
    public bool HasUploadLimit => Volatile.Read(ref _upload.Pacer) is not null;
    public bool QosEnabled => _priority is not null;
    public long GamePriorityPackets => Interlocked.Read(ref _gamePriorityPackets);
    public long VoicePriorityPackets => Interlocked.Read(ref _voicePriorityPackets);
    public Exception? Failure => Volatile.Read(ref _failure);
    public string? CleanupError => Volatile.Read(ref _cleanupError);
    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);
    public long SentBytes => Interlocked.Read(ref _sentBytes);
    public long UploadSentBytes => Interlocked.Read(ref _uploadSentBytes);
    public long DroppedPackets => Interlocked.Read(ref _droppedPackets);
    public long BypassedBytes => Interlocked.Read(ref _bypassedBytes);

    /// <summary>
    /// Rate changes keep the capture alive: replacing the device would flush
    /// unrelated ICMP/DNS/game frames in the native bidirectional queue.
    /// Only the sender mutates a pacer; updates publish a fresh instance.
    /// </summary>
    public void UpdateLimit(double? mbps) => _download.Update(mbps);
    public void UpdateUploadLimit(double? mbps) => _upload.Update(mbps);

    private void ReceiveLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var packet = _device.Receive();
                Interlocked.Add(ref _receivedBytes, packet.Length);
                PacketPriority priority;
                try { priority = _priority?.Invoke(packet) ?? PacketPriority.None; }
                catch { packet.Dispose(); throw; }
                // Interactive traffic (games, voice) never enters a queue. Downloads are
                // paced only when a limit exists; uploads only through an explicit policy,
                // so TCP acknowledgements and unknown flows always leave immediately.
                Lane? lane = null;
                if (priority == PacketPriority.None)
                {
                    if (packet.Direction == PacketDirection.Inbound)
                    {
                        if (Volatile.Read(ref _download.Pacer) is not null && (_shouldLimit is null || _shouldLimit(packet))) lane = _download;
                    }
                    else if (_shouldLimitUpload is not null && Volatile.Read(ref _upload.Pacer) is not null && _shouldLimitUpload(packet)) lane = _upload;
                }
                if (lane is null)
                {
                    using (packet)
                    {
                        var marked = priority != PacketPriority.None && packet.Direction == PacketDirection.Outbound && QosPacketMarker.TryMark(packet, priority);
                        _device.Send(packet);
                        // Count only successful sends. Inbound priority means
                        // immediate local delivery; outbound also requires marking.
                        if (packet.Direction == PacketDirection.Inbound || marked)
                        {
                            if (priority == PacketPriority.Game) Interlocked.Increment(ref _gamePriorityPackets);
                            if (priority == PacketPriority.Voice) Interlocked.Increment(ref _voicePriorityPackets);
                        }
                    }
                    Interlocked.Add(ref _bypassedBytes, packet.Length);
                    continue;
                }
                var queued = Interlocked.Add(ref lane.QueuedBytes, packet.Length);
                if (queued > Interlocked.Read(ref lane.MaxQueuedBytes) || !lane.Queue.Writer.TryWrite(packet))
                {
                    Interlocked.Add(ref lane.QueuedBytes, -packet.Length);
                    Interlocked.Increment(ref _droppedPackets);
                    packet.Dispose();
                }
            }
        }
        catch (Exception e) when (_stop.IsCancellationRequested) { _ = e; }
        catch (Exception e) { Fail(e); }
        finally { _download.Queue.Writer.TryComplete(); _upload.Queue.Writer.TryComplete(); }
    }

    private async Task SendLoopAsync(Lane lane, bool upload)
    {
        try
        {
            await foreach (var packet in lane.Queue.Reader.ReadAllAsync(_stop.Token))
            {
                using (packet)
                {
                    // Classification belongs to the receiver thread. A newly
                    // protected game bypasses immediately on future arrivals;
                    // packets already queued retain their bounded pacing delay.
                    Interlocked.Add(ref lane.QueuedBytes, -packet.Length);
                    // A rate update applies from the next queued packet. The
                    // current packet keeps its bounded, already scheduled delay.
                    var pacer = Volatile.Read(ref lane.Pacer);
                    // Do not allow a saturated sender to create seconds of latency.
                    if (pacer is not null && Stopwatch.GetElapsedTime(packet.CapturedAt).TotalSeconds > .3)
                    {
                        Interlocked.Increment(ref _droppedPackets);
                        continue;
                    }
                    var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                    var deadline = pacer?.Schedule(packet.Length, now) ?? now;
                    while (true)
                    {
                        var remaining = deadline - Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                        if (remaining <= 0) break;
                        // Task.Delay truncates sub-millisecond durations to zero.
                        // Always yield instead of spinning at fast rates.
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(.001, remaining)), _stop.Token).ConfigureAwait(false);
                    }
                    _stop.Token.ThrowIfCancellationRequested();
                    _device.Send(packet);
                    Interlocked.Add(ref upload ? ref _uploadSentBytes : ref _sentBytes, packet.Length);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception e) { Fail(e); }
        finally
        {
            while (lane.Queue.Reader.TryRead(out var packet)) packet.Dispose();
        }
    }

    private void Fail(Exception error)
    {
        Interlocked.CompareExchange(ref _failure, error, null);
        Close();
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _stop.Cancel();
        // Must close immediately, independently of a blocked Receive or IPC writer.
        try { _device.Dispose(); }
        catch (Exception e) { RecordCleanupError(e.Message, e); }
        if (_device.CleanupError is { } error) RecordCleanupError(error);
    }

    private void RecordCleanupError(string error, Exception? cause = null)
    {
        Interlocked.CompareExchange(ref _cleanupError, error, null);
        Interlocked.CompareExchange(ref _failure,
            new PacketReleaseException("Freigabe der Downloadbegrenzung nicht bestätigt: " + error, cause), null);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Close();
        await Task.WhenAll(_receiver, _sender, _uploadSender).ConfigureAwait(false);
        // Receive may have enqueued its final packet after a sender exited.
        foreach (var lane in new[] { _download, _upload })
            while (lane.Queue.Reader.TryRead(out var packet)) packet.Dispose();
        _stop.Dispose();
    }
}
