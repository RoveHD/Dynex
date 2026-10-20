using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Dynex.Mobile;

namespace Dynex.Mobile.Tests;

public static class MobileProxyChecks
{
    public static async Task RunAsync(string[] args, Action<string>? output = null)
    {
        Action<string> write = output ?? Console.WriteLine;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("FAIL: " + label);
            checks++;
            write("PASS: " + label);
        }

        async Task ExpectFailure<T>(Func<Task> action, string label) where T : Exception
        {
            try { await action(); }
            catch (T) { Check(true, label); return; }
            throw new InvalidOperationException("FAIL: " + label);
        }

        async Task WaitUntil(Func<bool> condition)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            while (!condition()) await Task.Delay(10, timeout.Token);
        }

        CheckTrafficEvidence(Check);

        async Task<Socket> Auth(LocalSocksProxy proxy, bool valid = true)
        {
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, proxy.Port, token);
                await Send(client, [5, 1, 2]);
                Check((await Read(client, 2)).SequenceEqual(new byte[] { 5, 2 }), "RFC1929 method selected");
                var user = Encoding.ASCII.GetBytes(proxy.Username);
                var pass = Encoding.ASCII.GetBytes(valid ? proxy.Password : "invalid");
                await Send(client, [1, (byte)user.Length, .. user, (byte)pass.Length, .. pass]);
                Check((await Read(client, 2)).SequenceEqual(new byte[] { 1, valid ? (byte)0 : (byte)1 }),
                    valid ? "random credentials accepted" : "wrong credentials rejected");
                return client;
            }
            catch { client.Dispose(); throw; }
        }

        async Task Send(Socket socket, byte[] data)
        {
            var remaining = data.AsMemory();
            while (!remaining.IsEmpty)
            {
                var sent = await socket.SendAsync(remaining, SocketFlags.None, token);
                if (sent == 0) throw new EndOfStreamException();
                remaining = remaining[sent..];
            }
        }

        async Task<byte[]> Read(Socket socket, int length)
        {
            var data = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var size = await socket.ReceiveAsync(data.AsMemory(offset), SocketFlags.None, token);
                if (size == 0) throw new EndOfStreamException();
                offset += size;
            }
            return data;
        }

        async Task<bool> Closed(Socket socket)
        {
            try { return await socket.ReceiveAsync(new byte[1], SocketFlags.None, token) == 0; }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted) { return true; }
        }

        byte[] AddressBytes(IPEndPoint endpoint)
        {
            var address = endpoint.Address.GetAddressBytes();
            return [address.Length == 4 ? (byte)1 : (byte)4, .. address, (byte)(endpoint.Port >> 8), (byte)endpoint.Port];
        }

        async Task<(byte Code, IPEndPoint Endpoint)> Reply(Socket client)
        {
            var header = await Read(client, 4);
            Check(header[0] == 5 && header[2] == 0 && header[3] is 1 or 4, "valid SOCKS reply framing");
            var address = await Read(client, header[3] == 1 ? 4 : 16);
            var port = await Read(client, 2);
            return (header[1], new IPEndPoint(new IPAddress(address), BinaryPrimitives.ReadUInt16BigEndian(port)));
        }

        async Task<Socket> Connect(LocalSocksProxy proxy, IPEndPoint target, string? hostname = null)
        {
            var client = await Auth(proxy);
            try
            {
                byte[] address = hostname is null ? AddressBytes(target)
                    : [3, (byte)hostname.Length, .. Encoding.ASCII.GetBytes(hostname), (byte)(target.Port >> 8), (byte)target.Port];
                await Send(client, [5, 1, 0, .. address]);
                Check((await Reply(client)).Code == 0, "CONNECT succeeded");
                return client;
            }
            catch { client.Dispose(); throw; }
        }

        async Task<(Socket Control, Socket Client, IPEndPoint Relay)> Associate(LocalSocksProxy proxy)
        {
            var control = await Auth(proxy);
            var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            await Send(control, [5, 3, 0, .. AddressBytes((IPEndPoint)udp.LocalEndPoint!)]);
            var response = await Reply(control);
            Check(response.Code == 0 && IPAddress.IsLoopback(response.Endpoint.Address), "UDP relay bound exclusively to loopback");
            return (control, udp, response.Endpoint);
        }

        async Task<byte[]> UdpReceive(Socket socket, int timeoutMs = 3000)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(timeoutMs);
            var buffer = new byte[65535];
            var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
            return buffer[..result.ReceivedBytes];
        }

        Check(!LocalSocksProxy.IsInternetDownload(new(IPAddress.Parse("192.168.1.5"), 443))
            && !LocalSocksProxy.IsInternetDownload(new(IPAddress.Parse("10.2.3.4"), 443))
            && !LocalSocksProxy.IsInternetDownload(new(IPAddress.Parse("172.31.1.1"), 443))
            && !LocalSocksProxy.IsInternetDownload(new(IPAddress.Parse("169.254.2.2"), 443))
            && !LocalSocksProxy.IsInternetDownload(new(IPAddress.IPv6Loopback, 443))
            && !LocalSocksProxy.IsInternetDownload(new(IPAddress.Parse("fd00::1"), 443))
            && !LocalSocksProxy.IsInternetDownload(new(IPAddress.Parse("fe80::1"), 443))
            && !LocalSocksProxy.IsInternetDownload(new(IPAddress.Parse("::ffff:192.168.1.5"), 443))
            && !LocalSocksProxy.IsInternetDownload(new(IPAddress.Parse("1.1.1.1"), 53)), "LAN and DNS bypass policy covers IPv4, IPv6 and mapped addresses");
        Check(LocalSocksProxy.IsInternetDownload(new(IPAddress.Parse("8.8.8.8"), 443))
            && LocalSocksProxy.IsInternetDownload(new(IPAddress.Parse("2606:4700::1111"), 443)), "public destinations are classified for telemetry only (no WAN traffic)");

        using (var limiter = new DownloadRateLimiter(1024, 1))
        {
            limiter.SetLimit(1);
            var first = limiter.WaitAsync(1024, token);
            var second = limiter.WaitAsync(1024, token);
            Check(first.IsCompletedSuccessfully && second.IsCompletedSuccessfully && limiter.BytesPerSecond is null,
                "legacy one-byte cap cannot create pacing or a waiter queue");
            await Task.WhenAll(first.AsTask(), second.AsTask()).WaitAsync(TimeSpan.FromSeconds(1));
            limiter.SetLimit(1024 * 1024);
            Check(limiter.WaitAsync(1024, token).IsCompletedSuccessfully && limiter.BytesPerSecond is null,
                "changing a legacy cap never installs a bucket");
            limiter.SetLimit(null);
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await ExpectFailure<OperationCanceledException>(() => limiter.WaitAsync(1, cancel.Token).AsTask(),
                "cancelled forwarding still honors cancellation");
            await ExpectFailure<ArgumentOutOfRangeException>(() => limiter.WaitAsync(1025, token).AsTask(),
                "invalid payload reservation remains rejected");
            limiter.Dispose();
            await ExpectFailure<ObjectDisposedException>(() => limiter.WaitAsync(1, token).AsTask(),
                "disposed forwarding compatibility object cannot be reused");
        }

        var prepared = 0;
        await using var proxy = new LocalSocksProxy(socket =>
        {
            Check(!socket.Connected && socket.LocalEndPoint is null, "outgoing socket prepared before connect/bind/send");
            Interlocked.Increment(ref prepared);
        }, resolveHost: (host, _) => host == "loopback.test" ? Task.FromResult(new[] { IPAddress.Loopback }) : throw new InvalidOperationException("Unexpected DNS query"));
        var observedDownloads = 0;
        proxy.DownloadObserved += () => Interlocked.Increment(ref observedDownloads);
        await proxy.StartAsync(token);
        Check(proxy.Port > 0 && proxy.Username.Length >= 16 && proxy.Password.Length >= 32, "random listener port and process credentials available");
        await using (var other = new LocalSocksProxy(_ => { }))
            Check(other.Username != proxy.Username && other.Password != proxy.Password, "independent proxy instances use independent credentials");
        await using (var persisted = new LocalSocksProxy(_ => { }, username: "stable-user", password: "stable-password"))
        {
            await persisted.StartAsync(token);
            Check(persisted.Username == "stable-user" && persisted.Password == "stable-password", "explicit persistent credentials preserved");
            using var authenticated = await Auth(persisted);
        }
        await using (var occupied = new LocalSocksProxy(_ => { }, listenPort: proxy.Port))
            await ExpectFailure<SocketException>(() => occupied.StartAsync(token), "occupied fixed port fails without a silent fallback");
        await ExpectFailure<ArgumentException>(() => Task.FromResult(new LocalSocksProxy(_ => { }, username: "only-one")), "partial credential configuration rejected");
        await ExpectFailure<ArgumentException>(() => Task.FromResult(new LocalSocksProxy(_ => { }, username: "user", password: "bad\npassword")), "control characters in persistent credentials rejected");
        await ExpectFailure<ArgumentException>(() => Task.FromResult(new LocalSocksProxy(_ => { }, username: "user", password: new string('x', 256))), "RFC1929 credential length bounded");

        using (var unauth = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            await unauth.ConnectAsync(IPAddress.Loopback, proxy.Port, token);
            await Send(unauth, [5, 1, 0]);
            Check((await Read(unauth, 2)).SequenceEqual(new byte[] { 5, 255 }), "unauthenticated SOCKS method is refused");
        }
        using (var invalid = await Auth(proxy, false))
            Check(await Closed(invalid), "failed authentication closes connection");
        Check(prepared == 0, "authentication failures never create outbound sockets");
        Check(proxy.Snapshot() is { AcceptedConnections: 2, AuthenticatedConnections: 0, AuthenticationFailures: 2,
            DownloadBytes: 0, LimitedDownloadBytes: 0 }, "diagnostics distinguish rejected SOCKS credentials from forwarded traffic");
        Check(observedDownloads == 0, "idle listener and authentication do not confirm forwarding");

        var baselineListener = new TcpListener(IPAddress.Loopback, 0);
        baselineListener.Start();
        var halfCloseSupported = false;
        using (var baselineClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            await baselineClient.ConnectAsync(baselineListener.LocalEndpoint, token);
            using var baselineServer = await baselineListener.AcceptSocketAsync(token);
            baselineClient.Shutdown(SocketShutdown.Send);
            Check(await baselineServer.ReceiveAsync(new byte[1], SocketFlags.None, token) == 0, "baseline socket half-close arrives");
            try
            {
                await Send(baselineServer, [42]);
                halfCloseSupported = (await Read(baselineClient, 1))[0] == 42;
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                write($"SKIP: relay half-close assertions: direct socket baseline fails on this host ({ex.GetType().Name}).");
            }
        }
        baselineListener.Stop();
        if (args.Contains("--require-half-close")) Check(halfCloseSupported, "host must support direct TCP half-close");

        foreach (var address in Socket.OSSupportsIPv6 ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback } : [IPAddress.Loopback])
        {
            var listener = new TcpListener(address, 0);
            listener.Start();
            try
            {
                var accepting = listener.AcceptSocketAsync(token).AsTask();
                using var client = await Connect(proxy, (IPEndPoint)listener.LocalEndpoint);
                using var remote = await accepting;
                var payload = Encoding.ASCII.GetBytes("payload-" + address);
                await Send(client, payload);
                if (halfCloseSupported) client.Shutdown(SocketShutdown.Send);
                Check((await Read(remote, payload.Length)).SequenceEqual(payload), $"{address} TCP upload forwarded");
                if (halfCloseSupported) Check(await remote.ReceiveAsync(new byte[1], SocketFlags.None, token) == 0, "TCP send half-close reaches target");
                await Send(remote, payload);
                if (halfCloseSupported) remote.Shutdown(SocketShutdown.Send);
                Check((await Read(client, payload.Length)).SequenceEqual(payload), "TCP download forwarded");
                if (halfCloseSupported) Check(await client.ReceiveAsync(new byte[1], SocketFlags.None, token) == 0, "TCP response half-close reaches client");
            }
            finally { listener.Stop(); }
        }

        await WaitUntil(() => Volatile.Read(ref observedDownloads) == 1);
        Check(proxy.DownloadBytes > 0 && observedDownloads == 1, "forwarded payload wakes status exactly once across multiple streams");
        Check(proxy.Snapshot().LimitedDownloadBytes == 0, "unlimited LAN payload cannot confirm an applied internet limit");
        Check(proxy.Snapshot().BulkDownloadIngressBytes == 0, "LAN bypass download never joins the measured bulk allocation");
        var dnsListener = new TcpListener(IPAddress.Loopback, 0);
        dnsListener.Start();
        try
        {
            var accepting = dnsListener.AcceptSocketAsync(token).AsTask();
            using var client = await Connect(proxy, (IPEndPoint)dnsListener.LocalEndpoint, "loopback.test");
            using var remote = await accepting;
            Check(remote.Connected, "DNS CONNECT uses injected network-aware resolver");
            proxy.ObserveNextDownload();
            await Send(remote, [0x42]);
            Check((await Read(client, 1))[0] == 0x42, "setup recheck observes newly forwarded payload");
            await WaitUntil(() => Volatile.Read(ref observedDownloads) == 2);
            Check(observedDownloads == 2, "setup can rearm one status wake without periodic polling");
        }
        finally { dnsListener.Stop(); }

        using (var unsupported = await Auth(proxy))
        {
            await Send(unsupported, [5, 2, 0, .. AddressBytes(new(IPAddress.Loopback, 80))]);
            Check((await Reply(unsupported)).Code == 7, "unsupported BIND command returns REP 7");
        }
        using (var malformed = await Auth(proxy))
        {
            await Send(malformed, [5, 1, 0, 9]);
            Check((await Reply(malformed)).Code == 8, "unsupported address type returns REP 8 without hanging");
        }
        using (var malformed = await Auth(proxy))
        {
            await Send(malformed, [5, 1, 0, 3, 3, (byte)'a', 0, (byte)'b', 0, 80]);
            Check((await Reply(malformed)).Code == 8, "NUL hostname is rejected");
        }
        await using (var protectedProxy = new LocalSocksProxy(_ => throw new InvalidOperationException("protect failed")))
        {
            await protectedProxy.StartAsync(token);
            using var client = await Auth(protectedProxy);
            await Send(client, [5, 1, 0, .. AddressBytes(new(IPAddress.Loopback, 9))]);
            Check((await Reply(client)).Code != 0, "protection failure rejects CONNECT");
        }

        // Isolated loopback with public-flow classification proves legacy cap requests cannot delay payload.
        await using (var limitedProxy = new LocalSocksProxy(_ => { }, shouldLimitDownload: _ => true))
        {
            await limitedProxy.StartAsync(token);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var clients = new List<Socket>();
            var remotes = new List<Socket>();
            try
            {
                for (var i = 0; i < 2; i++)
                {
                    var accepting = listener.AcceptSocketAsync(token).AsTask();
                    clients.Add(await Connect(limitedProxy, (IPEndPoint)listener.LocalEndpoint));
                    remotes.Add(await accepting);
                }
                limitedProxy.SetDownloadLimit(1);
                var payload = new byte[65_536];
                await Task.WhenAll(remotes.Select(remote => Send(remote, payload)));
                await Task.WhenAll(clients.Select(client => Read(client, payload.Length))).WaitAsync(TimeSpan.FromSeconds(2));
                Check(limitedProxy.DownloadBytes == 131_072 && limitedProxy.UploadBytes == 0,
                    "two TCP streams forward complete payload despite a legacy one-byte-per-second cap");
                Check(limitedProxy.Snapshot() is { LimitedDownloadBytes: 0, DownloadLimitBytesPerSecond: null, LimitVersion: 0 },
                    "legacy cap cannot claim TCP shaping or retain limit metadata");
                Check(limitedProxy.Snapshot().BulkDownloadIngressBytes == 131_072,
                    "public TCP payload accounting is preserved without pacing");
                limitedProxy.SetDownloadLimit(65_536);
                Check(limitedProxy.Snapshot().DownloadLimitBytesPerSecond is null,
                    "repeated legacy updates remain unlimited");
                await Send(clients[1], new byte[32_768]);
                await Read(remotes[1], 32_768).WaitAsync(TimeSpan.FromSeconds(1));
                Check(limitedProxy.UploadBytes == 32_768, "uploads remain unpaced");
                limitedProxy.SetDownloadLimit(1);
                await Send(remotes[0], new byte[32_768]);
                await Read(clients[0], 32_768).WaitAsync(TimeSpan.FromSeconds(1));
                Check(limitedProxy.Snapshot().LimitedDownloadBytes == 0,
                    "tiny legacy cap cannot delay a subsequent TCP payload or confirm a limit");
                limitedProxy.SetDownloadLimit(null);
                Check(limitedProxy.Snapshot().BulkDownloadIngressBytes == 163_840,
                    "legacy limit removal cannot reset lifetime ingress accounting");
                await Send(remotes[0], new byte[4096]);
                await Read(clients[0], 4096);
                Check(limitedProxy.Snapshot().BulkDownloadIngressBytes == 167_936,
                    "eligible public ingress remains measured after obsolete cap changes");
                await limitedProxy.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                Check(limitedProxy.Port == 0 && limitedProxy.Snapshot().ActiveSessions == 0,
                    "dispose closes live TCP streams and listener without a limiter queue");
            }
            finally
            {
                foreach (var client in clients) client.Dispose();
                foreach (var remote in remotes) remote.Dispose();
                listener.Stop();
            }
        }

        await WaitUntil(() => proxy.Snapshot().ActiveSessions == 0);
        var initial = proxy.Snapshot();
        if (!args.Contains("--tcp-only"))
        {
        using (var baselineUdpServer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        using (var baselineUdpClient = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            baselineUdpServer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            baselineUdpClient.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            await baselineUdpClient.SendToAsync(new byte[] { 42 }, SocketFlags.None, baselineUdpServer.LocalEndPoint!, token);
            Check((await UdpReceive(baselineUdpServer))[0] == 42, "baseline direct UDP works on host");
        }
        foreach (var address in Socket.OSSupportsIPv6 ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback } : [IPAddress.Loopback])
        {
            var association = await Associate(proxy);
            using var control = association.Control;
            using var client = association.Client;
            using var target = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            target.Bind(new IPEndPoint(address, 0));
            var targetAddress = (IPEndPoint)target.LocalEndPoint!;
            var payload = Encoding.ASCII.GetBytes("udp-" + address);
            await client.SendToAsync((byte[])[0, 0, 0, .. AddressBytes(targetAddress), .. payload], SocketFlags.None, association.Relay, token);
            var buffer = new byte[1024];
            var received = await target.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(address.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0), token);
            Check(buffer[..received.ReceivedBytes].SequenceEqual(payload), $"UDP upload forwarded to {address}");
            await target.SendToAsync(payload, SocketFlags.None, received.RemoteEndPoint, token);
            var response = await UdpReceive(client);
            Check(response.SequenceEqual((byte[])[0, 0, 0, .. AddressBytes(targetAddress), .. payload]), "UDP response carries true source address and payload");

            using var attacker = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            attacker.Bind(new IPEndPoint(address, 0));
            await attacker.SendToAsync(new byte[] { 0x66 }, SocketFlags.None, received.RemoteEndPoint, token);
            await ExpectFailure<OperationCanceledException>(() => UdpReceive(client, 100), "unsolicited UDP source-port cannot use authorized return route");

            using var localAttacker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            localAttacker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            await localAttacker.SendToAsync((byte[])[0, 0, 0, .. AddressBytes(targetAddress), 0x77], SocketFlags.None, association.Relay, token);
            await client.SendToAsync((byte[])[0, 0, 1, .. AddressBytes(targetAddress), 0x88], SocketFlags.None, association.Relay, token);
            await client.SendToAsync(new byte[] { 0, 0, 0, 3, 255, 0x99 }, SocketFlags.None, association.Relay, token);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(100);
                await ExpectFailure<OperationCanceledException>(async () => await target.ReceiveFromAsync(buffer, SocketFlags.None,
                    new IPEndPoint(address.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0), timeout.Token),
                    "UDP rejects wrong local source, fragments and truncated host field");
            }
            // Earlier malformed datagrams must not break the established association.
            await client.SendToAsync((byte[])[0, 0, 0, .. AddressBytes(targetAddress), 42], SocketFlags.None, association.Relay, token);
            received = await target.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(address.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0), token);
            await target.SendToAsync(new byte[] { 42 }, SocketFlags.None, received.RemoteEndPoint, token);
            Check((await UdpReceive(client))[^1] == 42, "valid UDP traffic still flows after malformed inputs");
        }
        await WaitUntil(() => proxy.Snapshot().ActiveSessions == 0);
        var final = proxy.Snapshot();
        var expectedUdpPayload = Encoding.ASCII.GetByteCount("udp-" + IPAddress.Loopback) + 1
            + (Socket.OSSupportsIPv6 ? Encoding.ASCII.GetByteCount("udp-" + IPAddress.IPv6Loopback) + 1 : 0);
        Check(final.DownloadBytes - initial.DownloadBytes == expectedUdpPayload && final.UploadBytes - initial.UploadBytes == expectedUdpPayload,
            "UDP counters exclude malformed/unauthorized traffic and SOCKS framing");

        await using (var mixed = new LocalSocksProxy(_ => { }, shouldLimitDownload: _ => true))
        {
            await mixed.StartAsync(token);
            var association = await Associate(mixed);
            using var control = association.Control;
            using var udpClient = association.Client;
            using var udpTarget = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udpTarget.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var accepting = listener.AcceptSocketAsync(token).AsTask();
                using var tcpClient = await Connect(mixed, (IPEndPoint)listener.LocalEndpoint);
                using var tcpTarget = await accepting;
                var payload = new byte[32_768];
                await udpClient.SendToAsync((byte[])[0, 0, 0, .. AddressBytes((IPEndPoint)udpTarget.LocalEndPoint!), 42],
                    SocketFlags.None, association.Relay, token);
                var received = await udpTarget.ReceiveFromAsync(new byte[16], SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), token);
                mixed.SetDownloadLimit(1);
                await Send(tcpTarget, payload);
                await udpTarget.SendToAsync(payload, SocketFlags.None, received.RemoteEndPoint, token);
                await Task.WhenAll(Read(tcpClient, payload.Length), UdpReceive(udpClient)).WaitAsync(TimeSpan.FromSeconds(2));
                Check(mixed.Snapshot().LimitedDownloadBytes == 0 && mixed.Snapshot().DownloadLimitBytesPerSecond is null,
                    "authenticated TCP and UDP both bypass a legacy one-byte cap");
                Check(mixed.Snapshot().BulkDownloadIngressBytes == 65_536,
                    "TCP and authenticated UDP retain public ingress accounting without pacing");
                mixed.SetDownloadLimit(1);
                await udpTarget.SendToAsync(payload, SocketFlags.None, received.RemoteEndPoint, token);
                await UdpReceive(udpClient).WaitAsync(TimeSpan.FromSeconds(1));
                Check(mixed.Snapshot().BulkDownloadIngressBytes == 98_304 && mixed.Snapshot().LimitedDownloadBytes == 0,
                    "a second UDP payload also forwards immediately without limit evidence");
                mixed.SetDownloadLimit(null);
                await mixed.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                Check(mixed.Snapshot().ActiveSessions == 0, "disposal closes mixed UDP and TCP sessions");
            }
            finally { listener.Stop(); }
        }
        }
        else write("SKIP: UDP tests explicitly excluded by --tcp-only (run default suite on a host with working UDP loopback).");

        await using (var bounded = new LocalSocksProxy(_ => { }, maxSessions: 1))
        {
            await bounded.StartAsync(token);
            using var first = await Auth(bounded);
            using var extra = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await extra.ConnectAsync(IPAddress.Loopback, bounded.Port, token);
            Check(await Closed(extra), "active session count has a hard bound");
            await bounded.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Check(bounded.Snapshot().ActiveSessions == 0, "dispose cancels incomplete authenticated handshake");
        }

        await using (var hangingDns = new LocalSocksProxy(_ => { }, resolveHost: (_, _) => new TaskCompletionSource<IPAddress[]>().Task))
        {
            await hangingDns.StartAsync(token);
            using var client = await Auth(hangingDns);
            await Send(client, [5, 1, 0, 3, 1, (byte)'a', 0, 80]);
            await hangingDns.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Check(true, "disposal cannot hang on a platform resolver ignoring cancellation");
        }
        await using (var udpControlProxy = new LocalSocksProxy(_ => { }))
        {
            await udpControlProxy.StartAsync(token);
            var association = await Associate(udpControlProxy);
            using var control = association.Control;
            using var udp = association.Client;
            await udpControlProxy.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Check(udpControlProxy.Snapshot().ActiveSessions == 0, "UDP association control and pending receive cancel on disposal");
        }

        write($"{checks} executed mobile proxy checks passed. TCP half-close {(halfCloseSupported ? "included" : "blocked by direct host baseline")}; UDP {(args.Contains("--tcp-only") ? "explicitly excluded" : "included")}. No WAN traffic or VPN/filter was started.");
    }

    private static void CheckTrafficEvidence(Action<bool, string> check)
    {
        var clock = new TrafficClock();
        var monitor = new ProxyTrafficMonitor(clock);
        ProxySnapshot sample = new(0, 0, 0);
        check(monitor.Observe(sample, null) is { DownloadMbps: null, UploadMbps: null, ForwardingObserved: false, LimitApplied: false },
            "unused proxy listener reports unknown traffic rather than numeric zero");
        clock.Advance(4);
        check(monitor.Observe(sample, null).DownloadMbps is null, "repeated idle listener samples do not manufacture a measured idle network");
        clock.Advance(4);
        sample = sample with { DownloadBytes = 125_000, UploadBytes = 62_500 };
        check(monitor.Observe(sample, null) is { DownloadMbps: .25, UploadMbps: .125, ForwardingObserved: true, LimitApplied: false },
            "real forwarded payload produces measured proxy rates independently of gaming");
        clock.Advance(1);
        sample = sample with { DownloadBytes = 126_000, LimitVersion = 1, DownloadLimitBytesPerSecond = 125_000 };
        check(!monitor.Observe(sample, 1).LimitApplied, "LAN or DNS bypass payload never confirms an installed internet cap");
        clock.Advance(1);
        sample = sample with { DownloadBytes = 127_000, LimitedDownloadBytes = 1_000 };
        check(!monitor.Observe(sample, 1).LimitApplied, "legacy limited-payload counters cannot claim an active cap");
        clock.Advance(4);
        check(monitor.Observe(sample, 1) is { DownloadMbps: 0, LimitApplied: false },
            "measured idle remains available without reporting a legacy cap");
        clock.Advance(8);
        check(monitor.Observe(sample, 1) is { DownloadMbps: null, ForwardingObserved: false, LimitApplied: false },
            "monotonic evidence expiry removes stale route and limit claims without inventing zero traffic");
        clock.Advance(1);
        sample = sample with { DownloadBytes = 128_000, LimitedDownloadBytes = 2_000 };
        check(monitor.Observe(sample, 1) is { ForwardingObserved: true, LimitApplied: false }, "new payload restores forwarding evidence but never a legacy cap");
        sample = sample with { LimitVersion = 2, LimitedDownloadBytes = 0, DownloadLimitBytesPerSecond = 250_000 };
        check(!monitor.Observe(sample, 2).LimitApplied, "cap replacement needs fresh payload under the new limiter version");
        clock.Advance(1);
        sample = sample with { DownloadBytes = 129_000, LimitedDownloadBytes = 1_000 };
        check(!monitor.Observe(sample, 1).LimitApplied, "a different planned rate cannot reuse an old applied-rate confirmation");
        check(!monitor.Observe(sample, null).LimitApplied, "game end or pause releases confirmation immediately");
        monitor.Reset();
        check(monitor.Observe(new(0, 0, 0), 2) is { DownloadMbps: null, LimitApplied: false },
            "proxy or native-tunnel replacement forgets prior-session traffic and confirmation");
        check(monitor.Observe(null, 2) is { DownloadMbps: null, UploadMbps: null, LimitApplied: false },
            "failed or absent data plane cannot publish measured zero or an applied limit");
        check(monitor.Observe(new(0, 0, 0), null).LimitedDownloadMbps is null,
            "legacy snapshots without a bulk counter retain unknown classified activity");
        monitor.Reset();
        sample = new(0, 0, 0, BulkDownloadIngressBytes: 0);
        check(monitor.Observe(sample, null).LimitedDownloadMbps is null,
            "first live bulk snapshot needs a real measurement interval");
        clock.Advance(4);
        check(monitor.Observe(sample, null) is { LimitedDownloadMbps: 0, DownloadMbps: null, ForwardingObserved: false },
            "live proxy measures classified bulk idle without inventing forwarding or total NIC throughput");
        clock.Advance(4);
        sample = sample with { BulkDownloadIngressBytes = 12_500_000 };
        check(monitor.Observe(sample, null) is { LimitedDownloadMbps: 25, DownloadMbps: null, LimitApplied: false },
            "bulk ingress measures actual pre-pacing demand independently of forwarded traffic");
        sample = sample with { LimitVersion = 3, LimitedDownloadBytes = 0, DownloadLimitBytesPerSecond = 1 };
        clock.Advance(4);
        sample = sample with { BulkDownloadIngressBytes = 25_000_000 };
        check(monitor.Observe(sample, .000008).LimitedDownloadMbps == 25,
            "limit-version changes cannot zero or spike durable bulk demand");
        clock.Advance(11);
        check(monitor.Observe(sample, null).LimitedDownloadMbps is null,
            "sampling gaps over ten seconds remain unknown instead of publishing stale averaged activity");
        clock.Advance(4);
        check(monitor.Observe(sample, null).LimitedDownloadMbps == 0,
            "fresh consecutive idle snapshots restore a measured zero after a sampling gap");
        clock.Advance(4);
        check(monitor.Observe(sample with { BulkDownloadIngressBytes = 0 }, null).LimitedDownloadMbps is null,
            "data-plane counter reset discards the previous lifetime rather than publishing negative demand");
        monitor.Reset();
        check(monitor.Observe(null, null).LimitedDownloadMbps is null,
            "stopped proxy or tunnel never claims measured classified idle");
        check(global::Dynex.Android.AdGuardSetupDiagnostics.VpnIntegrationHint(false, false) is null &&
            global::Dynex.Android.AdGuardSetupDiagnostics.VpnIntegrationHint(true, true) is null,
            "VPN integration hint requires installed VPN and missing forwarding, not mere package presence");
        var hint = global::Dynex.Android.AdGuardSetupDiagnostics.VpnIntegrationHint(true, false)!;
        check(hint.Contains("Prüfe") && hint.Contains("ob") && hint.Contains("IP-Verschleierung") && hint.Contains("ändert diese Einstellung nicht"),
            "possible AdGuard VPN integration explains privacy tradeoff without asserting activity or disabling anything");
        check(global::Dynex.Android.AdGuardSetupDiagnostics.WaitingStatus(new(0, 0, 0), true).Contains("keine Proxy-Verbindung"),
            "sent automation without SOCKS evidence remains unconfirmed");
        check(global::Dynex.Android.AdGuardSetupDiagnostics.WaitingStatus(new(0, 0, 0, AuthenticationFailures: 1), true).Contains("Anmeldung abgewiesen"),
            "rejected SOCKS credentials are distinguished from an unapplied automation request");
        check(global::Dynex.Android.AdGuardSetupDiagnostics.WaitingStatus(new(0, 0, 0, AuthenticatedConnections: 1, OutboundFailures: 1), true).Contains("Weiterleitung ins Netz fehlgeschlagen"),
            "authenticated proxy with failed network forwarding is not presented as active shaping");
        check(!global::Dynex.Android.AdGuardWizardGate.CanContinue(global::Dynex.Android.AppVpnRoute.Vpn) &&
            !global::Dynex.Android.AdGuardWizardGate.CanContinue(global::Dynex.Android.AppVpnRoute.Unknown),
            "wizard cannot skip an unresolved or still-VPN-routed app exception");
        check(!global::Dynex.Android.AdGuardWizardGate.CanFinish(global::Dynex.Android.AppVpnRoute.Physical, false) &&
            !global::Dynex.Android.AdGuardWizardGate.CanFinish(global::Dynex.Android.AppVpnRoute.Vpn, true) &&
            global::Dynex.Android.AdGuardWizardGate.CanFinish(global::Dynex.Android.AppVpnRoute.Physical, true),
            "wizard finishes only with a direct app route and actual forwarding, never just an automation request");
    }

    private sealed class TrafficClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => timestamp;
        public void Advance(int seconds) => timestamp += seconds * TimestampFrequency;
    }
}
