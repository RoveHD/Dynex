using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dynex.Core;

internal static class PeerConnectionTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        if (OperatingSystem.IsWindows())
        {
            using var probeSocket = BoundSocket();
            PeerService.SuppressUdpConnectionReset(probeSocket);
            await probeSocket.SendToAsync(new byte[] { 1 }, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, FreePort()));
            check(!await Receives(probeSocket, 150),
                "Windows unused-port probe does not abort a shared UDP receive with PORT_UNREACHABLE");
            using var healthySender = BoundSocket();
            await healthySender.SendToAsync(new byte[] { 2 }, SocketFlags.None, probeSocket.LocalEndPoint!);
            check(await Receives(probeSocket, 500), "UDP receiver still accepts real peer packets after a closed-port probe");
        }
        var key = GroupKey.Generate();
        var portA = FreePort(); var portB = FreePort();
        while (portB == portA) portB = FreePort();
        var settings = new DynexSettings
        {
            GroupKeyBase64 = key, LoopbackOnly = true, Port = portA,
            HeartbeatInterval = TimeSpan.FromMilliseconds(100), PeerTtl = TimeSpan.FromSeconds(1),
            DirectPeers = [new(IPAddress.Loopback, portB)]
        };
        await using (var initiator = new PeerService(settings))
        await using (var receiver = new PeerService(settings with { DeviceId = Guid.NewGuid(), Port = portB, DirectPeers = [] }))
        {
            await receiver.StartAsync(); await initiator.StartAsync();
            await Until(() => initiator.Snapshot().Count == 1 && receiver.Snapshot().Count == 1);
            check(true, "one-sided PSK direct address establishes bidirectional authenticated UDP discovery");
            var diagnostic = receiver.GetNetworkDiagnostics();
            check(diagnostic.DirectPeers.Count == 0 && diagnostic.LearnedEndpoints == 1 && diagnostic.AcceptedStatusDatagrams > 0 &&
                diagnostic.SentDatagrams > 0 && diagnostic.LastReceiveUtc is not null,
                "PSK diagnostics expose learned return route and real UDP counters without requiring a reciprocal configured address");
            await initiator.StopAsync();
            await Until(() => receiver.Snapshot().Count == 0);
            var sent = receiver.GetNetworkDiagnostics().SentDatagrams;
            await Task.Delay(250);
            check(receiver.GetNetworkDiagnostics() is { LearnedEndpoints: 0 } && receiver.GetNetworkDiagnostics().SentDatagrams == sent,
                "learned PSK return route expires with peer TTL and stops transmitting to the departed endpoint");
        }

        var isolatedSettings = settings with { DeviceId = Guid.NewGuid(), Port = FreePort(), DirectPeers = [] };
        await using var isolated = new PeerService(isolatedSettings);
        await isolated.StartAsync();
        using var validSender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var impostor = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var codec = new PacketCodec(key);
        using var foreign = new PacketCodec(GroupKey.Generate());
        var endpoint = new IPEndPoint(IPAddress.Loopback, isolatedSettings.Port);
        var wire = new WireAdvertisement(Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "Verified", false, null, null, null, null, false);
        await SendUntil(impostor, foreign.Encode(wire), endpoint, () => isolated.GetNetworkDiagnostics().InvalidOrRejectedDatagrams > 0);
        check(isolated.GetNetworkDiagnostics() is { LearnedEndpoints: 0, SentDatagrams: 0 } && isolated.Snapshot().Count == 0,
            "unauthenticated endpoint never receives a learned PSK return route");
        await SendUntil(impostor, codec.Encode(wire with { SentAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds() }), endpoint,
            () => isolated.GetNetworkDiagnostics().AuthenticatedClockSkewDatagrams >= 1);
        check(isolated.GetNetworkDiagnostics().LearnedEndpoints == 0 && isolated.GetNetworkDiagnostics().Hint.Contains("Zeitabweichung"),
            "authenticated stale timestamp is diagnosed without learning or accepting the peer");
        var packet = codec.Encode(wire with { SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        await SendUntil(validSender, packet, endpoint, () => isolated.Snapshot().Count == 1);
        check(await Receives(validSender, 500), "authenticated fresh sender receives periodic return heartbeat");
        var rejected = isolated.GetNetworkDiagnostics().InvalidOrRejectedDatagrams;
        await SendUntil(impostor, packet, endpoint, () => isolated.GetNetworkDiagnostics().InvalidOrRejectedDatagrams > rejected);
        check(!await Receives(impostor, 200) && await Receives(validSender, 500),
            "replayed authenticated datagram cannot move an existing peer return route to another UDP endpoint");

        var keyAuto = AutoLanIdentity.GenerateSigningKey();
        using var announcementCapture = BoundSocket();
        var automaticSettings = isolatedSettings with { DeviceId = AutoLanIdentity.GetDeviceId(keyAuto), Port = FreePort(),
            DirectPeers = [(IPEndPoint)announcementCapture.LocalEndPoint!] };
        await using var automatic = new AutoLanPeerService(automaticSettings, keyAuto);
        await automatic.StartAsync();
        using var autoCrypto = new AutoLanCrypto(keyAuto);
        var ownAnnouncement = new byte[AutoLanCrypto.MaxDatagram];
        using (var captureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            var ownSize = await announcementCapture.ReceiveAsync(ownAnnouncement, SocketFlags.None, captureTimeout.Token);
            await SendUntil(impostor, ownAnnouncement[..ownSize], new IPEndPoint(IPAddress.Loopback, automaticSettings.Port),
                () => automatic.GetNetworkDiagnostics().SelfDatagrams >= 1);
        }
        check(automatic.GetNetworkDiagnostics().Hint.Contains("keine UDP-Pakete anderer PCs") &&
            automatic.GetNetworkDiagnostics().InvalidOrRejectedDatagrams == 0,
            "authenticated AutoLAN self-broadcast is separate from other-PC reception and rejection counters");
        await SendUntil(impostor, packet, new IPEndPoint(IPAddress.Loopback, automaticSettings.Port),
            () => automatic.GetNetworkDiagnostics().OtherModeDatagrams >= 1);
        check(automatic.GetNetworkDiagnostics() is { ConnectedPeers: 0, AcceptedStatusDatagrams: 0, AcceptedHandshakes: 0 } &&
            automatic.GetNetworkDiagnostics().Hint.Contains("Absender unbestätigt") && automatic.NetworkStatus is null,
            "AutoLAN diagnoses private-mode datagrams as unverified observations without trust or a blocking network error");
        await SendUntil(impostor, autoCrypto.Hello(DateTimeOffset.UtcNow), endpoint, () => isolated.GetNetworkDiagnostics().OtherModeDatagrams >= 1);
        check(isolated.GetNetworkDiagnostics().Mode == "Private Gruppe", "PSK diagnostics distinguish incoming automatic-mode datagrams");
        await SendUntil(impostor, new byte[] { 68, 89, 78, 65, 1, 3 }, new IPEndPoint(IPAddress.Loopback, automaticSettings.Port),
            () => automatic.GetNetworkDiagnostics().InvalidOrRejectedDatagrams >= 1);
        check(automatic.GetNetworkDiagnostics().LearnedEndpoints == 0, "invalid AutoLAN status never creates a diagnostic peer or endpoint");

        using var remote = new AutoLanCrypto(AutoLanIdentity.GenerateSigningKey());
        using var remoteSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var automaticEndpoint = new IPEndPoint(IPAddress.Loopback, automaticSettings.Port);
        await SendUntil(remoteSocket, remote.Hello(DateTimeOffset.UtcNow.AddMinutes(-2)), automaticEndpoint,
            () => automatic.GetNetworkDiagnostics().AuthenticatedClockSkewDatagrams > 0);
        check(automatic.GetNetworkDiagnostics().LearnedEndpoints == 0 && automatic.GetNetworkDiagnostics().AcceptedHandshakes == 0,
            "stale signed AutoLAN announcements explain clock skew without creating trust or a return route");
        await SendUntil(remoteSocket, remote.Hello(DateTimeOffset.UtcNow), automaticEndpoint,
            () => automatic.GetNetworkDiagnostics().AcceptedHandshakes >= 1);
        using (var receiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            var bytes = new byte[4096];
            var size = await remoteSocket.ReceiveAsync(bytes, SocketFlags.None, receiveTimeout.Token);
            var challenge = AutoLanCrypto.Verify(bytes.AsSpan(0, size), DateTimeOffset.UtcNow);
            check(challenge is not null && remote.IsAckForLocal(challenge), "AutoLAN diagnostic handshake counters correspond to a verifiable signed challenge");
            var ack = remote.Ack(challenge!, DateTimeOffset.UtcNow);
            await SendUntil(remoteSocket, ack, automaticEndpoint, () => automatic.GetNetworkDiagnostics().LearnedEndpoints == 1);
            var before = automatic.GetNetworkDiagnostics();
            await SendUntil(remoteSocket, ack, automaticEndpoint, () => automatic.GetNetworkDiagnostics().ReceivedDatagrams > before.ReceivedDatagrams);
            check(automatic.GetNetworkDiagnostics().InvalidOrRejectedDatagrams == before.InvalidOrRejectedDatagrams &&
                automatic.GetNetworkDiagnostics().LearnedEndpoints == 1,
                "valid repeated AutoLAN acknowledgement retains its channel without increasing rejection count");
        }

        await SendUntil(impostor, autoCrypto.Hello(DateTimeOffset.UtcNow), automaticEndpoint,
            () => automatic.GetNetworkDiagnostics().ConflictingIdentityDatagrams > 0);
        check(automatic.GetNetworkDiagnostics().ConnectedPeers == 0 &&
            automatic.GetNetworkDiagnostics().Assess().Kind == ConnectionProblemKind.IdentityConflict,
            "same AutoLAN key from another boot session is diagnosed distinctly from the service's own broadcast");
        await SendUntil(impostor, codec.Encode(wire with { DeviceId = isolatedSettings.DeviceId,
            SessionId = Guid.NewGuid(), SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }), endpoint,
            () => isolated.GetNetworkDiagnostics().ConflictingIdentityDatagrams > 0);
        check(isolated.GetNetworkDiagnostics().ConflictingIdentityDatagrams > 0,
            "authenticated private-group packets with a duplicate local device ID are diagnosed without learning that sender");

        var deniedSettings = automaticSettings with { Port = FreePort(), LoopbackOnly = false, DirectPeers = [] };
        await using var denied = new AutoLanPeerService(deniedSettings, keyAuto, () => ([], "Test scope is closed"));
        await denied.StartAsync();
        await SendUntil(impostor, autoCrypto.Hello(DateTimeOffset.UtcNow), new IPEndPoint(IPAddress.Loopback, deniedSettings.Port),
            () => denied.GetNetworkDiagnostics().ScopeRejectedDatagrams >= 1);
        check(denied.GetNetworkDiagnostics() is { AcceptedHandshakes: 0, LearnedEndpoints: 0 },
            "AutoLAN separates out-of-scope datagrams and keeps private network trust closed");
        check(new PeerNetworkDiagnostics("Automatisch", ["192.168.1.2"], []).Hint.Contains("Ursache ist noch nicht bestimmt"),
            "missing datagrams diagnostic does not claim firewall or router isolation as a proven cause");
    }

    private static int FreePort() { using var socket = BoundSocket(); return ((IPEndPoint)socket.LocalEndPoint!).Port; }
    private static Socket BoundSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); return socket;
    }
    private static async Task<bool> Receives(Socket socket, int timeoutMs)
    {
        using var cancel = new CancellationTokenSource(timeoutMs);
        try { await socket.ReceiveAsync(new byte[4096], SocketFlags.None, cancel.Token); return true; }
        catch (OperationCanceledException) { return false; }
    }
    private static async Task SendUntil(Socket socket, byte[] packet, IPEndPoint endpoint, Func<bool> received)
    {
        // UDP offers no delivery guarantee. Model the real 100 ms heartbeat rather than
        // assuming that the first one-shot packet on a newly opened socket must arrive.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        var start = Stopwatch.StartNew();
        while (!received())
        {
            if (start.Elapsed > TimeSpan.FromSeconds(4)) throw new InvalidOperationException("UDP heartbeat was not observed.");
            await socket.SendToAsync(packet.AsMemory(), SocketFlags.None, endpoint);
            await timer.WaitForNextTickAsync();
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        var start = Stopwatch.StartNew();
        while (!condition())
        {
            if (start.Elapsed > TimeSpan.FromSeconds(4)) throw new InvalidOperationException("UDP connection condition did not become true.");
            await Task.Delay(20);
        }
    }
}
