using System.Buffers;
using System.Diagnostics;

namespace Dynex.Shaper;

/// <summary>Source/sink boundary permits lifecycle and priority tests without a driver.</summary>
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

/// <summary>
/// Observes and marks traffic, then forwards every packet immediately. There
/// are no shaping queues, pacing waits or bandwidth-related packet drops.
/// </summary>
public sealed class PacketPump : IAsyncDisposable
{
    private readonly IPacketDevice _device;
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<CapturedPacket, PacketPriority>? _priority;
    private readonly Action<CapturedPacket>? _observer;
    private readonly Task _receiver;
    private long _receivedBytes, _sentBytes, _uploadSentBytes, _bypassedBytes;
    private long _gamePriorityPackets, _voicePriorityPackets;
    private Exception? _failure;
    private string? _cleanupError;
    private int _closed;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    // Legacy arguments remain source-compatible for old callers, but cannot
    // enable a limiter. Even an obsolete application rule cannot delay traffic.
    public PacketPump(IPacketDevice device, double? mbps, Func<CapturedPacket, bool>? shouldLimit = null,
        Func<CapturedPacket, PacketPriority>? priority = null, double? uploadMbps = null, Func<CapturedPacket, bool>? shouldLimitUpload = null,
        Action<CapturedPacket>? observer = null, Func<CapturedPacket, ApplicationLimit?>? applicationRule = null,
        IReadOnlyList<ApplicationLimit>? applicationRules = null)
    {
        _device = device;
        _priority = priority;
        _observer = observer;
        _receiver = Task.Factory.StartNew(ReceiveLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public bool Active => Volatile.Read(ref _closed) == 0;
    public bool HasDownloadLimit => false;
    public bool HasUploadLimit => false;
    public bool HasApplicationRules => false;
    public bool QosEnabled => _priority is not null;
    public long GamePriorityPackets => Interlocked.Read(ref _gamePriorityPackets);
    public long VoicePriorityPackets => Interlocked.Read(ref _voicePriorityPackets);
    public Exception? Failure => Volatile.Read(ref _failure);
    public string? CleanupError => Volatile.Read(ref _cleanupError);
    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);
    public long LimitedDownloadBytes => 0;
    public long SentBytes => Interlocked.Read(ref _sentBytes);
    public long UploadSentBytes => Interlocked.Read(ref _uploadSentBytes);
    public long DroppedPackets => 0;
    public long BypassedBytes => Interlocked.Read(ref _bypassedBytes);

    // Old commands are deliberately inert, including while capture is running.
    public void UpdateLimit(double? mbps) { }
    public void UpdateUploadLimit(double? mbps) { }
    public void UpdateApplicationRules(IReadOnlyList<ApplicationLimit> rules) { }

    private void ReceiveLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var packet = _device.Receive();
                if (_stop.IsCancellationRequested) break;
                Interlocked.Add(ref _receivedBytes, packet.Length);
                // Measurement must never block packet delivery if a counter or
                // an ownership snapshot is unavailable.
                try { _observer?.Invoke(packet); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
                var priority = _priority?.Invoke(packet) ?? PacketPriority.None;
                var marked = priority != PacketPriority.None && packet.Direction == PacketDirection.Outbound &&
                    QosPacketMarker.TryMark(packet, priority);
                _device.Send(packet);
                Interlocked.Add(ref _sentBytes, packet.Length);
                if (packet.Direction == PacketDirection.Outbound) Interlocked.Add(ref _uploadSentBytes, packet.Length);
                Interlocked.Add(ref _bypassedBytes, packet.Length);
                if (packet.Direction == PacketDirection.Inbound || marked)
                {
                    if (priority == PacketPriority.Game) Interlocked.Increment(ref _gamePriorityPackets);
                    if (priority == PacketPriority.Voice) Interlocked.Increment(ref _voicePriorityPackets);
                }
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref _failure, error, null);
            Close();
        }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _stop.Cancel();
        // Native release must work independently of blocked Receive or IPC.
        try { _device.Dispose(); }
        catch (Exception error) { RecordCleanupError(error.Message, error); }
        if (_device.CleanupError is { } cleanup) RecordCleanupError(cleanup);
    }

    private void RecordCleanupError(string error, Exception? cause = null)
    {
        Interlocked.CompareExchange(ref _cleanupError, error, null);
        Interlocked.CompareExchange(ref _failure,
            new PacketReleaseException("Freigabe der Paketpriorisierung nicht bestätigt: " + error, cause), null);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Close();
        await _receiver.ConfigureAwait(false);
        _stop.Dispose();
    }
}
