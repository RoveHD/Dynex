using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Dynex.Core;

/// <summary>Automatic first-contact trust confined to on-link Windows private networks.</summary>
public sealed class AutoLanPeerService : IPeerService
{
    private static readonly IPAddress Multicast = IPAddress.Parse("239.255.39.74");
    private readonly DynexSettings _settings;
    private readonly string _privateKey;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Dictionary<Guid, Session> _channels = new();
    private readonly Dictionary<Guid, (PeerAdvertisement Peer, long Tick)> _peers = new();
    private readonly Dictionary<(Guid Id, Guid Session), (long Tick, bool Retired)> _sessions = new();
    private readonly Dictionary<IPAddress, (long Tick, int Count)> _rates = new();
    private AutoLanBinding[] _bindings = [];
    private AutoLanCrypto? _crypto;
    private Socket? _socket;
    private CancellationTokenSource? _stop;
    private Task[] _tasks = [];
    private long _sequence, _sampleSequence, _rateTick;
    private int _rateCount, _networkChanged;
    private bool _subscribed;
    private volatile bool _disposed;
    private string? _networkStatus;
    private State _local = new(false, null, null, null, null, false, null, null, null, 0, null, 0);

    public event Action? PeersChanged;
    public string? NetworkStatus => Volatile.Read(ref _networkStatus);

