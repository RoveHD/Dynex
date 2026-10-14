using System.Net;
using System.Net.Sockets;
using System.Text;
using Dynex.Core;

internal static class AutoLanTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        var keyA = AutoLanIdentity.GenerateSigningKey();
        var keyB = AutoLanIdentity.GenerateSigningKey();
        var idA = AutoLanIdentity.GetDeviceId(keyA);
        var idB = AutoLanIdentity.GetDeviceId(keyB);
        check(idA != idB && AutoLanIdentity.GetDeviceId(keyA) == idA, "AutoLAN identities are independent and stable across private-key reloads");
        using var cryptoA = new AutoLanCrypto(keyA);
        using var cryptoB = new AutoLanCrypto(keyB);
        var now = DateTimeOffset.UtcNow;
        var helloA = AutoLanCrypto.Verify(cryptoA.Hello(now), now)!;
        var helloB = AutoLanCrypto.Verify(cryptoB.Hello(now), now)!;
        check(helloA?.DeviceId == idA && helloB?.DeviceId == idB, "AutoLAN validates signed self-authenticating P-256 announcements");
        var spoof = cryptoA.Sign(cryptoA.Local with { DeviceId = idB, SentAtUnixMs = now.ToUnixTimeMilliseconds() }, 1);
        check(AutoLanCrypto.Verify(spoof, now) is null, "AutoLAN rejects a valid signature claiming another key-derived device ID");
        var tamperedHello = cryptoA.Hello(now); tamperedHello[^1] ^= 1;
        check(AutoLanCrypto.Verify(tamperedHello, now) is null, "AutoLAN rejects invalid ECDSA signatures");
        check(AutoLanCrypto.Verify(cryptoA.Hello(now.AddMinutes(-1)), now) is null, "AutoLAN rejects stale signed announcements");
        check(AutoLanCrypto.Verify(new byte[4096], now) is null, "AutoLAN bounds handshake datagram length");
        var ackB = AutoLanCrypto.Verify(cryptoB.Ack(helloA!, now), now)!;
        check(cryptoA.IsAckForLocal(ackB), "AutoLAN acknowledgement binds the recipient identity, session and random challenge");
        check(!cryptoA.IsAckForLocal(ackB with { TargetChallenge = cryptoB.Local.Challenge }), "AutoLAN rejects an acknowledgement for a different challenge");
        check(!cryptoA.IsAckForLocal(ackB with { TargetExchangePublicKey = cryptoB.Local.ExchangePublicKey }),
            "AutoLAN acknowledgement explicitly authenticates both ephemeral exchange public keys");
        using var restarted = new AutoLanCrypto(keyA);
        check(!restarted.IsAckForLocal(ackB), "AutoLAN restart invalidates captured acknowledgements with fresh ephemeral session/challenge");
        using var channelA = cryptoA.Derive(helloB!);
        using var channelB = cryptoB.Derive(helloA!);
        var wire = new WireAdvertisement(idA, cryptoA.Local.SessionId, 1, now.ToUnixTimeMilliseconds(), "Encrypted device", true,
            4, 25, 50, "Encrypted game", true, 20, "target-route", 1, now, 0);
        var encrypted = cryptoA.Encrypt(channelA, wire);
        check(!Encoding.UTF8.GetString(encrypted).Contains("Encrypted", StringComparison.Ordinal), "AutoLAN status names and game metadata are encrypted");
        check(cryptoB.Decrypt(channelB, encrypted, now) == wire, "independent ECDH clients derive matching directional AES-GCM keys");
        check(cryptoB.Decrypt(channelB, cryptoA.Encrypt(channelA, wire with { Sequence = 2, UploadMbps = null }), now)?.UploadMbps is null,
            "AutoLAN treats an omitted legacy upload metric as unknown");
        check(cryptoB.Decrypt(channelB, cryptoA.Encrypt(channelA, wire with { Sequence = 3, UploadMbps = 100001 }), now) is null,
            "AutoLAN rejects out-of-range authenticated upload metrics");
        check(cryptoB.Decrypt(channelB, cryptoA.Encrypt(channelA, wire with { Sequence = 4, UploadMbps = -1 }), now) is null,
            "AutoLAN rejects negative authenticated upload metrics");
        check(cryptoB.Decrypt(channelB, cryptoA.Encrypt(channelA, wire with { Sequence = 4, BudgetProposal = new(200, 91, 12) }), now) is null,
            "AutoLAN rejects authenticated invalid reserve-policy proposals");
        check(cryptoB.Decrypt(channelB, cryptoA.Encrypt(channelA, wire with { Sequence = 4, BudgetProposal = new(0, 15, 12) }), now) is null,
            "AutoLAN rejects authenticated zero-capacity proposals");
        check(cryptoB.Decrypt(channelB, encrypted, now) is null, "AutoLAN rejects replayed encrypted sequence numbers");
        var packet2 = cryptoA.Encrypt(channelA, wire with { Sequence = 5 });
        for (var i = 0; i < packet2.Length; i++)
        {
            var modified = packet2.ToArray(); modified[i] ^= 1;
            if (cryptoB.Decrypt(channelB, modified, now) is not null) throw new InvalidOperationException("AutoLAN tampering accepted at byte " + i);
        }
        check(true, "AutoLAN rejects tampering at every encrypted datagram byte");
        check(cryptoB.Decrypt(channelB, packet2, now) is not null, "invalid AutoLAN ciphertext cannot advance replay state");
        var old = cryptoA.Encrypt(channelA, wire with { Sequence = 6, SentAtUnixMs = now.AddMinutes(-1).ToUnixTimeMilliseconds() });
        check(cryptoB.Decrypt(channelB, old, now) is null, "AutoLAN rejects stale authenticated encrypted status");
        var binding = new AutoLanBinding(IPAddress.Parse("192.168.4.12"), IPAddress.Parse("255.255.255.0"), 4);
        check(binding.Contains(IPAddress.Parse("192.168.4.8")) && !binding.Contains(IPAddress.Parse("192.168.5.8")) &&
            binding.Broadcast.Equals(IPAddress.Parse("192.168.4.255")), "AutoLAN discovery fallback computes subnet broadcast and rejects off-link source scope");
        var privateId = Guid.NewGuid(); var publicId = Guid.NewGuid(); var domainId = Guid.NewGuid();
        var profiles = new Dictionary<Guid, int> { [privateId] = 1, [publicId] = 0, [domainId] = 2 };
        AutoLanNetworkScope.Candidate Candidate(Guid? id, string name) => new(id, name, binding);
        check(AutoLanNetworkScope.Select([Candidate(privateId, "Ethernet")], profiles) is { Error: null, Bindings.Length: 1 },
            "AutoLAN profile mapping admits only explicitly private active adapters");
        var publicScope = AutoLanNetworkScope.Select([Candidate(publicId, "Ethernet")], profiles);
        check(publicScope.Bindings.Length == 0 && publicScope.Error!.Contains("Ethernet: öffentliches Windows-Netzwerk"),
            "public Windows profile produces an actionable adapter-specific explanation without opening scope");
        var domainScope = AutoLanNetworkScope.Select([Candidate(domainId, "Office")], profiles);
        check(domainScope.Bindings.Length == 0 && domainScope.Error!.Contains("Domänennetzwerk"),
            "domain profile remains excluded and is distinguished from public home networks");
        var unknownScope = AutoLanNetworkScope.Select([Candidate(Guid.NewGuid(), "Wi-Fi")], profiles);
        check(unknownScope.Bindings.Length == 0 && unknownScope.Error!.Contains("Netzwerkprofil unbekannt"),
            "unmapped adapters fail closed with a distinct unknown-profile explanation");
        check(AutoLanNetworkScope.Select([], profiles).Error!.Contains("Keine aktive LAN-Verbindung"),
            "absent LAN is distinguished from a connected but untrusted Windows profile");
        check(AutoLanNetworkScope.Select([Candidate(privateId, "LAN"), Candidate(publicId, "Other LAN")], profiles).Bindings.Length == 1,
            "public adapters do not prevent discovery on a separate trusted private adapter");
        if (OperatingSystem.IsWindows())
        {
            var scope = AutoLanNetworkScope.Read();
            Console.WriteLine($"AutoLAN read-only profile probe: {scope.Bindings.Length} private IPv4 bindings; {scope.Error ?? "profile query succeeded"}");
            check(scope.Bindings.All(b => LanAddress.IsAllowed(b.Address, false)), "Windows profile probe exposes only private-scope IPv4 bindings");
        }

        var portA = FreePort(); var portB = FreePort();
        while (portB == portA) portB = FreePort();
        var settingsA = new DynexSettings
        {
            DeviceId = idA, DeviceName = "Automatic A", Port = portA, LoopbackOnly = true,
            DirectPeers = [new(IPAddress.Loopback, portB)], PeerTtl = TimeSpan.FromSeconds(1), HeartbeatInterval = TimeSpan.FromMilliseconds(150)
        };
        var settingsB = settingsA with { DeviceId = idB, DeviceName = "Automatic B", Port = portB, DirectPeers = [new(IPAddress.Loopback, portA)] };
        var scopeRecovered = false;
        await using (var recovery = new AutoLanPeerService(settingsA with { LoopbackOnly = false, DirectPeers = [], Port = FreePort() }, keyA,
            () => ([], Volatile.Read(ref scopeRecovered) ? null : "Temporary profile probe error")))
        {
            await recovery.StartAsync();
            check(recovery.NetworkStatus == "Temporary profile probe error", "AutoLAN exposes an initial profile failure");
            Volatile.Write(ref scopeRecovered, true);
            await Until(() => recovery.NetworkStatus is null, "AutoLAN clears recovered scope errors without an address change");
            check(recovery.NetworkStatus is null, "recovered discovery no longer leaves speedtest readiness blocked by a stale status");
        }
        try { using var invalid = new AutoLanPeerService(settingsA with { DeviceId = idB }, keyA); check(false, "AutoLAN mismatched identity"); }
        catch (ArgumentException) { check(true, "AutoLAN rejects mismatched persistent private key and configured device ID"); }
        await using var a = new AutoLanPeerService(settingsA, keyA);
        await using var b = new AutoLanPeerService(settingsB, keyB);
        b.Publish(true, 2, 90, null, "Live AutoLAN Game", false, 20, "target-route", now, 0);
        await a.StartAsync(); await b.StartAsync();
        await Until(() => a.Snapshot().Count == 1 && b.Snapshot().Count == 1, "two independently initialized AutoLAN clients exchange UDP encrypted status");
        var received = a.Snapshot().Single();
        check(received.DeviceId == idB && received.IsGaming && received.Game == "Live AutoLAN Game" && received.IdleBaselineMs == 20,
            "two real UDP AutoLAN peers exchange encrypted gaming status without any shared key");
        check(received.UploadMbps == 0, "AutoLAN peers exchange zero upload distinctly");
        check(received.BudgetProposal == DownloadBudgetProposal.FromSettings(settingsB),
            "AutoLAN encrypted status advertises original local capacity and reserve settings");
        try { b.Publish(false, uploadMbps: double.PositiveInfinity); check(false, "invalid AutoLAN upload rejected"); }
        catch (ArgumentException) { check(true, "AutoLAN rejects non-finite upload values before publishing"); }
        check(a.NetworkStatus is null && b.NetworkStatus is null, "AutoLAN loopback discovery has no transport errors");
        var sampleSequence = received.LatencySampleSequence;
        var seen = received.LastSeenUtc;
        await Until(() => a.Snapshot().Single().LastSeenUtc > seen, "next encrypted heartbeat");
        check(a.Snapshot().Single().LatencySampleSequence == sampleSequence, "multiple AutoLAN heartbeats preserve one published latency sample ID");
        b.Publish(true, 2, 30, null, "Live AutoLAN Game", false, 20, "target-route", now, 10);
        await Until(() => a.Snapshot().Single().LatencySampleSequence > sampleSequence, "new published latency sample");
        check(a.Snapshot().Single().LatencyMs == 30, "new Publish advances AutoLAN feedback sample sequence");
        check(a.Snapshot().Single().UploadMbps == 10, "AutoLAN peers receive updated upload rate");
        b.Publish(true, 2, 30, null, "Live AutoLAN Game", false, 20, "target-route", now, null);
        await Until(() => a.Snapshot().Single().UploadMbps is null, "AutoLAN missing upload update");
        check(a.Snapshot().Single().UploadMbps is null, "AutoLAN peers receive null upload updates");
        b.Publish(true, 2, 30, null, "Live AutoLAN Game", false, 20, "target-route", now, 10);
        await Until(() => a.Snapshot().Single().UploadMbps == 10, "AutoLAN throughput refresh before expiry");
        var autoThroughputSequence = a.Snapshot().Single().LatencySampleSequence;
        await Until(() => a.Snapshot().Single().DownloadMbps is null && a.Snapshot().Single().UploadMbps is null,
            "AutoLAN published throughput expires after peer TTL");
        check(a.Snapshot().Single().LatencySampleSequence == autoThroughputSequence,
            "AutoLAN throughput expiry preserves the published latency sample ID");
        check(a.Snapshot().Single().BudgetProposal is null, "AutoLAN heartbeat stops advertising capacity policy after Publish goes stale");
        b.Publish(true, 2, 30, null, "Live AutoLAN Game", false, 20, "target-route", now, 10);
        await Until(() => a.Snapshot().Single().UploadMbps == 10, "AutoLAN throughput resumes after fresh publish");
        var planner = new LimitPlanner(settingsA);
        check(planner.Plan(false, a.Snapshot()).DownloadLimitMbps is not null, "AutoLAN gaming state activates the local download budget");
        await b.StopAsync();
        await Until(() => a.Snapshot().Count == 0, "AutoLAN peer TTL expiry");
        check(planner.Plan(false, a.Snapshot()).DownloadLimitMbps is null, "AutoLAN peer loss expires and releases the automatic limit");
        using var attack = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        foreach (var invalid in new[] { spoof, tamperedHello, cryptoA.Hello(now.AddMinutes(-1)), new byte[4096] })
            await attack.SendToAsync(invalid, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, portA));
        await Task.Delay(200);
        check(a.Snapshot().Count == 0, "live AutoLAN receiver ignores key-ID spoofing, invalid signatures, stale announcements and oversized datagrams");
        await attack.SendToAsync(cryptoB.Hello(DateTimeOffset.UtcNow), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, portA));
        var replyBuffer = new byte[AutoLanCrypto.MaxDatagram + 1];
        using var replyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var reply = await attack.ReceiveFromAsync(replyBuffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), replyTimeout.Token);
        var serviceAck = AutoLanCrypto.Verify(replyBuffer.AsSpan(0, reply.ReceivedBytes), DateTimeOffset.UtcNow)!;
        check(serviceAck is not null && cryptoB.IsAckForLocal(serviceAck), "live AutoLAN challenge binds a separately implemented peer to its actual session");
        using var liveFakeChannel = cryptoB.Derive(serviceAck!);
        await attack.SendToAsync(cryptoB.Ack(serviceAck!, DateTimeOffset.UtcNow), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, portA));
        var fakeWire = wire with { DeviceId = idB, SessionId = cryptoB.Local.SessionId, DeviceName = "Captured status", Sequence = 1,
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        var capturedStatus = cryptoB.Encrypt(liveFakeChannel, fakeWire);
        await attack.SendToAsync(capturedStatus, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, portA));
        await Until(() => a.Snapshot().SingleOrDefault()?.DeviceName == "Captured status", "live challenge peer ciphertext accepted");
        await Until(() => a.Snapshot().Count == 0, "live captured peer expires");
        await attack.SendToAsync(capturedStatus, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, portA));
        await Task.Delay(180);
        check(a.Snapshot().Count == 0, "live AutoLAN retains replay state after peer TTL so captured status cannot reactivate gaming");
        await b.StartAsync();
        await Until(() => a.Snapshot().Count == 1 && b.Snapshot().Count == 1, "AutoLAN peer restart reconnects with a fresh handshake");
        check(a.Snapshot().Single().DeviceId == idB && a.Snapshot().Single().SessionId != received.SessionId && a.Snapshot().Single().SessionId is not null,
            "AutoLAN reconnects the same persistent identity with a new authenticated session ID");
        await a.StopAsync();
        check(a.Snapshot().Count == 0, "AutoLAN stop immediately clears remote gaming state");
        await b.StopAsync();
        await using var unmeasured = new AutoLanPeerService(settingsB with { CapacityMeasured = false }, keyB);
        unmeasured.Publish(true, 1, game: "Unmeasured AutoLAN game");
        await a.StartAsync(); await unmeasured.StartAsync();
        await Until(() => a.Snapshot().Count == 1, "unmeasured AutoLAN peer received");
        check(a.Snapshot().Single() is { IsGaming: true, DownloadMbps: 1, BudgetProposal: null },
            "fresh unmeasured AutoLAN peer publishes gaming and throughput without a default budget proposal");

    }

    private static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException(message);
            await Task.Delay(25);
        }
    }
}
