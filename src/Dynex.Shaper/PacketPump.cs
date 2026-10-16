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
        internal double? Rate;
        internal TaskCompletionSource Changed = NewSignal();
        internal static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal long QueuedBytes;
        internal long MaxQueuedBytes;
        internal void Update(double? mbps)
        {
            if (Rate == mbps && MaxQueuedBytes != 0) return;
            Rate = mbps;
            Interlocked.Exchange(ref MaxQueuedBytes, (long)Math.Clamp((mbps ?? 0) * 125000 * .25, 65575, 2 * 1024 * 1024));
            Volatile.Write(ref Pacer, mbps.HasValue ? new PacketPacer(mbps.Value) : null);
            Interlocked.Exchange(ref Changed, NewSignal()).TrySetResult();
        }
    }

    private readonly IPacketDevice _device;
    private readonly Lane _download = new(), _upload = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<CapturedPacket, bool>? _shouldLimit;
    private readonly Func<CapturedPacket, bool>? _shouldLimitUpload;
    private readonly Func<CapturedPacket, PacketPriority>? _priority;
    private readonly Action<CapturedPacket>? _observer;
    private readonly Func<CapturedPacket, ApplicationLimit?>? _applicationRule;
    private readonly object _applicationGate = new();
    private readonly Dictionary<string, (Lane Down, Lane Up)> _applicationLanes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _applicationSenders = [];
    private readonly Task _receiver;
    private readonly Task _sender;
    private readonly Task _uploadSender;
    private long _receivedBytes;
    private long _limitedDownloadBytes;
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
        Func<CapturedPacket, PacketPriority>? priority = null, double? uploadMbps = null, Func<CapturedPacket, bool>? shouldLimitUpload = null,
        Action<CapturedPacket>? observer = null, Func<CapturedPacket, ApplicationLimit?>? applicationRule = null,
        IReadOnlyList<ApplicationLimit>? applicationRules = null)
    {
        _download.Update(mbps);
        _upload.Update(uploadMbps);
        _device = device;
        _shouldLimit = shouldLimit;
        _shouldLimitUpload = shouldLimitUpload;
        _priority = priority;
        _observer = observer;
        _applicationRule = applicationRule;
        UpdateApplicationRules(applicationRules ?? []);
        _receiver = Task.Factory.StartNew(ReceiveLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _sender = Task.Run(() => SendLoopAsync(_download, false));
        _uploadSender = Task.Run(() => SendLoopAsync(_upload, true));
    }

    public bool Active => Volatile.Read(ref _closed) == 0;
    public bool HasDownloadLimit => Volatile.Read(ref _download.Pacer) is not null;
    public bool HasUploadLimit => Volatile.Read(ref _upload.Pacer) is not null;
    public bool HasApplicationRules { get { lock (_applicationGate) return _applicationLanes.Count != 0; } }
    public bool QosEnabled => _priority is not null;
    public long GamePriorityPackets => Interlocked.Read(ref _gamePriorityPackets);
    public long VoicePriorityPackets => Interlocked.Read(ref _voicePriorityPackets);
    public Exception? Failure => Volatile.Read(ref _failure);
    public string? CleanupError => Volatile.Read(ref _cleanupError);
    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);
    /// <summary>Shared-budget-eligible IP bytes at ingress, before pacing or queue drops.</summary>
    public long LimitedDownloadBytes => Interlocked.Read(ref _limitedDownloadBytes);
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

    public void UpdateApplicationRules(IReadOnlyList<ApplicationLimit> rules)
    {
        lock (_applicationGate)
        {
            if (Volatile.Read(ref _closed) != 0) return;
            var names = rules.Select(r => r.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in _applicationLanes.Keys.Where(n => !names.Contains(n)).ToArray())
            {
                var old = _applicationLanes[name];
                foreach (var lane in new[] { old.Down, old.Up })
                { lane.Update(null); lane.Queue.Writer.TryComplete(); }
                _applicationLanes.Remove(name);
            }
            _applicationSenders.RemoveAll(t => t.IsCompleted);
            foreach (var rule in rules)
            {
                if (!_applicationLanes.TryGetValue(rule.ProcessName, out var lanes))
                {
                    lanes = (new Lane(), new Lane());
                    _applicationLanes.Add(rule.ProcessName, lanes);
                    _applicationSenders.Add(Task.Run(() => SendLoopAsync(lanes.Down, false, true)));
                    _applicationSenders.Add(Task.Run(() => SendLoopAsync(lanes.Up, true, true)));
                }
                lanes.Down.Update(rule.DownloadMbps); lanes.Up.Update(rule.UploadMbps);
            }
        }
    }

    private void ReceiveLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var packet = _device.Receive();
                Interlocked.Add(ref _receivedBytes, packet.Length);
                // Observation must never consume a packet, alter its priority,
                // or break delivery when ownership/telemetry is unavailable.
                try { _observer?.Invoke(packet); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
                PacketPriority priority;
                try { priority = _priority?.Invoke(packet) ?? PacketPriority.None; }
                catch { packet.Dispose(); throw; }
                bool bulkDownload;
                try
                {
                    // Observe demand even when no download cap currently exists. Socket
                    // ownership and the bulk policy exclude games, voice and unknown flows.
                    bulkDownload = packet.Direction == PacketDirection.Inbound && priority == PacketPriority.None &&
                        _shouldLimit?.Invoke(packet) == true;
                }
                catch { packet.Dispose(); throw; }
                // Interactive traffic (games, voice) never enters a queue. Downloads are
                // paced only when a limit exists; uploads only through an explicit policy,
                // so TCP acknowledgements and unknown flows always leave immediately.
                Lane? lane = null;
                var applicationDownload = false;
                // Explicit rules override automatic game/voice exemptions.
                ApplicationLimit? application;
                try { application = _applicationRule?.Invoke(packet); }
                catch { packet.Dispose(); throw; }
                if (application is not null)
                {
                    lock (_applicationGate)
                        if (_applicationLanes.TryGetValue(application.ProcessName, out var lanes))
                        {
                            var candidate = packet.Direction == PacketDirection.Inbound ? lanes.Down : lanes.Up;
                            if (Volatile.Read(ref candidate.Pacer) is not null)
                            {
                                lane = candidate;
                                applicationDownload = packet.Direction == PacketDirection.Inbound;
                            }
                        }
                }
                if (lane is null && priority == PacketPriority.None)
                {
                    if (packet.Direction == PacketDirection.Inbound)
                    {
                        if (Volatile.Read(ref _download.Pacer) is not null && (_shouldLimit is null || bulkDownload)) lane = _download;
                    }
                    else if (_shouldLimitUpload is not null && Volatile.Read(ref _upload.Pacer) is not null && _shouldLimitUpload(packet)) lane = _upload;
                }
                // Explicit download rules also consume shared capacity, including
                // games or otherwise unclassified flows. Count each ingress packet once.
                if (bulkDownload || applicationDownload)
                    Interlocked.Add(ref _limitedDownloadBytes, packet.Length - packet.IpOffset);
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
        finally
        {
            _download.Queue.Writer.TryComplete(); _upload.Queue.Writer.TryComplete();
            lock (_applicationGate)
                foreach (var pair in _applicationLanes.Values)
                { pair.Down.Queue.Writer.TryComplete(); pair.Up.Queue.Writer.TryComplete(); }
        }
    }

    private async Task SendLoopAsync(Lane lane, bool upload, bool application = false)
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
                    // Rate changes/removal wake pending packets; over-age queues are bounded.
                    var pacer = Volatile.Read(ref lane.Pacer);
                    // Do not allow a saturated sender to create seconds of latency.
                    if (pacer is not null && Stopwatch.GetElapsedTime(packet.CapturedAt).TotalSeconds > .3)
                    {
                        Interlocked.Increment(ref _droppedPackets);
                        continue;
                    }
                    static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                    async Task WaitBudgetAsync(Lane budget)
                    {
                        while (true)
                        {
                            var changed = Volatile.Read(ref budget.Changed).Task;
                            var current = Volatile.Read(ref budget.Pacer);
                            var deadline = Now();
                            if (current is not null) lock (current) deadline = current.Schedule(packet.Length, Now());
                            var remaining = deadline - Now();
                            if (remaining <= 0) return;
                            var delay = Task.Delay(TimeSpan.FromSeconds(Math.Max(.001, remaining)), _stop.Token);
                            if (await Task.WhenAny(delay, changed).ConfigureAwait(false) == delay)
                            { await delay.ConfigureAwait(false); return; }
                        }
                    }
                    // Reserve the app budget before shared global capacity. A slow app
                    // must never reserve seconds of global capacity ahead of unrelated apps.
                    await WaitBudgetAsync(lane).ConfigureAwait(false);
                    if (application && Volatile.Read(ref lane.Pacer) is not null)
                        await WaitBudgetAsync(upload ? _upload : _download).ConfigureAwait(false);
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
        Task[] applicationSenders;
        lock (_applicationGate) applicationSenders = _applicationSenders.ToArray();
        await Task.WhenAll(new[] { _receiver, _sender, _uploadSender }.Concat(applicationSenders)).ConfigureAwait(false);
        // Receive may have enqueued its final packet after a sender exited.
        foreach (var lane in new[] { _download, _upload })
            while (lane.Queue.Reader.TryRead(out var packet)) packet.Dispose();
        lock (_applicationGate)
            foreach (var pair in _applicationLanes.Values)
                foreach (var lane in new[] { pair.Down, pair.Up })
                    while (lane.Queue.Reader.TryRead(out var packet)) packet.Dispose();
        _stop.Dispose();
    }
}