    public AutoLanPeerService(DynexSettings settings, string signingPrivateKeyBase64)
    {
        settings.Validate(false);
        if (AutoLanIdentity.GetDeviceId(signingPrivateKeyBase64) != settings.DeviceId)
            throw new ArgumentException("AutoLAN Geräte-ID muss vom Identitätsschlüssel abgeleitet sein.");
        _settings = settings with { DirectPeers = settings.DirectPeers.Select(p => new IPEndPoint(p.Address, p.Port)).ToArray() };
        _privateKey = signingPrivateKeyBase64;
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
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, false);
                socket.ReceiveBufferSize = 128 * 1024;
                socket.Bind(new IPEndPoint(_settings.LoopbackOnly ? IPAddress.Loopback : IPAddress.Any, _settings.Port));
                lock (_gate) { _crypto = new(_privateKey); _sequence = 0; }
                Volatile.Write(ref _networkStatus, null);
                RefreshScope(socket, true);
                _socket = socket;
                _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (!_settings.LoopbackOnly)
                { NetworkChange.NetworkAddressChanged += OnNetworkChanged; _subscribed = true; }
                _tasks = [ReceiveLoopAsync(socket, _stop.Token), HeartbeatLoopAsync(socket, _stop.Token)];
            }
            catch
            {
                socket.Dispose();
                lock (_gate) { _crypto?.Dispose(); _crypto = null; }
                throw;
            }
        }
        catch (SocketException e) { SetError("AutoLAN-Start: " + e.Message); throw; }
        finally { _lifecycle.Release(); }
    }

    public void Publish(bool isGaming, double? downloadMbps = null, double? latencyMs = null,
        double? appliedLimitMbps = null, string? game = null, bool shaperActive = false,
        double? idleBaselineMs = null, string? latencyTargetId = null, DateTimeOffset? idleBaselineAtUtc = null,
        double? uploadMbps = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Valid(downloadMbps, 100000) || !Valid(uploadMbps, 100000) || !Valid(latencyMs, 10000) || !Valid(appliedLimitMbps, 100000) ||
            game?.Length > 80 || game?.Any(char.IsControl) == true || !Valid(idleBaselineMs, 10000) ||
            latencyTargetId?.Length > 256 || latencyTargetId?.Any(char.IsControl) == true) throw new ArgumentException("Ungültiger lokaler Status.");
        Volatile.Write(ref _local, new(isGaming, downloadMbps, latencyMs, appliedLimitMbps, game, shaperActive,
            idleBaselineMs, latencyTargetId, idleBaselineAtUtc, Interlocked.Increment(ref _sampleSequence), uploadMbps,
            Stopwatch.GetTimestamp()));
    }

    public IReadOnlyList<PeerAdvertisement> Snapshot()
    {
        lock (_gate)
        {
            ExpireLocked(Stopwatch.GetTimestamp());
            return _peers.Values.Select(p => p.Peer).OrderBy(p => p.DeviceName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken token)
    {
        var bytes = new byte[AutoLanCrypto.MaxDatagram + 1];
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var received = await socket.ReceiveMessageFromAsync(bytes.AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), token).ConfigureAwait(false);
                    var source = (IPEndPoint)received.RemoteEndPoint;
                    var length = received.ReceivedBytes;
                    if (!SourceAllowed(source.Address, received.PacketInformation.Interface) || length is < 6 or > AutoLanCrypto.MaxDatagram ||
                        !bytes.AsSpan(0, 4).SequenceEqual("DYNA"u8) || bytes[4] != 1) continue;
                    byte[]? response = null;
                    var changed = false;
                    lock (_gate)
                    {
                        var tick = Stopwatch.GetTimestamp();
                        if (!AllowPacketLocked(source.Address, tick, bytes[5] != 3)) continue;
                        ExpireLocked(tick);
                        var now = DateTimeOffset.UtcNow;
                        if (bytes[5] == 3)
                        {
                            if (length < AutoLanCrypto.StatusHeader + 16) continue;
                            var id = new Guid(bytes.AsSpan(6, 16));
                            if (!_channels.TryGetValue(id, out var session)) continue;
                            var wire = _crypto!.Decrypt(session.Crypto, bytes.AsSpan(0, length), now);
                            if (wire is null) continue;
                            session.LastActivity = tick;
                            // Only authenticated ciphertext can update a device's observed address.
                            session.Endpoint = source;
                            _peers[id] = (new(id, wire.DeviceName, wire.IsGaming, wire.DownloadMbps, wire.LatencyMs,
                                now, source.Address.ToString(), wire.AppliedLimitMbps, wire.Game, wire.ShaperActive,
                                wire.IdleBaselineMs, wire.LatencyTargetId, wire.LatencySampleSequence, wire.IdleBaselineAtUtc, SessionId: wire.SessionId,
                                UploadMbps: wire.UploadMbps, BudgetProposal: wire.BudgetProposal), tick);
                            changed = true;
                        }
                        else if (bytes[5] is 1 or 2)
                        {
                            var hello = AutoLanCrypto.Verify(bytes.AsSpan(0, length), now);
                            if (hello is null || hello.DeviceId == _settings.DeviceId) continue;
                            var key = (hello.DeviceId, hello.SessionId);
                            if (_sessions.TryGetValue(key, out var history) && history.Retired) continue;
                            if (bytes[5] == 1)
                            {
                                // Stateless signed challenge response: unproven announcements do not
                                // consume peer/session slots or reset established replay counters.
                                response = _crypto!.Ack(hello, now);
                            }
                            else
                            {
                                if (!_crypto!.IsAckForLocal(hello)) continue;
                                if (_channels.TryGetValue(hello.DeviceId, out var prior) && prior.Crypto.Remote.SessionId == hello.SessionId)
                                {
                                    // Existing session's signed ephemeral key/challenge are immutable.
                                    if (prior.Crypto.Remote.ExchangePublicKey != hello.ExchangePublicKey || prior.Crypto.Remote.Challenge != hello.Challenge) continue;
                                    continue; // Never reset sequence state on a replayed acknowledgement.
                                }
                                if (!_channels.ContainsKey(hello.DeviceId) && _channels.Count >= 64 || !_sessions.ContainsKey(key) && _sessions.Count >= 256) continue;
                                var channel = _crypto.Derive(hello);
                                foreach (var old in _sessions.Keys.Where(k => k.Id == hello.DeviceId && k.Session != hello.SessionId).ToArray())
                                    _sessions[old] = (tick, true);
                                _sessions[key] = (tick, false);
                                if (prior is not null) { prior.Crypto.Dispose(); _peers.Remove(hello.DeviceId); }
                                _channels[hello.DeviceId] = new(channel, source, tick);
                                response = _crypto.Ack(hello, now);
                            }
                        }
                    }
                    if (response is not null) await SendAsync(socket, response, source, token).ConfigureAwait(false);
                    if (changed) Notify();
                }
                catch (SocketException e)
                {
                    if (token.IsCancellationRequested) break;
                    if (e.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset or SocketError.ConnectionRefused) continue;
                    SetError("AutoLAN-Empfang: " + e.Message); break;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
    }

    private async Task HeartbeatLoopAsync(Socket socket, CancellationToken token)
    {
        using var timer = new PeriodicTimer(_settings.HeartbeatInterval);
        try
        {
            do
            {
                // Revalidate profile policy each heartbeat: changing a connection to Public must
                // close AutoLAN scope even when Windows keeps the same IP address.
                RefreshScope(socket, Interlocked.Exchange(ref _networkChanged, 0) != 0);
                byte[] hello;
                List<(byte[] Packet, IPEndPoint Endpoint)> statuses = [];
                bool expired;
                lock (_gate)
                {
                    var now = DateTimeOffset.UtcNow;
                    expired = ExpireLocked(Stopwatch.GetTimestamp());
                    hello = _crypto!.Hello(now);
                    var local = Volatile.Read(ref _local);
                    var throughputIsFresh = local.PublishedAtTick != 0 && Stopwatch.GetElapsedTime(local.PublishedAtTick) < _settings.PeerTtl;
                    var wire = new WireAdvertisement(_settings.DeviceId, _crypto.Local.SessionId, ++_sequence, now.ToUnixTimeMilliseconds(),
                        _settings.DeviceName, local.Gaming, throughputIsFresh ? local.Download : null, local.Latency, local.Limit, local.Game, local.Shaper,
                        local.Baseline, local.Target, local.SampleSequence > 0 ? local.SampleSequence : null, local.BaselineAt,
                        throughputIsFresh ? local.Upload : null,
                        throughputIsFresh && _settings.CapacityMeasured ? DownloadBudgetProposal.FromSettings(_settings) : null);
                    foreach (var session in _channels.Values)
                        if (EndpointAllowed(session.Endpoint.Address)) statuses.Add((_crypto.Encrypt(session.Crypto, wire), session.Endpoint));
                }
                foreach (var endpoint in _settings.DirectPeers)
                    if (EndpointAllowed(endpoint.Address)) await SendAsync(socket, hello, endpoint, token).ConfigureAwait(false);
                foreach (var binding in Volatile.Read(ref _bindings))
                {
                    try
                    {
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, binding.Address.GetAddressBytes());
                        await SendAsync(socket, hello, new(Multicast, _settings.Port), token).ConfigureAwait(false);
                        // A subnet broadcast reaches home mesh segments that suppress multicast.
                        await SendAsync(socket, hello, new(binding.Broadcast, _settings.Port), token).ConfigureAwait(false);
                    }
                    catch (SocketException e) { SetError("AutoLAN-Schnittstelle: " + e.Message); }
                }
                foreach (var (packet, endpoint) in statuses) await SendAsync(socket, packet, endpoint, token).ConfigureAwait(false);
                if (expired) Notify();
            } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
    }

    private void RefreshScope(Socket socket, bool force)
    {
        if (_settings.LoopbackOnly) return;
        var (current, error) = AutoLanNetworkScope.Read();
        var previous = Volatile.Read(ref _bindings);
        if (!force && previous.SequenceEqual(current))
        { if (error is not null) SetError(error); return; }
        foreach (var binding in previous)
            try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption(Multicast, binding.Address)); }
            catch (SocketException) { }
        Volatile.Write(ref _bindings, current);
        Volatile.Write(ref _networkStatus, error);
        foreach (var binding in current)
            try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Multicast, binding.Address)); }
            catch (SocketException e) { SetError("AutoLAN Multicast: " + e.Message + " · Subnetz-Broadcast bleibt aktiv."); }
        lock (_gate)
        {
            foreach (var id in _channels.Where(p => !EndpointAllowed(p.Value.Endpoint.Address)).Select(p => p.Key).ToArray())
            { _channels[id].Crypto.Dispose(); _channels.Remove(id); _peers.Remove(id); }
        }
        Notify();
    }

    private bool EndpointAllowed(IPAddress ip) => _settings.LoopbackOnly ? IPAddress.IsLoopback(ip) :
        LanAddress.IsAllowed(ip, false) && Volatile.Read(ref _bindings).Any(b => b.Contains(ip));
    private bool SourceAllowed(IPAddress ip, int interfaceIndex) => _settings.LoopbackOnly ? IPAddress.IsLoopback(ip) :
        LanAddress.IsAllowed(ip, false) && Volatile.Read(ref _bindings).Any(b => b.InterfaceIndex == interfaceIndex && b.Contains(ip));

    private bool AllowPacketLocked(IPAddress source, long now, bool handshake)
    {
        if (Stopwatch.GetElapsedTime(_rateTick, now) >= TimeSpan.FromSeconds(1)) { _rateTick = now; _rateCount = 0; }
        if (++_rateCount > 512) return false;
        if (!handshake) return true;
        foreach (var old in _rates.Where(p => Stopwatch.GetElapsedTime(p.Value.Tick, now) > TimeSpan.FromSeconds(30)).Select(p => p.Key).ToArray()) _rates.Remove(old);
        if (!_rates.TryGetValue(source, out var rate)) { if (_rates.Count >= 128) return false; rate = (now, 0); }
        if (Stopwatch.GetElapsedTime(rate.Tick, now) >= TimeSpan.FromSeconds(1)) rate = (now, 0);
        rate.Count++;
        _rates[source] = rate;
        return rate.Count <= 16;
    }

    private bool ExpireLocked(long tick)
    {
        var ids = _peers.Where(p => Stopwatch.GetElapsedTime(p.Value.Tick, tick) >= _settings.PeerTtl).Select(p => p.Key).ToArray();
        foreach (var id in ids) _peers.Remove(id);
        foreach (var id in _channels.Where(p => Stopwatch.GetElapsedTime(p.Value.LastActivity, tick) > TimeSpan.FromMinutes(10)).Select(p => p.Key).ToArray())
        { _channels[id].Crypto.Dispose(); _channels.Remove(id); }
        foreach (var key in _sessions.Where(p => Stopwatch.GetElapsedTime(p.Value.Tick, tick) > TimeSpan.FromMinutes(10)).Select(p => p.Key).ToArray())
            if (!_channels.TryGetValue(key.Id, out var channel) || channel.Crypto.Remote.SessionId != key.Session) _sessions.Remove(key);
        return ids.Length > 0;
    }

    private async Task SendAsync(Socket socket, byte[] bytes, IPEndPoint target, CancellationToken token)
    {
        try { await socket.SendToAsync(bytes.AsMemory(), SocketFlags.None, target, token).ConfigureAwait(false); }
        catch (SocketException e) { if (!token.IsCancellationRequested) SetError("AutoLAN-Senden: " + e.Message); }
    }
    private void OnNetworkChanged(object? sender, EventArgs args) => Interlocked.Exchange(ref _networkChanged, 1);
    private static bool Valid(double? value, double max) => value is null || double.IsFinite(value.Value) && value >= 0 && value <= max;
    private void SetError(string error) { if (NetworkStatus == error) return; Volatile.Write(ref _networkStatus, error); Notify(); }
    private void Notify()
    {
        if (PeersChanged is not { } action) return;
        foreach (Action subscriber in action.GetInvocationList()) try { subscriber(); } catch { }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_subscribed) { NetworkChange.NetworkAddressChanged -= OnNetworkChanged; _subscribed = false; }
            if (_socket is null) return;
            _stop!.Cancel(); _socket.Dispose();
            try { await Task.WhenAll(_tasks).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
            _socket = null; _stop.Dispose(); _stop = null; _tasks = [];
            lock (_gate)
            {
                foreach (var channel in _channels.Values) channel.Crypto.Dispose();
                _channels.Clear(); _peers.Clear(); _sessions.Clear(); _rates.Clear();
                _crypto?.Dispose(); _crypto = null;
            }
            Volatile.Write(ref _bindings, []);
            Notify();
        }
        finally { _lifecycle.Release(); }
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; }
        finally { _lifecycle.Release(); }
        await StopAsync().ConfigureAwait(false);
    }
    private sealed record State(bool Gaming, double? Download, double? Latency, double? Limit, string? Game, bool Shaper,
        double? Baseline, string? Target, DateTimeOffset? BaselineAt, long SampleSequence, double? Upload, long PublishedAtTick);
    private sealed class Session(AutoChannel crypto, IPEndPoint endpoint, long tick)
    {
        internal AutoChannel Crypto { get; } = crypto;
        internal IPEndPoint Endpoint { get; set; } = endpoint;
        internal long LastActivity { get; set; } = tick;
    }
}
