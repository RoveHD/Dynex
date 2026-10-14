using System.Net;
using System.Net.Sockets;
using Dynex.Core;

internal static class SharedBudgetTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var settings = new[]
        {
            new DynexSettings { DeviceId = ids[0], LineMbps = 200, HeadroomPercent = 15, GamingReserveMbps = 12, AdaptiveEnabled = false },
            new DynexSettings { DeviceId = ids[1], LineMbps = 160, HeadroomPercent = 10, GamingReserveMbps = 8, AdaptiveEnabled = false },
            new DynexSettings { DeviceId = ids[2], LineMbps = 240, HeadroomPercent = 20, GamingReserveMbps = 15, AdaptiveEnabled = false }
        };
        var proposals = settings.Select(DownloadBudgetProposal.FromSettings).ToArray();
        var participants = settings.Select((s, i) => new PeerAdvertisement(s.DeviceId, "PC " + i, i == 2, null, null, now,
            BudgetProposal: proposals[i])).ToArray();
        LimitDecision[] Plans(PeerAdvertisement[] views, DateTimeOffset? at = null) => settings.Select((s, i) =>
            new LimitPlanner(s).Plan(views.Single(p => p.DeviceId == s.DeviceId).IsGaming,
                views.Where(p => p.DeviceId != s.DeviceId).ToArray(), now: at ?? now)).ToArray();
        var plans = Plans(participants);
        check(plans.All(p => p.SharedCapacityMbps == 160 && p.SharedDownloadBudgetMbps == 136 && p.ParticipantCount == 3 &&
            p.GamingCount == 1 && p.SharedBudgetComplete && p.DownloadLimitMbps == 45.33),
            "different capacities and reserve policies converge on one 136 Mbps pool and equal per-PC shares");
        check(plans.Sum(p => p.DownloadLimitMbps) <= plans[0].SharedDownloadBudgetMbps,
            "all three PCs including the gamer together stay inside the shared download pool");
        var switched = participants.Select((p, i) => p with { IsGaming = i == 1 }).ToArray();
        check(Plans(switched).All(p => p.SharedDownloadBudgetMbps == 136 && p.GamingCount == 1),
            "changing which PC games leaves the common reserve count and pool coherent");
        var twoGamers = participants.Select((p, i) => p with { IsGaming = i is 1 or 2 }).ToArray();
        check(Plans(twoGamers).All(p => p.SharedDownloadBudgetMbps == 128 && p.DownloadLimitMbps == 42.66 && p.GamingCount == 2),
            "multiple gamers reduce the same common pool on every participating PC");
        var withoutSlow = participants.Where(p => p.DeviceId != ids[1]).ToArray();
        var recovered = new LimitPlanner(settings[0]).Plan(false, withoutSlow.Where(p => p.DeviceId != ids[0]).ToArray(), now: now);
        check(recovered.SharedCapacityMbps == 200 && recovered.SharedDownloadBudgetMbps == 158 && recovered.DownloadLimitMbps == 79,
            "departed slow participant's original capacity is not recirculated as a negotiated minimum");
        var stale = new LimitPlanner(settings[0]).Plan(false,
            [participants[1] with { LastSeenUtc = now.AddSeconds(-11) }, participants[2]], now: now);
        check(stale.SharedDownloadBudgetMbps == 158 && stale.ParticipantCount == 2 && stale.SharedBudgetComplete,
            "stale capacity proposals expire together with their participant instead of pinning the shared budget");
        var legacy = participants[1] with { BudgetProposal = null };
        var legacyPlan = new LimitPlanner(settings[0]).Plan(false, [legacy, participants[2]], now: now);
        check(!legacyPlan.SharedBudgetComplete && legacyPlan.ParticipantCount == 3 && legacyPlan.SharedCapacityMbps == 200,
            "legacy peers count toward sharing but cannot silently certify a complete common capacity basis");
        foreach (var invalid in new[]
        {
            new DownloadBudgetProposal(0, 15, 12), new(double.NaN, 15, 12), new(double.PositiveInfinity, 15, 12),
            new(100001, 15, 12), new(200, -1, 12), new(200, 91, 12), new(200, 15, -1), new(200, 15, double.NaN)
        })
        {
            var invalidPlan = new LimitPlanner(settings[0]).Plan(false, [participants[1] with { BudgetProposal = invalid }, participants[2]], now: now);
            check(!invalid.IsValid && !invalidPlan.SharedBudgetComplete && invalidPlan.SharedCapacityMbps == 200,
                "invalid capacity/reserve proposal is excluded without inventing agreement");
        }
        var manualPlan = new LimitPlanner(settings[0] with { ManualDownloadLimitMbps = 5 }).Plan(false, participants[1..], now: now);
        check(manualPlan.DownloadLimitMbps == 5 && manualPlan.SharedDownloadBudgetMbps == 136,
            "a local manual cap only decreases its own share and does not change the common pool");
        var impossible = new LimitPlanner(settings[0] with { LineMbps = 1, HeadroomPercent = 90, GamingReserveMbps = 100 }).Plan(true, [], now: now);
        check(impossible.SharedDownloadBudgetMbps == 0 && !impossible.SharedBudgetComplete && impossible.DownloadLimitMbps == .1 &&
            impossible.Reason.Contains("Mindestlimit", StringComparison.Ordinal),
            "unrepresentable sub-0.1 Mbps shares are visibly best effort rather than falsely certified shared enforcement");
        var noGaming = Plans(participants.Select(p => p with { IsGaming = false }).ToArray());
        check(noGaming.All(p => p.SharedDownloadBudgetMbps is null && p.DownloadLimitMbps is null && p.GamingCount == 0),
            "no gaming releases automatic budgets while retaining discovered participant metadata");

        await ThreePeerUdp(check);
    }

    private static async Task ThreePeerUdp(Action<bool, string> check)
    {
        var keys = Enumerable.Range(0, 3).Select(_ => AutoLanIdentity.GenerateSigningKey()).ToArray();
        var ports = new HashSet<int>();
        while (ports.Count < 3) ports.Add(FreePort());
        var portArray = ports.ToArray();
        var configs = new[] { (200d, 15d, 12d), (160d, 10d, 8d), (240d, 20d, 15d) };
        var settings = configs.Select((c, i) => new DynexSettings
        {
            DeviceId = AutoLanIdentity.GetDeviceId(keys[i]), DeviceName = "Shared-PC " + i,
            LoopbackOnly = true, Port = portArray[i], LineMbps = c.Item1, HeadroomPercent = c.Item2, GamingReserveMbps = c.Item3,
            AdaptiveEnabled = false, PeerTtl = TimeSpan.FromMilliseconds(1500), HeartbeatInterval = TimeSpan.FromMilliseconds(250),
            DirectPeers = portArray.Where(p => p != portArray[i]).Select(p => new IPEndPoint(IPAddress.Loopback, p)).ToArray()
        }).ToArray();
        await using var a = new AutoLanPeerService(settings[0], keys[0]);
        await using var b = new AutoLanPeerService(settings[1], keys[1]);
        await using var cPeer = new AutoLanPeerService(settings[2], keys[2]);
        var services = new[] { a, b, cPeer };
        var planners = settings.Select(s => new LimitPlanner(s)).ToArray();
        var gaming = new[] { false, false, true };
        var applied = new double?[3];
        var gate = new object();
        using var publishStop = new CancellationTokenSource();
        void Publish()
        {
            lock (gate)
                for (var i = 0; i < services.Length; i++)
                    services[i].Publish(gaming[i], 5, 20, applied[i], gaming[i] ? "UDP test game" : null, applied[i].HasValue);
        }
        Publish();
        await a.StartAsync(); await b.StartAsync(); await cPeer.StartAsync();
        var publishing = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            try { while (await timer.WaitForNextTickAsync(publishStop.Token)) Publish(); }
            catch (OperationCanceledException) when (publishStop.IsCancellationRequested) { }
        });
        try
        {
            await Until(() => services.All(s => s.Snapshot().Count == 2 && s.Snapshot().All(p => p.BudgetProposal is { IsValid: true })),
                "three independent AutoLAN clients discover complete budget proposals");
            LimitDecision[] CurrentPlans() => services.Select((s, i) => planners[i].Plan(gaming[i], s.Snapshot())).ToArray();
            var decisions = CurrentPlans();
            check(decisions.All(d => d.SharedBudgetComplete && d.SharedCapacityMbps == 160 && d.SharedDownloadBudgetMbps == 136 &&
                d.ParticipantCount == 3 && d.DownloadLimitMbps == 45.33),
                "three actual encrypted UDP clients converge despite local 200/160/240 Mbps measurements");
            lock (gate) for (var i = 0; i < applied.Length; i++) applied[i] = decisions[i].DownloadLimitMbps;
            Publish();
            await Until(() => services.All(s => s.Snapshot().All(p => p.ShaperActive && p.AppliedLimitMbps == 45.33)),
                "all three publishers exchange their simulated local apply confirmations");
            check(applied.Sum() <= decisions[0].SharedDownloadBudgetMbps && services.All(s => s.Snapshot().All(p => p.ShaperActive)),
                "three publisher-confirmed simulated download shares sum to 135.99 Mbps inside the 136 Mbps pool");
            Console.WriteLine("UDP shared-pool evidence: capacity proposals=200/160/240 Mbps; pool=136 Mbps; applied test acknowledgements=45.33+45.33+45.33=135.99 Mbps. No native filter used.");
            lock (gate) { gaming[1] = true; gaming[2] = false; }
            Publish();
            await Until(() => services.All(s => s.Snapshot().All(p => p.IsGaming == (p.DeviceId == settings[1].DeviceId))), "gamer changes between PCs");
            check(CurrentPlans().All(d => d.GamingCount == 1 && d.SharedDownloadBudgetMbps == 136),
                "actual three-PC UDP gamer handover preserves one common reserve-adjusted pool");
            lock (gate) gaming[2] = true;
            Publish();
            await Until(() => services.All(s => s.Snapshot().All(p => p.IsGaming == (p.DeviceId != settings[0].DeviceId))), "two UDP gaming PCs");
            check(CurrentPlans().All(d => d.GamingCount == 2 && d.SharedDownloadBudgetMbps == 128 && d.DownloadLimitMbps == 42.66),
                "actual UDP two-gamer state converges on a smaller shared pool");
            await b.StopAsync();
            await Until(() => a.Snapshot().Count == 1 && cPeer.Snapshot().Count == 1 && a.Snapshot().All(p => p.DeviceId != settings[1].DeviceId),
                "slow-capacity participant expires from both remaining peers");
            var afterLoss = new[] { planners[0].Plan(false, a.Snapshot()), planners[2].Plan(true, cPeer.Snapshot()) };
            check(afterLoss.All(d => d.SharedBudgetComplete && d.ParticipantCount == 2 && d.GamingCount == 1 && d.SharedCapacityMbps == 200 &&
                d.SharedDownloadBudgetMbps == 158 && d.DownloadLimitMbps == 79),
                "actual UDP TTL removes departed 160 Mbps proposal and remaining original proposals recover to a 158 Mbps pool");
        }
        finally { publishStop.Cancel(); await publishing; }
    }

    private static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(7);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException(message);
            await Task.Delay(25);
        }
    }
}
