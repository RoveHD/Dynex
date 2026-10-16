using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Dynex.Mobile;

public readonly record struct ProxySnapshot(long DownloadBytes, long UploadBytes, int ActiveSessions,
    long LimitedDownloadBytes = 0, long LimitVersion = 0, long? DownloadLimitBytesPerSecond = null,
    long AcceptedConnections = 0, long AuthenticatedConnections = 0, long AuthenticationFailures = 0,
    long OutboundFailures = 0);

/// <summary>
/// Loopback-only RFC1928/RFC1929 SOCKS5 server for a trusted local TUN engine.
/// The socket preparer must throw if VPN protection or network binding fails.
/// Download shaping excludes DNS (destination port 53), loopback, RFC1918, IPv4 link-local,
/// IPv6 link-local and IPv6 ULA. Other destinations share one 64 KiB burst bucket.
/// Upload is not shaped. Counters count forwarded payload, excluding SOCKS framing.
/// UDP has no per-packet authentication in SOCKS5: an authenticated TCP association pins
/// the loopback UDP source endpoint (first valid datagram if the client specifies port zero).
/// </summary>
public sealed class LocalSocksProxy : IAsyncDisposable
{
    private const int BufferSize = 16_384;
    private const int MaximumUdpPacket = 65_507;
    private const int MaximumRoutes = 128;
    private static readonly TimeSpan RouteLifetime = TimeSpan.FromMinutes(2);
    private readonly object lifecycle = new();
    private readonly object trafficSync = new();
    private readonly Action<Socket> prepareOutboundSocket;
    private readonly Func<IPEndPoint, bool> shouldLimitDownload;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolveHost;
    private readonly int maxSessions;
    private readonly int listenPort;
    private readonly ConcurrentDictionary<long, Session> sessions = new();
    private readonly DownloadRateLimiter limiter = new();
    private readonly byte[] usernameBytes;
    private readonly byte[] passwordBytes;
    private CancellationTokenSource? lifetime;
    private TcpListener? listener;
    private Task? acceptTask;
    private Task? disposeTask;
    private long nextSession;
    private long downloadBytes;
    private long uploadBytes;
    private int port;
    private int downloadNoticeSent;
    private long limitedDownloadBytes, limitVersion;
    private long? downloadLimit;
    private long acceptedConnections, authenticatedConnections, authenticationFailures, outboundFailures;

