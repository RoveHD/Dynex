using System.Net;
using System.Net.Sockets;
using Dynex.Core;

internal static class SharedDownloadActivityTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var settings = Enumerable.Range(0, 4).Select(_ => new DynexSettings
        {
            LineMbps = 84, HeadroomPercent = 35, GamingReserveMbps = 4.2,
            UploadCapacityMbps = 20, SharedDownloadMaximumMbps = 25, AdaptiveEnabled = false
        }).ToArray();
        PeerAdvertisement[] Members(int active) => settings.Select((s, i) => new PeerAdvertisement(s.DeviceId, $"PC {i}",
            i == 3, 80, 20, now, BudgetProposal: DownloadBudgetProposal.FromSettings(s), ShareWeight: s.ShareWeight,
            LimitedDownloadMbps: i < active ? 25 : 0)).ToArray();
        LimitDecision Plan(int i, PeerAdvertisement[] members, ProbeStats? probe = null) => new LimitPlanner(settings[i]).Plan(
            members[i].IsGaming, members.Where(p => p.DeviceId != settings[i].DeviceId).ToArray(),
            probe ?? new(now, 20, 80, LimitedDownloadMbps: members[i].LimitedDownloadMbps), now);
        for (var count = 1; count <= 3; count++)
        {
            var members = Members(count);
            var plans = Enumerable.Range(0, 4).Select(i => Plan(i, members)).ToArray();
            var expected = Math.Floor(25d / count * 100) / 100;
            check(plans.Take(count).All(p => p.DownloadLimitMbps == expected && p.SharedDownloadBudgetMbps == 25 &&
                p.DownloadParticipantCount == count && p.DownloadActivityComplete),
                $"{count} active bulk downloader(s) divide one 25 Mbps maximum into {expected} Mbps shares");
            check(plans.Take(count).Sum(p => p.DownloadLimitMbps) <= 25,
                $"converged {count}-downloader sum cannot exceed the shared maximum");
            check(plans[3].DownloadLimitMbps == Math.Floor(25d / (count + 1) * 100) / 100 &&
                plans[3].DownloadLimitMbps > 0, "idle gaming PC has a finite prospective start share without consuming the active allocation");
            check(plans.All(p => p.ParticipantCount == 4 && p.ShareWeightTotal == 4 && p.SharedUploadBudgetMbps == 13) &&
                plans.Sum(p => UploadLimit.Compute(p, 20, 65, null, true, false)) == 13,
                "upload allocations still divide by all four participants independently of active downloads");
        }
        var one = Members(1);
        check(Plan(0, one).DownloadLimitMbps == 25, "high adapter/game traffic with zero classified bulk load occupies no download share");
        var unknown = one.ToArray(); unknown[1] = unknown[1] with { LimitedDownloadMbps = null };
        check(Plan(0, unknown) is { DownloadLimitMbps: 6.25, DownloadActivityComplete: false, DownloadParticipantCount: 4, SharedBudgetComplete: false },
            "one unknown or legacy activity report restores conservative all-participant sharing");
        check(Plan(0, one, new(now.AddSeconds(-11), 20, 0, LimitedDownloadMbps: 0)) is { DownloadLimitMbps: 6.25, DownloadActivityComplete: false },
            "expired local idle measurement is unknown rather than a measured zero");
        check(Plan(0, one, new(now.AddSeconds(2), 20, 0, LimitedDownloadMbps: 0)).DownloadActivityComplete == false,
            "future local activity cannot exclude participants");
        foreach (var invalid in new[] { -1d, double.NaN, double.PositiveInfinity, 100001d })
        {
            var bad = one.ToArray(); bad[1] = bad[1] with { LimitedDownloadMbps = invalid };
            check(Plan(0, bad).DownloadLimitMbps == 6.25, "invalid direct activity input fails conservatively");
            check(!new DownloadBudgetProposal(84, 35, 4.2, 20, invalid).IsValid, "invalid shared maximum fails proposal validation");
            try { _ = new LimitPlanner(settings[0] with { SharedDownloadMaximumMbps = invalid }); check(false, "invalid local maximum accepted"); }
            catch (ArgumentException) { check(true, "invalid local maximum is rejected before planning"); }
        }
        var expired = Members(2); expired[1] = expired[1] with { LastSeenUtc = now.AddSeconds(-11) };
        check(Plan(0, expired) is { DownloadLimitMbps: 25, ParticipantCount: 3 }, "expired downloader leaves the active distribution");
        var disconnected = new LimitPlanner(settings[0]).Plan(false, [one[3]], new(now, 20, 30, LimitedDownloadMbps: 25), now);
        check(disconnected.DownloadLimitMbps == 25 && disconnected.ParticipantCount == 2, "disconnected participants release their allocations");
        var restarted = one.Append(one[1] with { LastSeenUtc = now.AddMilliseconds(1), SessionId = Guid.NewGuid(), LimitedDownloadMbps = null }).ToArray();
        check(Plan(0, restarted) is { DownloadLimitMbps: 6.25, DownloadActivityComplete: false },
            "new session with unknown telemetry replaces old idle evidence instead of inheriting it");
        var smaller = one.ToArray(); smaller[1] = smaller[1] with { BudgetProposal = new(84, 35, 4.2, 20, 15) };
        check(Plan(0, smaller).SharedDownloadBudgetMbps == 15 && Plan(0, smaller).DownloadLimitMbps == 15,
            "different original maximum proposals converge to their conservative minimum");
        smaller[1] = smaller[1] with { BudgetProposal = new(20, 35, 4.2, 20, 25) };
        check(Math.Abs(Plan(0, smaller).SharedDownloadBudgetMbps!.Value - 8.8) < .001,
            "reserve-adjusted slower measured capacity remains below the configured shared maximum");
        var noGaming = one.Select(p => p with { IsGaming = false }).ToArray();
        check(Plan(0, noGaming) is { DownloadLimitMbps: null, SharedDownloadBudgetMbps: null, GamingActive: false },
            "common gaming maximum does not introduce a limiter outside gaming sessions");
        var weighted = Members(2); weighted[0] = weighted[0] with { ShareWeight = 2 };
        var highSettings = settings[0] with { ShareWeight = 2 };
        var high = new LimitPlanner(highSettings).Plan(false, weighted.Skip(1).ToArray(), new(now, 20, 25, LimitedDownloadMbps: 25), now);
        var normal = Plan(1, weighted);
        check(high.DownloadLimitMbps == 16.66 && normal.DownloadLimitMbps == 8.33 &&
            high.DownloadLimitMbps + normal.DownloadLimitMbps <= 25 && high.ShareWeightTotal == 5 && high.DownloadShareWeightTotal == 3,
            "active downloads honor weights while separate total weights preserve upload safety");
        var manual = new LimitPlanner(settings[0] with { ManualDownloadLimitMbps = 5 }).Plan(false, one.Skip(1).ToArray(),
            new(now, 20, 25, LimitedDownloadMbps: 25), now);
        check(manual.DownloadLimitMbps == 5 && manual.SharedDownloadBudgetMbps == 25, "local manual cap only reduces its assigned common-budget share");
        var consumer = new LimitPlanner(settings[0] with { CapacityMeasured = false });
        var measuredSource = one[3] with { BudgetProposal = new(100, 0, 0), LimitedDownloadMbps = 0 };
        var unshared = consumer.Plan(false, [measuredSource], new(now, 20, 30, LimitedDownloadMbps: 25), now);
        check(unshared.SharedDownloadBudgetMbps == 100 && unshared.DownloadLimitMbps == 100,
            "unmeasured local shared maximum is not silently applied without an advertised original proposal");
        var sharedConsumer = consumer.Plan(false, [one[3]], new(now, 20, 30, LimitedDownloadMbps: 25), now);
        check(sharedConsumer is { SharedDownloadBudgetMbps: 25, DownloadLimitMbps: 25, SharedBudgetComplete: true },
            "current phone consumer can confirm shared maximum from a measured PC without fabricating its own capacity");
        var tiny = new LimitPlanner(settings[0] with { SharedDownloadMaximumMbps = .1, ShareWeight = 4 }).Plan(false,
            [one[3] with { ShareWeight = .25, LimitedDownloadMbps = 1 }], new(now, 20, 30, LimitedDownloadMbps: 25), now);
        check(!tiny.SharedBudgetComplete && tiny.Reason.Contains("Mindestlimit"),
            "technical minimum prevents claiming a representable weighted pool when any participant share is too small");
        // Prove authentication/validation on both wire formats independently of sockets.
        using var codec = new PacketCodec(GroupKey.Generate());
        var wire = new WireAdvertisement(Guid.NewGuid(), Guid.NewGuid(), 1, now.ToUnixTimeMilliseconds(), "Bulk", true,
            80, 20, 25, null, true, BudgetProposal: new(84, 35, 4.2, 20, 25), LimitedDownloadMbps: 0);
        check(codec.Decode(codec.Encode(wire)) == wire, "PSK encrypted roundtrip preserves common maximum and measured bulk zero");
        check(codec.Decode(codec.Encode(wire with { LimitedDownloadMbps = null }))!.LimitedDownloadMbps is null,
            "PSK omitted legacy bulk telemetry stays unknown");
        using var a = new AutoLanCrypto(AutoLanIdentity.GenerateSigningKey());
        using var b = new AutoLanCrypto(AutoLanIdentity.GenerateSigningKey());
        using var channelA = a.Derive(AutoLanCrypto.Verify(b.Hello(now), now)!);
        using var channelB = b.Derive(AutoLanCrypto.Verify(a.Hello(now), now)!);
        wire = wire with { DeviceId = a.Local.DeviceId, SessionId = a.Local.SessionId };
        foreach (var bad in new[] { wire with { LimitedDownloadMbps = -1 }, wire with { LimitedDownloadMbps = 100001 },
            wire with { BudgetProposal = new(84, 35, 4.2, 20, 0) }, wire with { BudgetProposal = new(84, 35, 4.2, 20, 100001) } })
        {
            check(codec.Decode(codec.Encode(bad)) is null, "PSK rejects authenticated invalid shared maximum or bulk rate");
            check(b.Decrypt(channelB, a.Encrypt(channelA, bad), now) is null, "AutoLAN rejects authenticated invalid shared maximum or bulk rate");
        }
        check(b.Decrypt(channelB, a.Encrypt(channelA, wire), now) == wire,
            "AutoLAN valid shared maximum and zero bulk survive rejected packets without replay-state poisoning");
        await Transport(false, check);
        await Transport(true, check);
    }

    private static async Task Transport(bool automatic, Action<bool, string> check)
    {
        var keyA = AutoLanIdentity.GenerateSigningKey(); var keyB = AutoLanIdentity.GenerateSigningKey();
        var portA = Port(); var portB = Port(); while (portA == portB) portB = Port();
        var settingsA = new DynexSettings
        {
            DeviceId = AutoLanIdentity.GetDeviceId(keyA), DeviceName = "Observer", GroupKeyBase64 = GroupKey.Generate(),
            LoopbackOnly = true, Port = portA, DirectPeers = [new(IPAddress.Loopback, portB)],
            PeerTtl = TimeSpan.FromSeconds(1), HeartbeatInterval = TimeSpan.FromMilliseconds(100), SharedDownloadMaximumMbps = 25
        };
        var settingsB = settingsA with { DeviceId = AutoLanIdentity.GetDeviceId(keyB), DeviceName = "Sender", Port = portB,
            DirectPeers = [], SharedDownloadMaximumMbps = 30 };
        await using IPeerService a = automatic ? new AutoLanPeerService(settingsA, keyA) : new PeerService(settingsA);
        await using IPeerService b = automatic ? new AutoLanPeerService(settingsB, keyB) : new PeerService(settingsB);
        var mode = automatic ? "AutoLAN" : "PSK";
        b.Publish(true, 80, limitedDownloadMbps: 0);
        await a.StartAsync(); await b.StartAsync();
        await Until(() => a.Snapshot().SingleOrDefault() is { LimitedDownloadMbps: 0 });
        check(a.Snapshot().Single().BudgetProposal?.SharedDownloadMaximumMbps == 30,
            mode + " sends original configured maximum, never observer's smaller negotiated 25 Mbps");
        b.Publish(true, 80, limitedDownloadMbps: 12.5);
        await Until(() => a.Snapshot().SingleOrDefault() is { LimitedDownloadMbps: 12.5 });
        check(true, mode + " publishes updated authenticated classified bulk activity");
        b.Publish(true, 80, limitedDownloadMbps: null);
        await Until(() => a.Snapshot().SingleOrDefault() is { LimitedDownloadMbps: null });
        check(a.Snapshot().Single().DownloadMbps == 80, mode + " unknown bulk telemetry does not borrow adapter throughput");
        b.Publish(true, 80, limitedDownloadMbps: 0);
        await Until(() => a.Snapshot().SingleOrDefault() is { LimitedDownloadMbps: 0 });
        await Until(() => a.Snapshot().SingleOrDefault() is { LimitedDownloadMbps: null, BudgetProposal: null });
        check(true, mode + " repeated heartbeats expire unpublished bulk evidence to unknown with the local TTL");
        try { b.Publish(false, limitedDownloadMbps: double.NaN); check(false, "nonfinite bulk accepted"); }
        catch (ArgumentException) { check(true, mode + " refuses nonfinite local bulk before publication"); }
        await b.StopAsync();
        await Until(() => a.Snapshot().Count == 0);
        check(true, mode + " disconnect expires peer activity and budget together");
    }

    private static int Port() { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint!).Port; }
    private static async Task Until(Func<bool> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(6);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > until) throw new TimeoutException("Loopback shared-budget telemetry did not converge.");
            await Task.Delay(25);
        }
    }
}
