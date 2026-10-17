using Dynex.Core;

internal static class ReductionEfficacyChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var origin = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        var settings = new DynexSettings { LineMbps = 200, HeadroomPercent = 35, GamingReserveMbps = 10 };
        var gamerId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        PeerAdvertisement Gamer(int t, double latency = 90, Guid? id = null, long? sequence = null) =>
            new(id ?? gamerId, "Gamer", true, 1, latency, origin.AddSeconds(t), IdleBaselineMs: 20,
                LatencyTargetId: "game-route", IdleBaselineAtUtc: origin.AddSeconds(-10),
                LatencySampleSequence: sequence ?? t + 1);
        ProbeStats Load(int t, double rate = 115, double latency = 20) => new(origin.AddSeconds(t), latency, rate, true, "local-route");
        LimitDecision PeerSample(LimitPlanner planner, int t, double latency = 90, double load = 115) =>
            planner.Plan(false, [Gamer(t, latency)], Load(t, load), origin.AddSeconds(t));
        LimitPlanner Reduced(double? maximum = null)
        {
            var planner = new LimitPlanner(settings with { ManualDownloadLimitMbps = maximum });
            for (var t = 0; t <= 10; t += 5) PeerSample(planner, t);
            return planner;
        }
        LimitPlanner LocalBaseline()
        {
            var planner = new LimitPlanner(settings);
            for (var t = -10; t <= -2; t += 2) planner.Plan(false, [], Load(t, 0), origin.AddSeconds(t));
            return planner;
        }

        foreach (var local in new[] { false, true })
        {
            var planner = local ? LocalBaseline() : new LimitPlanner(settings);
            var cap = local ? 120d : 60d;
            var prior = cap;
            var minimum = cap;
            var decreases = 0;
            LimitDecision result = null!;
            // Saturated traffic follows each current cap. Elapsed time alone must
            // neither ratchet the cap lower nor periodically restart a failed trial.
            for (var t = 0; t <= 1200; t += 5)
            {
                result = local ? planner.Plan(true, [], Load(t, prior, 90), origin.AddSeconds(t))
                    : PeerSample(planner, t, load: prior);
                if (result.DownloadLimitMbps < prior - .02) decreases++;
                prior = result.DownloadLimitMbps!.Value;
                minimum = Math.Min(minimum, prior);
            }
            check(decreases == 1 && minimum >= cap * .85 - .02 && result.DownloadLimitMbps == cap,
                $"unchanged high {(local ? "local" : "remote")} RTT allows one bounded trial, no downward spiral or cooldown sawtooth over 20 minutes");
        }

        var effective = Reduced();
        LimitDecision decision = null!;
        foreach (var t in new[] { 15, 20 }) decision = PeerSample(effective, t, 75);
        check(decision.DownloadLimitMbps == 51, "two improved RTT rounds cannot authorize another reduction");
        decision = PeerSample(effective, 25, 75);
        check(Math.Abs(decision.DownloadLimitMbps!.Value - 43.35) < .02,
            "three improved loaded RTT rounds spanning ten seconds permit a second evidence-backed step");
        for (var t = 30; t <= 600; t += 5) decision = PeerSample(effective, t, 75);
        check(decision.DownloadLimitMbps == 51,
            "an ineffective second step rolls back only itself and cannot repeatedly undo the proven first-step benefit");

        var jitter = Reduced();
        for (var t = 15; t <= 90; t += 5) decision = PeerSample(jitter, t, t % 3 == 0 ? 87 : 89);
        check(decision.DownloadLimitMbps == 60, "small RTT jitter is not sufficient evidence to keep reducing");
        var repeated = Reduced();
        for (var t = 15; t <= 90; t += 5)
            decision = repeated.Plan(false, [Gamer(t, 60, sequence: 3)], Load(t), origin.AddSeconds(t));
        check(decision.DownloadLimitMbps == 60, "replayed improved values cannot prove efficacy or retain an old reduction");

        var idle = Reduced();
        for (var t = 15; t <= 70; t += 5) decision = PeerSample(idle, t, load: 1);
        check(decision.DownloadLimitMbps == 60, "a drop in load alone cannot prolong or compound the previous experiment");
        for (var t = 80; t <= 90; t += 5) decision = PeerSample(idle, t);
        check(decision.DownloadLimitMbps == 51, "a measured new load episode after sustained idle can try protection again");

        foreach (var kind in new[] { "missing", "expired", "invalid", "duplicate" })
        {
            var planner = Reduced(25);
            for (var t = 15; t <= 80; t += 5)
            {
                var peer = Gamer(t);
                peer = kind switch
                {
                    "missing" => peer with { IdleBaselineMs = null },
                    "expired" => peer with { IdleBaselineAtUtc = origin.AddMinutes(-31) },
                    "invalid" => peer with { LatencyMs = double.NaN },
                    _ => peer with { LatencySampleSequence = 3 }
                };
                decision = planner.Plan(false, [peer], Load(t), origin.AddSeconds(t));
                check(decision.DownloadLimitMbps <= 25, "safety recovery never exceeds the local manual share: " + kind);
            }
            check(decision.DownloadLimitMbps == 25 && !decision.AdaptiveReduced,
                "missing or unusable authoritative comparison has a bounded hold: " + kind);
        }

        var multiple = new LimitPlanner(settings);
        double minMultiple = 100;
        for (var t = 0; t <= 90; t += 5)
        {
            decision = multiple.Plan(false, [Gamer(t, t <= 10 ? 90 : 50), Gamer(t, 90, otherId)], Load(t), origin.AddSeconds(t));
            minMultiple = Math.Min(minMultiple, decision.DownloadLimitMbps!.Value);
        }
        check(minMultiple >= 31.16 && decision.DownloadLimitMbps == 36.66,
            "one improving gamer cannot mask another persistently slow gamer when proving a reduction effective");
        var bothAuthorities = LocalBaseline();
        var priorBoth = 55d;
        var bothCuts = 0;
        for (var t = 0; t <= 600; t += 5)
        {
            decision = bothAuthorities.Plan(true, [Gamer(t)], Load(t, priorBoth, 90), origin.AddSeconds(t));
            if (decision.DownloadLimitMbps < priorBoth - .02) bothCuts++;
            priorBoth = decision.DownloadLimitMbps!.Value;
        }
        check(bothCuts == 1 && decision.DownloadLimitMbps == 55,
            "local and remote authorities cannot take turns retrying the same failed experiment");

        var genuinelyResponsive = new LimitPlanner(settings);
        for (var round = 0; round < 20; round++)
            for (var sample = 0; sample < 3; sample++)
                decision = PeerSample(genuinelyResponsive, round * 15 + sample * 5, 20 + 2000 * Math.Pow(.8, round));
        check(decision.DownloadLimitMbps == 6,
            "repeated independently observed improvements preserve deep bufferbloat protection and the ten-percent floor");
        check(genuinelyResponsive.Plan(false, [], now: origin.AddSeconds(310)).DownloadLimitMbps is null,
            "ending gaming immediately releases even a deeply reduced effective budget");
    }
}