    public LocalSocksProxy(Action<Socket> prepareOutboundSocket, int maxSessions = 128,
        Func<IPEndPoint, bool>? shouldLimitDownload = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolveHost = null,
        int listenPort = 0, string? username = null, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(prepareOutboundSocket);
        if (maxSessions is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maxSessions));
        if (listenPort is < 0 or > 65_535) throw new ArgumentOutOfRangeException(nameof(listenPort));
        if ((username is null) != (password is null)) throw new ArgumentException("Supply both credentials or neither.");
        if (username is not null) ValidateCredential(username, nameof(username));
        if (password is not null) ValidateCredential(password, nameof(password));
        this.prepareOutboundSocket = prepareOutboundSocket;
        this.maxSessions = maxSessions;
        this.listenPort = listenPort;
        this.shouldLimitDownload = shouldLimitDownload ?? IsInternetDownload;
        this.resolveHost = resolveHost ?? ((host, token) => Dns.GetHostAddressesAsync(host, token));
        Username = username ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Password = password ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        usernameBytes = Encoding.ASCII.GetBytes(Username);
        passwordBytes = Encoding.ASCII.GetBytes(Password);
    }

    public string Username { get; }
    public string Password { get; }
    public int Port => Volatile.Read(ref port);
    public long DownloadBytes => Interlocked.Read(ref downloadBytes);
    public long UploadBytes => Interlocked.Read(ref uploadBytes);
    /// <summary>One notification after real downloaded payload; can be rearmed for a new setup check.</summary>
    public event Action? DownloadObserved;
    public void ObserveNextDownload() => Volatile.Write(ref downloadNoticeSent, 0);
    public ProxySnapshot Snapshot()
    {
        lock (trafficSync) return new(DownloadBytes, UploadBytes, sessions.Count,
            limitedDownloadBytes, limitVersion, downloadLimit, Interlocked.Read(ref acceptedConnections),
            Interlocked.Read(ref authenticatedConnections), Interlocked.Read(ref authenticationFailures), Interlocked.Read(ref outboundFailures));
    }
    public void SetDownloadLimit(long? bytesPerSecond)
    {
        lock (trafficSync)
        {
            limiter.SetLimit(bytesPerSecond);
            if (downloadLimit == bytesPerSecond) return;
            downloadLimit = bytesPerSecond;
            limitVersion++;
            limitedDownloadBytes = 0;
        }
    }

    private long? LimitedVersion(bool eligible)
    {
        lock (trafficSync) return eligible && downloadLimit.HasValue ? limitVersion : null;
    }

    private void RecordDownload(int bytes, long? limitedVersion)
    {
        if (bytes <= 0) return;
        lock (trafficSync)
        {
            Interlocked.Add(ref downloadBytes, bytes);
            if (limitedVersion is { } version && version == limitVersion && downloadLimit.HasValue)
                limitedDownloadBytes += bytes;
        }
        if (Interlocked.Exchange(ref downloadNoticeSent, 1) == 0) DownloadObserved?.Invoke();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposeTask is not null, this);
            if (listener is not null) throw new InvalidOperationException("Proxy has already been started.");
            cancellationToken.ThrowIfCancellationRequested();
            var startedListener = new TcpListener(IPAddress.Loopback, listenPort);
            startedListener.Server.ExclusiveAddressUse = true;
            try { startedListener.Start(maxSessions); }
            catch { startedListener.Stop(); throw; }
            listener = startedListener;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Volatile.Write(ref port, ((IPEndPoint)listener.LocalEndpoint).Port);
            acceptTask = AcceptAsync(listener, lifetime.Token);
            return Task.CompletedTask;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
            return new ValueTask(disposeTask ??= StopAsync());
    }

    private async Task StopAsync()
    {
        if (lifetime is not null) await lifetime.CancelAsync().ConfigureAwait(false);
        listener?.Stop();
        if (acceptTask is not null) await acceptTask.ConfigureAwait(false);
        var pending = sessions.Values.ToArray();
        foreach (var session in pending) session.Client.Dispose();
        await Task.WhenAll(pending.Select(s => s.Completion)).ConfigureAwait(false);
        limiter.Dispose();
        lifetime?.Dispose();
        Volatile.Write(ref port, 0);
    }

    private async Task AcceptAsync(TcpListener server, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await server.AcceptSocketAsync(token).ConfigureAwait(false);
                Interlocked.Increment(ref acceptedConnections);
                if (sessions.Count >= maxSessions) { client.Dispose(); continue; }
                client.NoDelay = true;
                var id = Interlocked.Increment(ref nextSession);
                var session = new Session(client);
                sessions.TryAdd(id, session);
                session.Completion = RunSessionAsync(id, session, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
        finally { server.Stop(); Volatile.Write(ref port, 0); }
    }

    private async Task RunSessionAsync(long id, Session session, CancellationToken token)
    {
        using var client = session.Client;
        using var stream = new NetworkStream(client, ownsSocket: false);
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            if (!await AuthenticateAsync(stream, handshake.Token).ConfigureAwait(false)) return;
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, handshake.Token).ConfigureAwait(false);
            if (header[0] != 5 || header[2] != 0) { await ReplyAsync(stream, 1, null, handshake.Token); return; }
            Destination destination;
            try { destination = await ReadDestinationAsync(stream, header[3], handshake.Token).ConfigureAwait(false); }
            catch (InvalidDataException) { await ReplyAsync(stream, 8, null, handshake.Token); return; }
            if (header[1] == 1)
                await ConnectAsync(client, stream, destination, token).ConfigureAwait(false);
            else if (header[1] == 3)
                await AssociateAsync(client, stream, destination, token).ConfigureAwait(false);
            else
                await ReplyAsync(stream, 7, null, handshake.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsSessionFailure(ex)) { }
        finally { sessions.TryRemove(id, out _); }
    }

    private async Task<bool> AuthenticateAsync(NetworkStream stream, CancellationToken token)
    {
        var greeting = new byte[2];
        await stream.ReadExactlyAsync(greeting, token).ConfigureAwait(false);
        if (greeting[0] != 5 || greeting[1] == 0) return false;
        var methods = new byte[greeting[1]];
        await stream.ReadExactlyAsync(methods, token).ConfigureAwait(false);
        var supported = methods.Contains((byte)2);
        await stream.WriteAsync(new byte[] { 5, supported ? (byte)2 : (byte)255 }, token).ConfigureAwait(false);
        if (!supported) { Interlocked.Increment(ref authenticationFailures); return false; }
        var auth = new byte[2];
        await stream.ReadExactlyAsync(auth, token).ConfigureAwait(false);
        if (auth[0] != 1 || auth[1] == 0) return false;
        var username = new byte[auth[1]];
        await stream.ReadExactlyAsync(username, token).ConfigureAwait(false);
        var length = new byte[1];
        await stream.ReadExactlyAsync(length, token).ConfigureAwait(false);
        var password = new byte[length[0]];
        await stream.ReadExactlyAsync(password, token).ConfigureAwait(false);
        var valid = CryptographicOperations.FixedTimeEquals(username, usernameBytes)
            & CryptographicOperations.FixedTimeEquals(password, passwordBytes);
        if (valid) Interlocked.Increment(ref authenticatedConnections);
        else Interlocked.Increment(ref authenticationFailures);
        await stream.WriteAsync(new byte[] { 1, valid ? (byte)0 : (byte)1 }, token).ConfigureAwait(false);
        return valid;
    }

    private async Task ConnectAsync(Socket client, NetworkStream stream, Destination destination, CancellationToken token)
    {
        Socket? remote = null;
        try
        {
            using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            var endpoints = await ResolveAsync(destination, connectDeadline.Token).ConfigureAwait(false);
            Exception? failure = null;
            foreach (var endpoint in endpoints)
            {
                var candidate = CreateOutbound(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await candidate.ConnectAsync(endpoint, connectDeadline.Token).ConfigureAwait(false);
                    remote = candidate;
                    break;
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                {
                    candidate.Dispose();
                    failure = ex;
                    connectDeadline.Token.ThrowIfCancellationRequested();
                }
                catch { candidate.Dispose(); throw; }
            }
            if (remote is null) throw failure ?? new SocketException((int)SocketError.HostNotFound);
        }
        catch (Exception ex) when (IsSessionFailure(ex))
        {
            if (!token.IsCancellationRequested) Interlocked.Increment(ref outboundFailures);
            remote?.Dispose();
            var code = ex is SocketException { SocketErrorCode: SocketError.ConnectionRefused } ? (byte)5 : (byte)4;
            await ReplyAsync(stream, code, null, token).ConfigureAwait(false);
            return;
        }
        using (remote)
        using (var relayLifetime = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            await ReplyAsync(stream, 0, (IPEndPoint)remote.LocalEndPoint!, token).ConfigureAwait(false);
            var limitDownload = shouldLimitDownload((IPEndPoint)remote.RemoteEndPoint!);
            await Task.WhenAll(
                RelayAsync(client, remote, false, false, relayLifetime),
                RelayAsync(remote, client, true, limitDownload, relayLifetime)).ConfigureAwait(false);
        }
    }

    private async Task RelayAsync(Socket source, Socket target, bool download, bool limited, CancellationTokenSource lifetime)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (true)
            {
                var received = await source.ReceiveAsync(buffer, SocketFlags.None, lifetime.Token).ConfigureAwait(false);
                if (received == 0)
                {
                    try { target.Shutdown(SocketShutdown.Send); } catch (SocketException) { } catch (ObjectDisposedException) { }
                    return;
                }
                var limitedVersion = LimitedVersion(limited);
                if (limited) await limiter.WaitAsync(received, lifetime.Token).ConfigureAwait(false);
                var remaining = buffer.AsMemory(0, received);
                while (!remaining.IsEmpty)
                {
                    var sent = await target.SendAsync(remaining, SocketFlags.None, lifetime.Token).ConfigureAwait(false);
                    if (sent == 0) throw new IOException("Socket closed while forwarding.");
                    if (download) RecordDownload(sent, limitedVersion); else Interlocked.Add(ref uploadBytes, sent);
                    remaining = remaining[sent..];
                }
            }
        }
        catch { await lifetime.CancelAsync().ConfigureAwait(false); throw; }
    }

    private async Task AssociateAsync(Socket client, NetworkStream stream, Destination requested, CancellationToken token)
    {
        var controlAddress = ((IPEndPoint)client.RemoteEndPoint!).Address;
        // UDP ASSOCIATE must refer to the local TCP client's address; no DNS lookup is needed here.
        if (requested.Address is null || (!requested.Address.Equals(IPAddress.Any) && !requested.Address.Equals(IPAddress.IPv6Any)
            && !requested.Address.Equals(controlAddress)))
        {
            await ReplyAsync(stream, 2, null, token).ConfigureAwait(false);
            return;
        }
        using var association = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var relay = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        relay.ExclusiveAddressUse = true;
        relay.ReceiveBufferSize = 131_072;
        relay.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var state = new UdpState(this, relay, controlAddress, requested.Port, association);
        await ReplyAsync(stream, 0, (IPEndPoint)relay.LocalEndPoint!, token).ConfigureAwait(false);
        var control = WatchControlAsync(stream, association);
        var requests = state.RunRequestsAsync();
        try { await Task.WhenAll(control, requests).ConfigureAwait(false); }
        finally
        {
            await association.CancelAsync().ConfigureAwait(false);
            await state.FinishAsync().ConfigureAwait(false);
        }
    }

    private static async Task WatchControlAsync(NetworkStream stream, CancellationTokenSource lifetime)
    {
        try
        {
            // No further commands or payload are legal on an association's control connection.
            await stream.ReadAsync(new byte[1], lifetime.Token).ConfigureAwait(false);
        }
        finally { await lifetime.CancelAsync().ConfigureAwait(false); }
    }

    private Socket CreateOutbound(AddressFamily family, SocketType type, ProtocolType protocol)
    {
        var socket = new Socket(family, type, protocol);
        try
        {
            if (type == SocketType.Stream) socket.NoDelay = true;
            else socket.ReceiveBufferSize = 131_072;
            try { prepareOutboundSocket(socket); }
            catch (Exception ex) { throw new IOException("Preparing the outbound socket failed.", ex); }
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    private async Task<IPEndPoint[]> ResolveAsync(Destination destination, CancellationToken token)
    {
        if (destination.Port == 0) throw new InvalidDataException("Zero destination port.");
        IPAddress[] addresses;
        try
        {
            // Platform resolvers may not implement cancellation; never let one hold VPN shutdown.
            addresses = destination.Address is not null ? [destination.Address]
                : await resolveHost(destination.Host!, token).WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new IOException("Destination resolution failed.", ex); }
        var endpoints = addresses.Where(IsUnicast).Distinct().Take(8)
            .Select(address => new IPEndPoint(address, destination.Port)).ToArray();
        if (endpoints.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
        return endpoints;
    }

    private static async Task<Destination> ReadDestinationAsync(NetworkStream stream, byte addressType, CancellationToken token)
    {
        int length;
        if (addressType == 1) length = 4;
        else if (addressType == 4) length = 16;
        else if (addressType == 3)
        {
            var size = new byte[1];
            await stream.ReadExactlyAsync(size, token).ConfigureAwait(false);
            length = size[0];
            if (length == 0) throw new InvalidDataException("Empty host.");
        }
        else throw new InvalidDataException("Unknown address type.");
        var bytes = new byte[length + 2];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        var port = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(length));
        return addressType == 3
            ? new Destination(null, ReadHost(bytes.AsSpan(0, length)), port)
            : new Destination(new IPAddress(bytes.AsSpan(0, length)), null, port);
    }

    private static string ReadHost(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > 253) throw new InvalidDataException("Invalid host length.");
        foreach (var value in bytes)
            if (!(value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)'-' or (byte)'_'))
                throw new InvalidDataException("Invalid host character.");
        return Encoding.ASCII.GetString(bytes);
    }

    private static Task ReplyAsync(NetworkStream stream, byte code, IPEndPoint? endpoint, CancellationToken token)
    {
        var bytes = EncodeEndpoint(endpoint ?? new IPEndPoint(IPAddress.Any, 0), 0);
        bytes[0] = 5;
        bytes[1] = code;
        return stream.WriteAsync(bytes, token).AsTask();
    }

    private static byte[] EncodeEndpoint(IPEndPoint endpoint, int payloadLength)
    {
        var address = endpoint.Address.GetAddressBytes();
        var bytes = new byte[4 + address.Length + 2 + payloadLength];
        bytes[3] = address.Length == 4 ? (byte)1 : (byte)4;
        address.CopyTo(bytes, 4);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4 + address.Length), (ushort)endpoint.Port);
        return bytes;
    }

    public static bool IsInternetDownload(IPEndPoint endpoint)
    {
        var address = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
        if (endpoint.Port == 53 || IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal) return false;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 16) return (bytes[0] & 0xfe) != 0xfc;
        return !(bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254);
    }

    private static bool IsUnicast(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast)) return false;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 ? bytes[0] is > 0 and < 224 : bytes[0] != 0xff;
    }

    private static bool IsSessionFailure(Exception ex) => ex is IOException or SocketException
        or OperationCanceledException or ObjectDisposedException or InvalidOperationException or ArgumentException;

    private static void ValidateCredential(string value, string parameter)
    {
        if (value.Length is < 1 or > 255 || value.Any(character => character is < '!' or > '~'))
            throw new ArgumentException("Credentials must contain 1..255 printable ASCII characters without whitespace.", parameter);
    }

    private sealed record Destination(IPAddress? Address, string? Host, int Port);
    private sealed class Session(Socket client)
    {
        public Socket Client { get; } = client;
        public Task Completion { get; set; } = Task.CompletedTask;
    }

    private sealed class UdpState(LocalSocksProxy owner, Socket relay, IPAddress controlAddress,
        int requestedPort, CancellationTokenSource lifetime) : IDisposable
    {
        private readonly Dictionary<IPEndPoint, long> routes = new();
        private readonly SemaphoreSlim routeLock = new(1, 1);
        private readonly List<Task> receivers = [];
        private Socket? ipv4;
        private Socket? ipv6;
        private IPEndPoint? clientEndpoint = requestedPort == 0 ? null : new(controlAddress, requestedPort);

        public async Task RunRequestsAsync()
        {
            var packet = new byte[MaximumUdpPacket + 1];
            try
            {
                while (true)
                {
                    SocketReceiveFromResult received;
                    try
                    {
                        received = await relay.ReceiveFromAsync(packet, SocketFlags.None,
                            new IPEndPoint(IPAddress.Any, 0), lifetime.Token).ConfigureAwait(false);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize) { continue; }
                    var source = (IPEndPoint)received.RemoteEndPoint;
                    if (!source.Address.Equals(controlAddress) || (clientEndpoint is not null && !source.Equals(clientEndpoint))) continue;
                    if (received.ReceivedBytes > MaximumUdpPacket || !TryParse(packet.AsSpan(0, received.ReceivedBytes), out var destination, out var offset)) continue;
                    // Pin only a syntactically valid request from the authenticated TCP client's host.
                    clientEndpoint ??= source;
                    IPEndPoint[] targets;
                    try
                    {
                        using var resolution = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        resolution.CancelAfter(TimeSpan.FromSeconds(10));
                        targets = await owner.ResolveAsync(destination!, resolution.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsSessionFailure(ex) && !lifetime.IsCancellationRequested)
                    { Interlocked.Increment(ref owner.outboundFailures); continue; }
                    var target = targets[0];
                    var outbound = GetOutbound(target.AddressFamily);
                    await routeLock.WaitAsync(lifetime.Token).ConfigureAwait(false);
                    try
                    {
                        var sent = await outbound.SendToAsync(packet.AsMemory(offset, received.ReceivedBytes - offset),
                            SocketFlags.None, target, lifetime.Token).ConfigureAwait(false);
                        // The receive path takes this same lock: even an immediate response cannot
                        // race ahead of successful-send route authorization.
                        if (!routes.ContainsKey(target) && routes.Count >= MaximumRoutes)
                            routes.Remove(routes.MinBy(pair => pair.Value).Key);
                        routes[target] = Stopwatch.GetTimestamp();
                        Interlocked.Add(ref owner.uploadBytes, sent);
                    }
                    catch (SocketException) { Interlocked.Increment(ref owner.outboundFailures); }
                    finally { routeLock.Release(); }
                }
            }
            finally { await lifetime.CancelAsync().ConfigureAwait(false); }
        }

        private Socket GetOutbound(AddressFamily family)
        {
            var existing = family == AddressFamily.InterNetwork ? ipv4 : ipv6;
            if (existing is not null) return existing;
            var created = owner.CreateOutbound(family, SocketType.Dgram, ProtocolType.Udp);
            try { created.Bind(new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0)); }
            catch { created.Dispose(); throw; }
            if (family == AddressFamily.InterNetwork) ipv4 = created; else ipv6 = created;
            receivers.Add(ReceiveResponsesAsync(created, family));
            return created;
        }

        private async Task ReceiveResponsesAsync(Socket outbound, AddressFamily family)
        {
            var payload = new byte[MaximumUdpPacket + 1];
            try
            {
                while (true)
                {
                    SocketReceiveFromResult received;
                    try
                    {
                        received = await outbound.ReceiveFromAsync(payload, SocketFlags.None,
                            new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0), lifetime.Token).ConfigureAwait(false);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset or SocketError.ConnectionRefused) { continue; }
                    var source = (IPEndPoint)received.RemoteEndPoint;
                    var destination = clientEndpoint;
                    if (destination is null) continue;
                    await routeLock.WaitAsync(lifetime.Token).ConfigureAwait(false);
                    bool allowed;
                    try
                    {
                        allowed = routes.TryGetValue(source, out var sentAt) && Stopwatch.GetElapsedTime(sentAt) <= RouteLifetime;
                        if (!allowed) routes.Remove(source);
                    }
                    finally { routeLock.Release(); }
                    if (!allowed) continue;
                    var headerLength = family == AddressFamily.InterNetwork ? 10 : 22;
                    // A SOCKS UDP packet itself must fit in an IPv4 UDP datagram to loopback.
                    if (received.ReceivedBytes + headerLength > MaximumUdpPacket) continue;
                    var eligible = owner.shouldLimitDownload(source);
                    var limitedVersion = owner.LimitedVersion(eligible);
                    if (eligible) await owner.limiter.WaitAsync(received.ReceivedBytes, lifetime.Token).ConfigureAwait(false);
                    var packet = EncodeEndpoint(source, received.ReceivedBytes);
                    payload.AsSpan(0, received.ReceivedBytes).CopyTo(packet.AsSpan(headerLength));
                    var sent = await relay.SendToAsync(packet, SocketFlags.None, destination, lifetime.Token).ConfigureAwait(false);
                    if (sent == packet.Length) owner.RecordDownload(received.ReceivedBytes, limitedVersion);
                }
            }
            finally { await lifetime.CancelAsync().ConfigureAwait(false); }
        }

        private static bool TryParse(ReadOnlySpan<byte> packet, out Destination? destination, out int payloadOffset)
        {
            destination = null;
            payloadOffset = 0;
            if (packet.Length < 4 || packet[0] != 0 || packet[1] != 0 || packet[2] != 0) return false;
            var type = packet[3];
            var offset = 4;
            int length;
            if (type == 1) length = 4;
            else if (type == 4) length = 16;
            else if (type == 3 && packet.Length >= 5) { length = packet[4]; offset++; }
            else return false;
            if (length == 0 || packet.Length < offset + length + 2) return false;
            var port = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + length, 2));
            if (port == 0) return false;
            try
            {
                destination = type == 3 ? new Destination(null, ReadHost(packet.Slice(offset, length)), port)
                    : new Destination(new IPAddress(packet.Slice(offset, length)), null, port);
            }
            catch (InvalidDataException) { return false; }
            payloadOffset = offset + length + 2;
            return true;
        }

        public async Task FinishAsync()
        {
            try { await Task.WhenAll(receivers).ConfigureAwait(false); }
            catch (Exception ex) when (IsSessionFailure(ex)) { }
        }

        public void Dispose()
        {
            ipv4?.Dispose();
            ipv6?.Dispose();
            routeLock.Dispose();
        }
    }
}
