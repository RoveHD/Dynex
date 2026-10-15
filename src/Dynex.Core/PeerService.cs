using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Dynex.Core;

/// <summary>Authenticated LAN discovery. Snapshots expire on receipt age, independently of network activity.</summary>
public sealed class PeerService : IPeerService
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.39.74");
    private readonly DynexSettings _settings;
    private readonly PacketCodec _codec;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Dictionary<Guid, ReceivedPeer> _peers = new();
    private readonly Dictionary<(Guid Device, Guid Session), ReplayEntry> _replay = new();
    private readonly Guid _session = Guid.NewGuid();
    private long _sequence;
    private long _sampleSequence;
    private int _networkChanged;
    private Socket? _socket;
    private CancellationTokenSource? _stop;
    private Task[] _tasks = Array.Empty<Task>();
    private IPAddress[] _interfaces = Array.Empty<IPAddress>();
    private LocalState _local = new(false, null, null, null, null, false, null, null, null, 0, null, 0);
    private string? _networkStatus;
    private long _sent, _received, _accepted, _rejected, _scopeRejected, _otherMode, _lastReceiveUnixMs, _clockSkew, _self;
    private volatile bool _disposed;

    public event Action? PeersChanged;
    public string? NetworkStatus => Volatile.Read(ref _networkStatus);

    public PeerService(DynexSettings settings)
    {
        settings.Validate(true);
        _settings = settings with { DirectPeers = settings.DirectPeers.Select(p => new IPEndPoint(p.Address, p.Port)).ToArray() };
        _codec = new PacketCodec(settings.GroupKeyBase64);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_socket is not null) return;
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.ReceiveBufferSize = 64 * 1024;
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, false);
                socket.Bind(new IPEndPoint(_settings.LoopbackOnly ? IPAddress.Loopback : IPAddress.Any, _settings.Port));
                Volatile.Write(ref _networkStatus, null);
                _interfaces = _settings.LoopbackOnly ? Array.Empty<IPAddress>() : GetLanInterfaces();
                foreach (var address in _interfaces)
                {
                    try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastAddress, address)); }
                    catch (SocketException e) { SetError("LAN-Discovery: " + e.Message); }
                }
                if (!_settings.LoopbackOnly && _interfaces.Length == 0 && _settings.DirectPeers.Count == 0)
                    SetError("Keine private IPv4-LAN-Schnittstelle verfügbar.");
                _socket = socket;
                _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (!_settings.LoopbackOnly) NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
                _tasks = new[] { ReceiveLoopAsync(socket, _stop.Token), HeartbeatLoopAsync(socket, _stop.Token) };
            }
            catch (Exception e) when (e is SocketException or NetworkInformationException)
            { socket.Dispose(); SetError("LAN-Start: " + e.Message); throw; }
        }
        finally { _lifecycle.Release(); }
    }

    public void Publish(bool isGaming, double? downloadMbps = null, double? latencyMs = null,
        double? appliedLimitMbps = null, string? game = null, bool shaperActive = false,
        double? idleBaselineMs = null, string? latencyTargetId = null, DateTimeOffset? idleBaselineAtUtc = null,
        double? uploadMbps = null, string? topProgram = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Valid(downloadMbps, 100000) || !Valid(uploadMbps, 100000) || !Valid(latencyMs, 10000) || !Valid(appliedLimitMbps, 100000) ||
            game?.Length > 80 || game?.Any(char.IsControl) == true || !Valid(idleBaselineMs, 10000) ||
            latencyTargetId?.Length > 256 || latencyTargetId?.Any(char.IsControl) == true || !WireAdvertisement.ValidProgram(topProgram))
            throw new ArgumentException("Ungültiger lokaler Status.");
        Volatile.Write(ref _local, new(isGaming, downloadMbps, latencyMs, appliedLimitMbps, game, shaperActive,
            idleBaselineMs, latencyTargetId, idleBaselineAtUtc, Interlocked.Increment(ref _sampleSequence), uploadMbps,
            Stopwatch.GetTimestamp(), topProgram));
    }

    public IReadOnlyList<PeerAdvertisement> Snapshot()
    {
        lock (_gate)
        {
            ExpireLocked(Stopwatch.GetTimestamp());
            return _peers.Values.Select(p => p.Advertisement).OrderBy(p => p.DeviceName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public PeerNetworkDiagnostics GetNetworkDiagnostics()
    {
        lock (_gate)
        {
            ExpireLocked(Stopwatch.GetTimestamp());
            var last = Interlocked.Read(ref _lastReceiveUnixMs);
            return new("Private Gruppe", _settings.LoopbackOnly ? [IPAddress.Loopback.ToString()] :
                Volatile.Read(ref _interfaces).Select(a => a.ToString()).ToArray(),
                _settings.DirectPeers.Select(p => p.ToString()).ToArray(),
                Interlocked.Read(ref _sent), Interlocked.Read(ref _received), Interlocked.Read(ref _accepted),
                InvalidOrRejectedDatagrams: Interlocked.Read(ref _rejected), ScopeRejectedDatagrams: Interlocked.Read(ref _scopeRejected),
                OtherModeDatagrams: Interlocked.Read(ref _otherMode), LastReceiveUtc: last == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(last),
                ConnectedPeers: _peers.Count, LearnedEndpoints: _peers.Values.Select(p => p.Endpoint).Distinct().Count(),
                AuthenticatedClockSkewDatagrams: Interlocked.Read(ref _clockSkew), SelfDatagrams: Interlocked.Read(ref _self));
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_socket is null) return;
            if (!_settings.LoopbackOnly) NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            _stop!.Cancel();
            _socket.Dispose();
            try { await Task.WhenAll(_tasks).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
            _socket = null;
            _stop.Dispose();
            _stop = null;
            _tasks = Array.Empty<Task>();
            lock (_gate) _peers.Clear();
            Notify();
        }
        finally { _lifecycle.Release(); }
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken cancellationToken)
    {
        var bytes = new byte[PacketCodec.MaxPacketBytes + 1];
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveFromAsync(bytes.AsMemory(), SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), cancellationToken).ConfigureAwait(false);
                var endpoint = (IPEndPoint)result.RemoteEndPoint;
                if (failures > 0)
                {
                    failures = 0;
                    if (NetworkStatus?.StartsWith("LAN-Empfang: ", StringComparison.Ordinal) == true)
                    { Volatile.Write(ref _networkStatus, null); Notify(); }
                }
                Interlocked.Increment(ref _received);
                Interlocked.Exchange(ref _lastReceiveUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (!LanAddress.IsAllowed(endpoint.Address, _settings.LoopbackOnly))
                { Interlocked.Increment(ref _scopeRejected); continue; }
                if (result.ReceivedBytes >= 4 && bytes.AsSpan(0, 4).SequenceEqual("DYNA"u8))
                { Interlocked.Increment(ref _otherMode); continue; }
                var wire = _codec.Decode(bytes.AsSpan(0, result.ReceivedBytes));
                if (wire is null) { Interlocked.Increment(ref _rejected); continue; }
                if (wire.DeviceId == _settings.DeviceId) { Interlocked.Increment(ref _self); continue; }
                var now = DateTimeOffset.UtcNow;
                // Bounds checked as decimal so an authenticated extreme Int64 cannot overflow subtraction.
                if (Math.Abs((decimal)now.ToUnixTimeMilliseconds() - wire.SentAtUnixMs) > 30000)
                { Interlocked.Increment(ref _rejected); Interlocked.Increment(ref _clockSkew); continue; }
                var changed = false;
                lock (_gate)
                {
                    var tick = Stopwatch.GetTimestamp();
                    ExpireLocked(tick);
                    foreach (var old in _replay.Where(kv => Elapsed(kv.Value.Tick, tick) > TimeSpan.FromMinutes(10)).Select(kv => kv.Key).ToArray())
                        _replay.Remove(old);
                    var key = (wire.DeviceId, wire.SessionId);
                    if (_replay.TryGetValue(key, out var previous) && (previous.Retired || wire.Sequence <= previous.Sequence))
                    { Interlocked.Increment(ref _rejected); continue; }
                    if (!_replay.ContainsKey(key) && _replay.Count >= 256 || !_peers.ContainsKey(wire.DeviceId) && _peers.Count >= 64)
                    { Interlocked.Increment(ref _rejected); continue; }
                    // Retire older boot sessions independently of peer TTL. Otherwise a captured
                    // old-session packet could reappear after the newer peer expires.
                    foreach (var old in _replay.Keys.Where(k => k.Device == wire.DeviceId && k.Session != wire.SessionId).ToArray())
                        _replay[old] = _replay[old] with { Retired = true };
                    _replay[key] = new(wire.Sequence, tick);
                    _peers[wire.DeviceId] = new(new(wire.DeviceId, wire.DeviceName, wire.IsGaming,
                        wire.DownloadMbps, wire.LatencyMs, now, endpoint.Address.ToString(), wire.AppliedLimitMbps,
                        wire.Game, wire.ShaperActive, wire.IdleBaselineMs, wire.LatencyTargetId,
                        wire.LatencySampleSequence, wire.IdleBaselineAtUtc, SessionId: wire.SessionId, UploadMbps: wire.UploadMbps,
                        BudgetProposal: wire.BudgetProposal, ShareWeight: wire.ShareWeight, TopProgram: wire.TopProgram), wire.SessionId, tick, endpoint);
                    Interlocked.Increment(ref _accepted);
                    changed = true;
                }
                if (changed) Notify();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SocketException e)
            {
                if (cancellationToken.IsCancellationRequested) break;
                // Windows reports ICMP port-unreachable for a previously sent UDP heartbeat as
                // ConnectionReset on the next receive. A sleeping/offline direct peer is normal;
                // it must not permanently stop discovery of peers that start later.
                if (e.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset or SocketError.ConnectionRefused) continue;
                SetError("LAN-Empfang: " + e.Message);
                // Standby and WLAN changes report transient adapter errors. Back off instead of
                // spinning, but keep receiving so peers reconnect without restarting DYNEX.
                failures++;
                try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(5000, 250 << Math.Min(failures, 5))), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HeartbeatLoopAsync(Socket socket, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_settings.HeartbeatInterval);
        try
        {
            do
            {
                if (Interlocked.Exchange(ref _networkChanged, 0) != 0) RefreshInterfaces(socket);
                var state = Volatile.Read(ref _local);
                var throughputIsFresh = state.PublishedAtTick != 0 && Stopwatch.GetElapsedTime(state.PublishedAtTick) < _settings.PeerTtl;
                var packet = _codec.Encode(new(_settings.DeviceId, _session, Interlocked.Increment(ref _sequence),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _settings.DeviceName, state.Gaming,
                    throughputIsFresh ? state.Download : null, state.Latency, state.Limit, state.Game, state.Shaper,
                    state.Baseline, state.Target, state.SampleSequence > 0 ? state.SampleSequence : null, state.BaselineAt,
                    throughputIsFresh ? state.Upload : null,
                    throughputIsFresh && _settings.CapacityMeasured ? DownloadBudgetProposal.FromSettings(_settings) : null, _settings.ShareWeight,
                        throughputIsFresh ? state.TopProgram : null));
                IPEndPoint[] targets;
                bool expired;
                lock (_gate)
                {
                    expired = ExpireLocked(Stopwatch.GetTimestamp());
                    // Learn the return route only from a fresh, authenticated, replay-checked
                    // advertisement. A single imported address then works in both directions.
                    targets = _settings.DirectPeers.Concat(_peers.Values.Select(p => p.Endpoint)).Distinct().ToArray();
                }
                foreach (var endpoint in targets)
                    await SendAsync(socket, packet, endpoint, cancellationToken).ConfigureAwait(false);
                foreach (var address in _interfaces)
                {
                    try
                    {
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                        await SendAsync(socket, packet, new(MulticastAddress, _settings.Port), cancellationToken).ConfigureAwait(false);
                    }
                    catch (SocketException e) { SetError("LAN-Schnittstelle: " + e.Message); }
                }
                lock (_gate) expired |= ExpireLocked(Stopwatch.GetTimestamp());
                if (expired) Notify();
            } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task SendAsync(Socket socket, byte[] packet, IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            await socket.SendToAsync(packet.AsMemory(), SocketFlags.None, endpoint, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _sent);
        }
        catch (SocketException e) { if (!cancellationToken.IsCancellationRequested) SetError("LAN-Senden: " + e.Message); }
    }

    private bool ExpireLocked(long now)
    {
        var stale = _peers.Where(kv => Elapsed(kv.Value.Tick, now) >= _settings.PeerTtl).Select(kv => kv.Key).ToArray();
        foreach (var key in stale) _peers.Remove(key);
        return stale.Length > 0;
    }

    private static IPAddress[] GetLanInterfaces() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) && n.SupportsMulticast)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
        .Where(a => LanAddress.IsAllowed(a, false)).Distinct().ToArray();
    private void OnNetworkAddressChanged(object? sender, EventArgs args) => Interlocked.Exchange(ref _networkChanged, 1);

    private void RefreshInterfaces(Socket socket)
    {
        try
        {
            var current = GetLanInterfaces();
            // Refresh on a real OS address-change signal, including same-address resume.
            // All socket mutations run in the single heartbeat loop, never in the OS callback.
            foreach (var old in _interfaces)
                try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption(MulticastAddress, old)); }
                catch (SocketException) { /* Old addresses may already have disappeared. */ }
            Volatile.Write(ref _networkStatus, null);
            foreach (var address in current)
                try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastAddress, address)); }
                catch (SocketException e) { SetError("LAN-Discovery nach Netzwechsel: " + e.Message); }
            _interfaces = current;
            if (current.Length == 0 && _settings.DirectPeers.Count == 0)
                SetError("Keine private IPv4-LAN-Schnittstelle verfügbar.");
        }
        catch (Exception e) when (e is NetworkInformationException or SocketException)
        { SetError("LAN-Netzwechsel: " + e.Message); }
    }
    private static TimeSpan Elapsed(long start, long end) => Stopwatch.GetElapsedTime(start, end);
    private static bool Valid(double? value, double max) => value is null || double.IsFinite(value.Value) && value >= 0 && value <= max;
    private void SetError(string error) { Volatile.Write(ref _networkStatus, error); Notify(); }
    private void Notify()
    {
        if (PeersChanged is not { } changed) return;
        foreach (Action subscriber in changed.GetInvocationList())
            try { subscriber(); } catch { /* Observers must not stop discovery or TTL expiration. */ }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; }
        finally { _lifecycle.Release(); }
        await StopAsync().ConfigureAwait(false);
        _codec.Dispose();
    }

    private sealed record LocalState(bool Gaming, double? Download, double? Latency, double? Limit, string? Game, bool Shaper,
        double? Baseline, string? Target, DateTimeOffset? BaselineAt, long SampleSequence, double? Upload, long PublishedAtTick, string? TopProgram = null);
    private sealed record ReceivedPeer(PeerAdvertisement Advertisement, Guid Session, long Tick, IPEndPoint Endpoint);
    private sealed record ReplayEntry(long Sequence, long Tick, bool Retired = false);
}
