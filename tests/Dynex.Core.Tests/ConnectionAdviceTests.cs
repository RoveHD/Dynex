using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dynex.Core;

internal static class ConnectionAdviceTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        // Pure cause selection: each problem names one observed cause and one concrete step.
        var lan = new[] { "192.168.1.2" };
        check(new PeerNetworkDiagnostics("Automatisch", [], []).Assess() is { Kind: ConnectionProblemKind.NoPrivateNetwork } noPrivate &&
            noPrivate.NextStep.Contains("Privat"), "missing private network names the Windows profile step");
        check(new PeerNetworkDiagnostics("Automatisch", lan, []).Assess(firewallConfigured: false) is { Kind: ConnectionProblemKind.FirewallMissing } firewall &&
            firewall.NextStep.Contains("Setup"), "missing firewall rule points to re-running setup");
        check(new PeerNetworkDiagnostics("Automatisch", lan, []).Assess() is { Kind: ConnectionProblemKind.NoResponse } silent &&
            silent.Cause.Contains("antwortet nicht") && silent.NextStep.Contains("Gruppencode"), "silent peer is reported as not responding with a fallback step");
        check(new PeerNetworkDiagnostics("Automatisch", lan, [], LostPeerName: "PC 2", LostPeerAtUtc: DateTimeOffset.UtcNow).Assess() is
            { Kind: ConnectionProblemKind.PeerLost } lost && lost.Cause.Contains("PC 2") && lost.NextStep.Contains("Standby"),
            "a departed peer is named and the automatic reconnect is explained");
        var older = new PeerNetworkDiagnostics("Automatisch", lan, [], ReceivedDatagrams: 3, AcceptedHandshakes: 1, LocalVersion: "1.0.6",
            RemoteVersions: [new(Guid.NewGuid(), "PC 2", RemoteVersion.BeforeVersionAnnouncement)]).Assess();
        check(older is { Kind: ConnectionProblemKind.VersionMismatch } && older.NextStep.Contains("„PC 2“ auf Version 1.0.6"),
            "older remote version asks to update the other PC");
        var newer = new PeerNetworkDiagnostics("Automatisch", lan, [], LocalVersion: "1.0.6",
            RemoteVersions: [new(Guid.NewGuid(), "PC 2", "1.1.0")]).Assess();
        check(newer is { Kind: ConnectionProblemKind.VersionMismatch } && newer.NextStep.Contains("auf diesem PC aktualisieren"),
            "newer remote version asks to update this PC");
        check(new PeerNetworkDiagnostics("Automatisch", lan, [], ConnectedPeers: 1, LocalVersion: "1.0.6",
            RemoteVersions: [new(Guid.NewGuid(), "PC 2", "1.0.6")]).Assess() is { IsProblem: false },
            "matching versions with a connected peer are not a problem");
        check(new PeerNetworkDiagnostics("Automatisch", lan, [], UnsupportedProtocolDatagrams: 1).Assess().Kind == ConnectionProblemKind.VersionMismatch,
            "an unsupported wire protocol version is explained as a version mismatch");
        check(new PeerNetworkDiagnostics("Automatisch", lan, [], ReceiveError: "Netzwerk ist nicht erreichbar").Assess().Kind == ConnectionProblemKind.ReceiveError,
            "a transient receive error is shown with an automatic retry note");
        check(new PeerNetworkDiagnostics("Automatisch", lan, []).Hint.Contains("Nächster Schritt:"), "the compact hint always carries the next step");

        // Signed version announcement.
        var key = AutoLanIdentity.GenerateSigningKey();
        using var versioned = new AutoLanCrypto(key, "1.0.6");
        var now = DateTimeOffset.UtcNow;
        check(AutoLanCrypto.Verify(versioned.Hello(now), now)?.AppVersion == "1.0.6", "AutoLAN hello carries the signed app version");
        using var legacy = new AutoLanCrypto(key);
        check(AutoLanCrypto.Verify(legacy.Hello(now), now) is { AppVersion: null }, "legacy hello without version remains valid");
        using var malformed = new AutoLanCrypto(key, "1.0.6<x>");
        check(AutoLanCrypto.Verify(malformed.Hello(now), now) is null, "malformed announced versions are rejected");
        check(!System.Text.Encoding.UTF8.GetString(legacy.Hello(now)).Contains("AppVersion"), "legacy-shaped hello omits the version property");

        // Two real AutoLAN services on loopback: version exchange, then departure.
        var keyA = AutoLanIdentity.GenerateSigningKey(); var keyB = AutoLanIdentity.GenerateSigningKey();
        var portA = FreePort(); var portB = FreePort();
        var settingsA = new DynexSettings
        {
            DeviceId = AutoLanIdentity.GetDeviceId(keyA), DeviceName = "PC 1", Port = portA, LoopbackOnly = true, AppVersion = "1.0.6",
            DirectPeers = [new(IPAddress.Loopback, portB)], PeerTtl = TimeSpan.FromSeconds(1), HeartbeatInterval = TimeSpan.FromMilliseconds(150)
        };
        var settingsB = settingsA with { DeviceId = AutoLanIdentity.GetDeviceId(keyB), DeviceName = "PC 2", Port = portB, AppVersion = "1.0.5",
            DirectPeers = [new(IPAddress.Loopback, portA)] };
        await using var a = new AutoLanPeerService(settingsA, keyA);
        var b = new AutoLanPeerService(settingsB, keyB);
        await a.StartAsync(); await b.StartAsync();
        await Until(() => a.Snapshot().Count == 1 && a.Snapshot()[0].AppVersion == "1.0.5");
        var connected = a.GetNetworkDiagnostics();
        check(connected.Assess() is { Kind: ConnectionProblemKind.VersionMismatch } mismatch && mismatch.Cause.Contains("„PC 2“ 1.0.5"),
            "connected peers expose a version mismatch with the remote PC name");
        await b.DisposeAsync();
        await Until(() => a.Snapshot().Count == 0);
        check(a.GetNetworkDiagnostics() is { LostPeerName: "PC 2" }, "peer TTL expiry records which PC stopped answering");
        b = new AutoLanPeerService(settingsB with { AppVersion = "1.0.6" }, keyB);
        await b.StartAsync();
        await Until(() => a.Snapshot().Count == 1 && a.Snapshot()[0].AppVersion == "1.0.6");
        check(a.GetNetworkDiagnostics() is { LostPeerName: null } restored && !restored.Assess().IsProblem,
            "a restarted peer reconnects automatically and clears the lost/mismatch state");
        await b.DisposeAsync();

        // A future protocol version byte is counted separately from generic rejection.
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var future = new byte[80]; "DYNA"u8.CopyTo(future); future[4] = 2; future[5] = 1;
        var start = Stopwatch.StartNew();
        while (a.GetNetworkDiagnostics().UnsupportedProtocolDatagrams == 0 && start.Elapsed < TimeSpan.FromSeconds(4))
        { await sender.SendToAsync(future, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, portA)); await Task.Delay(100); }
        check(a.GetNetworkDiagnostics().UnsupportedProtocolDatagrams > 0, "future AutoLAN protocol datagrams are counted as unsupported");
    }

    private static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
    private static async Task Until(Func<bool> condition)
    {
        var start = Stopwatch.StartNew();
        while (!condition())
        {
            if (start.Elapsed > TimeSpan.FromSeconds(6)) throw new InvalidOperationException("AutoLAN condition did not become true.");
            await Task.Delay(20);
        }
    }
}

