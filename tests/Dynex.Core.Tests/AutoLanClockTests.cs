using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Dynex.Core;

internal static class AutoLanClockTests
{
    internal static async Task Run(Action<bool, string> check, bool cryptoOnly = false)
    {
        var keyA = AutoLanIdentity.GenerateSigningKey();
        var keyB = AutoLanIdentity.GenerateSigningKey();
        using var a = new AutoLanCrypto(keyA);
        using var b = new AutoLanCrypto(keyB);
        var now = DateTimeOffset.UtcNow;
        var tick = Stopwatch.GetTimestamp();
        long After(double seconds) => tick + (long)(seconds * Stopwatch.Frequency);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 40123);
        foreach (var minutes in new[] { -17, 23 })
        {
            var remoteNow = now.AddMinutes(minutes);
            var hello = AutoLanCrypto.VerifySignature(b.Hello(remoteNow))!;
            check(hello is not null && AutoLanCrypto.Verify(b.Hello(remoteNow), now) is null,
                $"clock {minutes:+#;-#}m: signature authentication alone does not pass the legacy UTC freshness check");
            var challenges = new AutoLanChallenges();
            var nonce = challenges.Issue(hello!, endpoint, tick)!;
            var request = AutoLanCrypto.VerifySignature(a.Ack(hello!, now, nonce))!;
            var signedProof = b.Ack(request, remoteNow, statusSequence: 7);
            var proof = AutoLanCrypto.VerifySignature(signedProof)!;
            check(a.IsAckForLocal(proof) && !challenges.Consume(proof, new(IPAddress.Loopback, endpoint.Port + 1), tick),
                "fresh proof is bound to its recipient session AND actual source endpoint");
            check(!challenges.Consume(proof with { ExchangePublicKey = a.Local.ExchangePublicKey }, endpoint, tick) &&
                !challenges.Consume(proof with { SessionId = Guid.NewGuid() }, endpoint, tick) &&
                !challenges.Consume(proof with { TargetFreshnessChallenge = a.Local.Challenge }, endpoint, tick),
                "wrong ephemeral key, session and process-lifetime challenge cannot consume a fresh pending nonce");
            var forged = signedProof.ToArray(); forged[^1] ^= 1;
            check(AutoLanCrypto.VerifySignature(forged) is null && challenges.Count == 1,
                "forged proof cannot authenticate or consume the pending challenge");
            check(challenges.Consume(proof, endpoint, After(1)) && !challenges.Consume(proof, endpoint, After(1)),
                "fresh challenge is consumed once; a replay cannot authorize another anchor or route");
            using var receiver = a.Derive(hello!);
            using var sender = b.Derive(a.Local);
            receiver.ConfirmFreshness(proof, tick);
            var wire = new WireAdvertisement(b.Local.DeviceId, b.Local.SessionId, 8, remoteNow.ToUnixTimeMilliseconds(), "Clock peer", true,
                null, null, null, null, false);
            var encrypted = b.Encrypt(sender, wire);
            var decoded = a.Decrypt(receiver, encrypted, now, out _, tick);
            check(decoded is { IsGaming: true, DownloadMbps: null, UploadMbps: null },
                $"clock {minutes:+#;-#}m: anchored encrypted status works and missing throughput stays unknown");
            check(a.Decrypt(receiver, encrypted, now, out _, tick) is null,
                "clock anchoring retains encrypted sequence replay rejection");
            var stale = b.Encrypt(sender, wire with { Sequence = 9 });
            check(a.Decrypt(receiver, stale, now, out var skew, After(31)) is null && skew,
                "anchored status expires after the unchanged 30-second monotonic freshness window");
            check(a.Decrypt(receiver, stale, now.AddYears(2), out _, After(2)) is not null,
                "receiver wall-clock changes cannot alter the anchored monotonic freshness decision");
            receiver.ConfirmFreshness(proof with { StatusSequence = 20, SentAtUnixMs = remoteNow.AddSeconds(40).ToUnixTimeMilliseconds() }, After(40));
            check(a.Decrypt(receiver, b.Encrypt(sender, wire with { Sequence = 19, SentAtUnixMs = remoteNow.AddSeconds(40).ToUnixTimeMilliseconds() }), now, out _, After(40)) is null,
                "a fresh signed sequence floor excludes withheld ciphertext produced before re-anchoring");
            receiver.ConfirmFreshness(proof with { StatusSequence = 1 }, After(41));
            check(receiver.LastReceivedSequence == 20, "re-anchoring never lowers the replay floor");
            var expired = new AutoLanChallenges();
            var expiryNonce = expired.Issue(hello!, endpoint, tick);
            check(!expired.Consume(proof with { TargetFreshnessChallenge = expiryNonce }, endpoint, After(10)) && expired.Count == 0,
                "pending proof expires at its monotonic deadline even if both wall clocks are wrong");
            var nextNonce = expired.Issue(hello!, endpoint, After(11));
            check(nextNonce != expiryNonce && !expired.Consume(proof with { TargetFreshnessChallenge = expiryNonce }, endpoint, After(11)),
                "expired challenges are replaced with unpredictable nonces; captured proof cannot reopen a session");
            expired.Clear();
            check(!expired.Consume(proof with { TargetFreshnessChallenge = nextNonce }, endpoint, After(11)),
                "closing network scope clears pending authorization");
        }
        var bounded = new AutoLanChallenges();
        for (var i = 0; i < 128; i++) bounded.Issue(b.Local, new(IPAddress.Loopback, 41000 + i), tick);
        check(bounded.Count == 128 && bounded.Issue(b.Local, new(IPAddress.Loopback, 42000), tick) is null,
            "unproven signed discovery is bounded to 128 pending endpoint challenges");
        bounded.Expire(After(10));
        check(bounded.Count == 0, "expired pending capacity is reclaimed without wall-clock dependence");
        var legacy = b.Local with { FreshnessChallenge = null, TargetFreshnessChallenge = null, StatusSequence = null, SentAtUnixMs = now.ToUnixTimeMilliseconds() };
        check(AutoLanCrypto.Verify(b.Sign(legacy, 1), now) is not null &&
            AutoLanCrypto.Verify(b.Sign(legacy with { SentAtUnixMs = now.AddMinutes(-2).ToUnixTimeMilliseconds() }, 1), now) is null,
            "legacy v1 announcements remain interoperable only inside the original UTC window");
        using var restarted = new AutoLanCrypto(keyA);
        var oldAck = AutoLanCrypto.VerifySignature(b.Ack(a.Local, now))!;
        check(!restarted.IsAckForLocal(oldAck), "new local cryptographic session rejects old challenge acknowledgements");
        ServiceHandshake(check, keyA, keyB);
        if (cryptoOnly) return;
        await Live(check, keyA, keyB);
    }

    private static void ServiceHandshake(Action<bool, string> check, string keyA, string keyB)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        static object Field(object value, string name) => value.GetType().GetField(name, flags)!.GetValue(value)!;
        static void Set(object value, string name, object? replacement) => value.GetType().GetField(name, flags)!.SetValue(value, replacement);
        static object? Call(object value, string method, params object[] parameters) => value.GetType().GetMethod(method, flags)!.Invoke(value, parameters);
        static AutoChannel Channel(AutoLanPeerService service, Guid remote) =>
            (AutoChannel)((System.Collections.IDictionary)Field(service, "_channels"))[remote]!.GetType().GetProperty("Crypto", flags)!
                .GetValue(((System.Collections.IDictionary)Field(service, "_channels"))[remote])!;
        var settingsA = new DynexSettings { DeviceId = AutoLanIdentity.GetDeviceId(keyA), DeviceName = "A", Port = 40111, LoopbackOnly = true };
        var settingsB = settingsA with { DeviceId = AutoLanIdentity.GetDeviceId(keyB), DeviceName = "B", Port = 40112 };
        using var a = new AutoLanPeerService(settingsA, keyA);
        using var b = new AutoLanPeerService(settingsB, keyB);
        using var cryptoA = new AutoLanCrypto(keyA);
        using var cryptoB = new AutoLanCrypto(keyB);
        Set(a, "_crypto", cryptoA); Set(b, "_crypto", cryptoB);
        var nowA = DateTimeOffset.UtcNow.AddMinutes(-17);
        var nowB = DateTimeOffset.UtcNow.AddMinutes(23);
        var tick = Stopwatch.GetTimestamp();
        var endpointA = new IPEndPoint(IPAddress.Loopback, settingsA.Port);
        var endpointB = new IPEndPoint(IPAddress.Loopback, settingsB.Port);
        byte[]? Deliver(AutoLanPeerService receiver, byte[] bytes, IPEndPoint source, DateTimeOffset now) =>
            (byte[]?)Call(receiver, "HandshakeLocked", AutoLanCrypto.VerifySignature(bytes)!, bytes[5], source, now, tick);
        var first = Deliver(b, cryptoA.Hello(nowA), endpointA, nowB)!;
        check(b.GetNetworkDiagnostics().LearnedEndpoints == 0, "service learns no route from a signed skewed announcement alone");
        var second = Deliver(a, first, endpointB, nowA)!;
        check(a.GetNetworkDiagnostics().LearnedEndpoints == 0, "service learns no route from an acknowledgement without its own active challenge");
        var third = Deliver(b, second, endpointA, nowB)!;
        check(b.GetNetworkDiagnostics().LearnedEndpoints == 1, "service installs the source route only after consuming its fresh echoed nonce");
        byte[]? packet = Deliver(a, third, endpointB, nowA);
        var toB = true;
        var responses = 0;
        while (packet is not null && responses++ < 10)
        {
            packet = toB ? Deliver(b, packet, endpointA, nowB) : Deliver(a, packet, endpointB, nowA);
            toB = !toB;
        }
        check(packet is null && responses <= 3 && a.GetNetworkDiagnostics().LearnedEndpoints == 1,
            "actual service handshake terminates its reciprocal acknowledgements without nonce bounce");
        var retryNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var retryRequest = cryptoB.Ack(cryptoA.Local, nowB, retryNonce);
        var retryReply = Deliver(a, retryRequest, endpointB, nowA);
        check(retryReply is not null && Deliver(a, retryRequest, endpointB, nowA) is not null &&
            Deliver(b, retryReply, endpointA, nowB) is null,
            "a lost directed challenge reply can be retransmitted without starting an acknowledgement loop");
        var wire = new WireAdvertisement(cryptoB.Local.DeviceId, cryptoB.Local.SessionId, 1, nowB.ToUnixTimeMilliseconds(), "B", true, null, null, null, null, false);
        check(cryptoA.Decrypt(Channel(a, settingsB.DeviceId), cryptoB.Encrypt(Channel(b, settingsA.DeviceId), wire), nowA, out _, tick) is not null,
            "actual service-derived session keys and remote clock anchor accept skewed encrypted status");
        var redirect = new IPEndPoint(IPAddress.Loopback, endpointB.Port + 1);
        Deliver(a, third, redirect, nowA);
        var storedSession = ((System.Collections.IDictionary)Field(a, "_channels"))[settingsB.DeviceId]!;
        check(((IPEndPoint)storedSession.GetType().GetProperty("Endpoint", flags)!.GetValue(storedSession)!).Equals(endpointB),
            "replayed valid proof from a different endpoint cannot redirect an established route");
        tick += 601 * Stopwatch.Frequency;
        Call(b, "ExpireLocked", tick);
        Deliver(b, second, endpointA, nowB);
        check(b.GetNetworkDiagnostics().LearnedEndpoints == 0,
            "after channel and retired-session retention expire, captured proof still requires a NEW active challenge");
        Set(a, "_clockSkew", 3L);
        Set(a, "_clockSkewAt", Stopwatch.GetTimestamp() - 31 * Stopwatch.Frequency);
        check(a.GetNetworkDiagnostics().AuthenticatedClockSkewDatagrams == 0,
            "a historical authenticated skew rejection stops overriding current connection diagnostics");
        var oldSession = cryptoA.Local.SessionId;
        Set(a, "_clockAnchor", ((DateTimeOffset Utc, long Tick)?)(nowA, tick));
        check((bool)Call(a, "RenewAfterClockChangeLocked", nowA.AddMinutes(-5), tick + Stopwatch.Frequency)!,
            "actual service detects a backward wall-clock step using its monotonic anchor");
        var replacement = (AutoLanCrypto)Field(a, "_crypto");
        check(replacement.Local.SessionId != oldSession && a.GetNetworkDiagnostics().LearnedEndpoints == 0 &&
            !replacement.IsAckForLocal(AutoLanCrypto.VerifySignature(third)!),
            "clock step clears old routes and pending proofs and replaces the cryptographic session");
        replacement.Dispose();

        // These packets are genuinely signed in the original pre-extension JSON shape.
        // The legacy receiver verifies the signature over raw bytes before ignoring unknown fields.
        foreach (var oldInitiates in new[] { false, true })
        {
            using var modern = new AutoLanPeerService(settingsA, keyA);
            using var modernCrypto = new AutoLanCrypto(keyA);
            using var oldCrypto = new AutoLanCrypto(keyB);
            Set(modern, "_crypto", modernCrypto);
            var now = DateTimeOffset.UtcNow;
            var oldHello = oldCrypto.Local with { FreshnessChallenge = null, TargetFreshnessChallenge = null, StatusSequence = null,
                SentAtUnixMs = now.ToUnixTimeMilliseconds() };
            var received = oldInitiates
                ? Deliver(modern, oldCrypto.Sign(oldHello, 1), endpointB, now)!
                : modernCrypto.Hello(now);
            var legacyParsed = LegacyVerify(received)!;
            var oldAck = oldHello with { TargetId = legacyParsed.DeviceId, TargetSession = legacyParsed.SessionId,
                TargetChallenge = legacyParsed.Challenge, TargetExchangePublicKey = legacyParsed.ExchangePublicKey };
            Deliver(modern, oldCrypto.Sign(oldAck, 2), endpointB, now);
            check(modern.GetNetworkDiagnostics().LearnedEndpoints == 1 && legacyParsed.FreshnessChallenge is null,
                $"legacy original JSON shape interoperates when {(oldInitiates ? "old" : "new")} peer initiates discovery");
            using var oldChannel = oldCrypto.Derive(legacyParsed);
            var oldWire = wire with { SessionId = oldCrypto.Local.SessionId, SentAtUnixMs = now.ToUnixTimeMilliseconds() };
            check(modernCrypto.Decrypt(Channel(modern, settingsB.DeviceId), oldCrypto.Encrypt(oldChannel, oldWire), now) is not null &&
                modernCrypto.Decrypt(Channel(modern, settingsB.DeviceId), oldCrypto.Encrypt(oldChannel, oldWire with { Sequence = 2,
                    SentAtUnixMs = now.AddMinutes(2).ToUnixTimeMilliseconds() }), now) is null,
                "legacy session exchanges encrypted status and retains strict UTC freshness without learning an offset");
            Channel(modern, settingsB.DeviceId).Dispose();
        }
    }

    private static AutoHello? LegacyVerify(byte[] packet)
    {
        var old = JsonSerializer.Deserialize<LegacyHello>(packet.AsSpan(8, packet.Length - 72))!;
        using var signing = ECDsa.Create();
        signing.ImportSubjectPublicKeyInfo(Convert.FromBase64String(old.SigningPublicKey), out _);
        if (!signing.VerifyData(packet.AsSpan(0, packet.Length - 64), packet.AsSpan(packet.Length - 64), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return null;
        return new(old.DeviceId, old.SessionId, old.SigningPublicKey, old.ExchangePublicKey, old.Challenge, old.SentAtUnixMs,
            old.TargetId, old.TargetSession, old.TargetChallenge, old.TargetExchangePublicKey);
    }

    private sealed record LegacyHello(Guid DeviceId, Guid SessionId, string SigningPublicKey, string ExchangePublicKey,
        string Challenge, long SentAtUnixMs, Guid? TargetId = null, Guid? TargetSession = null, string? TargetChallenge = null,
        string? TargetExchangePublicKey = null);

    private static async Task Live(Action<bool, string> check, string keyA, string keyB)
    {
        var portA = FreePort();
        var portB = FreePort();
        while (portB == portA) portB = FreePort();
        var settingsA = new DynexSettings
        {
            DeviceId = AutoLanIdentity.GetDeviceId(keyA), DeviceName = "Clock A", Port = portA, LoopbackOnly = true,
            DirectPeers = [new(IPAddress.Loopback, portB)], PeerTtl = TimeSpan.FromSeconds(1), HeartbeatInterval = TimeSpan.FromMilliseconds(200)
        };
        var settingsB = settingsA with { DeviceId = AutoLanIdentity.GetDeviceId(keyB), DeviceName = "Clock B", Port = portB, DirectPeers = [] };
        long offsetA = TimeSpan.FromMinutes(-17).Ticks, offsetB = TimeSpan.FromMinutes(23).Ticks;
        await using var a = new AutoLanPeerService(settingsA, keyA, () => ([], null), false,
            utcNow: () => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref offsetA)));
        await using var b = new AutoLanPeerService(settingsB, keyB, () => ([], null), false,
            utcNow: () => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref offsetB)));
        a.Publish(true); b.Publish(true);
        await a.StartAsync(); await b.StartAsync();
        try { await Until(() => a.Snapshot().Count == 1 && b.Snapshot().Count == 1, "clock-skewed live handshake"); }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"{error.Message}; A: {a.GetNetworkDiagnostics()}; B: {b.GetNetworkDiagnostics()}", error);
        }
        check(a.Snapshot().Single().IsGaming && b.Snapshot().Single().IsGaming &&
            a.GetNetworkDiagnostics().LearnedEndpoints == 1 && b.GetNetworkDiagnostics().LearnedEndpoints == 1,
            "live UDP peers with -17/+23 minute clocks establish both encrypted status and a one-sided discovery return route");
        var sessionA = b.Snapshot().Single().SessionId;
        var sessionB = a.Snapshot().Single().SessionId;
        Interlocked.Exchange(ref offsetA, TimeSpan.FromMinutes(19).Ticks);
        await Until(() => b.Snapshot().SingleOrDefault()?.SessionId is { } id && id != sessionA && a.Snapshot().Count == 1,
            "local forward clock jump rekeys");
        check(b.Snapshot().Single().SessionId != sessionA, "live forward clock jump rotates the local cryptographic session and reconnects");
        Interlocked.Exchange(ref offsetB, TimeSpan.FromMinutes(-29).Ticks);
        await Until(() => a.Snapshot().SingleOrDefault()?.SessionId is { } id && id != sessionB && b.Snapshot().Count == 1,
            "local backward clock jump rekeys");
        check(a.Snapshot().Single().SessionId != sessionB, "live backward clock jump rotates the local cryptographic session and reconnects");
        check(a.GetNetworkDiagnostics().AuthenticatedClockSkewDatagrams == 0 && b.GetNetworkDiagnostics().AuthenticatedClockSkewDatagrams == 0,
            "successful freshness proof clears an obsolete clock-skew diagnosis");
        await b.StopAsync();
        await Until(() => a.Snapshot().Count == 0, "clock-skewed peer TTL");
        check(a.Snapshot().Count == 0, "peer TTL remains local and monotonic despite divergent clocks");
        await b.StartAsync();
        await Until(() => a.Snapshot().Count == 1 && b.Snapshot().Count == 1, "restart after clock-skewed TTL");
        check(a.Snapshot().Single().DownloadMbps is null && a.Snapshot().Single().UploadMbps is null,
            "reconnected skewed peer never invents numeric zero for absent telemetry");
        await a.StopAsync(); await b.StopAsync();

        await using var closedScope = new AutoLanPeerService(settingsA with { LoopbackOnly = false, DirectPeers = [] }, keyA,
            () => ([], null), false, lowPowerDiscovery: true, utcNow: () => DateTimeOffset.UtcNow.AddHours(1));
        await closedScope.StartAsync();
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var remote = new AutoLanCrypto(keyB);
        await sender.SendToAsync(remote.Hello(DateTimeOffset.UtcNow.AddHours(-1)), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, portA));
        await Until(() => closedScope.GetNetworkDiagnostics().ScopeRejectedDatagrams > 0, "closed scope rejection");
        check(closedScope.GetNetworkDiagnostics().LearnedEndpoints == 0 && closedScope.GetNetworkDiagnostics().AcceptedHandshakes == 0,
            "out-of-scope signed skewed discovery cannot solicit a challenge or learn a route");
    }

    private static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(deadline) > TimeSpan.FromSeconds(8)) throw new TimeoutException(message);
            await Task.Delay(25);
        }
    }
}