internal static class LimitExplanationTests
{
    internal static void Run(Action<bool, string> check)
    {
        var none = new LimitDecision(null, "", false, null, false);
        check(LimitExplanation.Describe(none, false, null, [], true, false, true, false, null, null) == "Kein Spiel im Fokus · kein Limit",
            "idle network explains why no limit is active");
        var shared = new LimitDecision(16, "", true, null, false, ParticipantCount: 3, GamingCount: 1);
        PeerAdvertisement Peer(string name, bool gaming, string? game = null) => new(Guid.NewGuid(), name, gaming, null, null, DateTimeOffset.UtcNow, Game: game);
        check(LimitExplanation.Describe(shared, false, null, [Peer("PC 2", true, "Valorant"), Peer("PC 3", false)], true, false, true, true, null, 16)
            == "Gaming auf PC 2 (Valorant) erkannt · Budget geteilt auf 3 PCs", "remote gamer is named as the limit reason");
        check(LimitExplanation.Describe(shared with { AdaptiveReduced = true }, true, "CS2", [Peer("PC 2", true)], true, false, true, true, null, 16)
            .StartsWith("Gaming auf diesem PC (CS2) und PC 2 erkannt · Ping gestiegen"), "several gamers and adaptive reduction are listed");
        check(LimitExplanation.Describe(shared with { DownloadLimitMbps = 5 }, true, null, [], true, false, true, true, 5, 5).Contains("manuelle Obergrenze"),
            "manual cap is identified as the effective reason");
        check(LimitExplanation.Describe(shared, true, null, [], false, false, true, false, null, null).Contains("Speedtest"),
            "missing capacity measurement is explained");
        check(LimitExplanation.Describe(shared, true, null, [], true, false, true, false, null, null).Contains("noch aktiviert"),
            "planned but unconfirmed limit is not presented as active");
        check(LimitExplanation.Describe(shared, true, null, [], true, true, true, false, null, null).Contains("pausiert"),
            "speed test pause takes precedence");
    }
}

internal static class StatusNotificationTests
{
    internal static void Run(Action<bool, string> check)
    {
        var notes = new StatusNotifications(TimeSpan.FromSeconds(60));
        var t = DateTimeOffset.UtcNow;
        check(notes.Observe(["PC 2"], false, "", null, t).Count == 0, "first snapshot only sets the baseline");
        check(notes.Observe(["PC 2"], false, "", null, t.AddSeconds(2)).Count == 0, "unchanged status does not notify");
        var lost = notes.Observe([], false, "", null, t.AddSeconds(4));
        check(lost is [{ Title: "PC nicht mehr verbunden" }] && lost[0].Text.Contains("PC 2"), "departed PC notifies once by name");
        check(notes.Observe(["PC 2"], false, "", null, t.AddSeconds(6)) is [{ Title: "PC verbunden" }], "reconnected PC notifies");
        check(notes.Observe([], false, "", null, t.AddSeconds(8)).Count == 0, "flapping PC is rate limited");
        check(notes.Observe([], true, "Gaming auf PC 2 erkannt", null, t.AddSeconds(10)) is [{ Text: "Gaming auf PC 2 erkannt" }],
            "limit activation notifies with its reason");
        check(notes.Observe([], true, "x", null, t.AddSeconds(12)).Count == 0, "an ongoing limit does not repeat");
        var advice = new PeerNetworkDiagnostics("Automatisch", [], []).Assess();
        check(notes.Observe([], true, "x", advice, t.AddSeconds(14)) is [{ Title: "Verbindungsproblem" }], "a new actionable problem notifies");
        check(LimitExplanation.Describe(new LimitDecision(16, "", true, null, false), true, null, [], true, false, true, true, null, 16,
            pausedUntil: t.AddMinutes(30)).StartsWith("Von dir pausiert"), "a user pause is the stated limit reason");
    }
}

internal static class BufferbloatTests
{
    internal static void Run(Action<bool, string> check)
    {
        check(Bufferbloat.Rate(20, 22) is { Grade: "A+", Recommendation: null }, "small latency increase rates A+");
        check(Bufferbloat.Rate(20, 70) is { Grade: "B" }, "moderate increase rates B");
        check(Bufferbloat.Rate(20, 200) is { Grade: "C" } c && c.Recommendation!.Contains("SQM"), "strong increase recommends router SQM");
        check(Bufferbloat.Rate(20, 900)?.Grade == "F", "severe increase rates F");
        check(Bufferbloat.Rate(null, 50) is null && Bufferbloat.Rate(20, double.NaN) is null, "missing samples give no rating");
        check(Bufferbloat.Median([10, null, 30, 20]) == 20 && Bufferbloat.Median([5, null]) is null, "median ignores lost pings and needs two answers");
    }
}

internal static class ShareWeightTests
{
    internal static void Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var settings = new DynexSettings { LineMbps = 100, HeadroomPercent = 40, GamingReserveMbps = 0, AdaptiveEnabled = false, ShareWeight = 2 };
        var proposal = new DownloadBudgetProposal(100, 40, 0);
        PeerAdvertisement Peer(double? weight) => new(Guid.NewGuid(), "PC 2", true, null, null, now, BudgetProposal: proposal, ShareWeight: weight);
        var high = new LimitPlanner(settings).Plan(false, [Peer(1)], now: now);
        check(high.DownloadLimitMbps == 40 && high.ShareWeightTotal == 3, "high-priority PC receives two thirds of a 60 Mbit/s pool");
        var low = new LimitPlanner(settings with { ShareWeight = 1 }).Plan(false, [Peer(2)], now: now);
        check(low.DownloadLimitMbps == 20, "the other PC receives the matching one third, so shares never exceed the pool");
        var legacy = new LimitPlanner(settings).Plan(false, [Peer(null)], now: now);
        check(legacy.DownloadLimitMbps == 30, "priorities fall back to equal shares while an older peer announces none");
        try { new LimitPlanner(settings with { ShareWeight = 10 }); check(false, "out-of-range priority accepted"); }
        catch (ArgumentException) { check(true, "out-of-range priorities are rejected"); }
    }
}

internal static class SessionTrackerTests
{
    internal static void Run(Action<bool, string> check)
    {
        var tracker = new SessionTracker(TimeSpan.FromMinutes(2));
        var t = DateTimeOffset.UtcNow;
        check(tracker.Observe(true, "CS2", 20, false, t) is null, "session start returns nothing");
        for (var i = 1; i <= 90; i++) tracker.Observe(true, "CS2", i == 45 ? null : i % 2 == 0 ? 20 : 40, i > 30, t.AddSeconds(2 * i));
        var summary = tracker.Observe(false, null, 20, false, t.AddSeconds(182));
        check(summary is { Game: "CS2", AveragePingMs: { } avg, MaxPingMs: 40 } s && Math.Round(avg) == 30 && Math.Round(s.Duration.TotalSeconds) == 180 &&
            Math.Round(s.LimitedFor.TotalSeconds) == 120 && s.PingLossPercent > 0, "ended session reports duration, ping, loss and limited time");
        check(summary!.Text.StartsWith("CS2: 3 min · Ø Ping 30 ms (max 40 ms)"), "summary text is readable");
        tracker.Observe(true, "Short", 20, false, t.AddMinutes(10));
        check(tracker.Observe(false, null, null, false, t.AddMinutes(10).AddSeconds(30)) is null, "very short sessions are not reported");
    }
}

internal static class UploadLimitTests
{
    internal static void Run(Action<bool, string> check)
    {
        var gaming = new LimitDecision(16, "", true, null, false, ParticipantCount: 2, GamingCount: 1, ShareWeightTotal: 2, LocalShareWeight: 1);
        check(UploadLimit.Compute(gaming, 40, 65, null, true, false) == 13, "upload share = capacity × background share / PCs");
        check(UploadLimit.Compute(gaming with { ShareWeightTotal = 3, LocalShareWeight = 2 }, 40, 65, null, true, false) == 17.33,
            "PC priority applies to uploads as well");
        check(UploadLimit.Compute(gaming with { GamingActive = false }, 40, 65, null, true, false) is null, "no game in focus means uploads stay free");
        check(UploadLimit.Compute(gaming, 40, 65, null, true, paused: true) is null, "a user pause also frees uploads");
        check(UploadLimit.Compute(gaming, 40, 65, null, enabled: false, false) is null, "upload limiting can be switched off");
        check(UploadLimit.Compute(gaming, null, 65, null, true, false) is null, "no upload measurement means no automatic upload limit");
        check(UploadLimit.Compute(gaming, null, 65, 5, true, false) == 5 && UploadLimit.Compute(gaming, 40, 65, 5, true, false) == 5,
            "a manual upload cap works alone and as an upper bound");
        check(UploadLimit.Compute(gaming, 1, 10, null, true, false) == 0.5, "very slow lines keep a usable minimum upload");
    }
}
