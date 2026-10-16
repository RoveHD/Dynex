using System.Text.Json;
using Dynex.Core;

internal static class CapacitySharingTests
{
    internal static void Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var consumer = new DynexSettings { CapacityMeasured = false, LineMbps = 200, UploadCapacityMbps = 8, AdaptiveEnabled = false };
        var source = new PeerAdvertisement(Guid.NewGuid(), "Measured PC", true, null, null, now,
            BudgetProposal: new(800, 20, 10, 120), ShareWeight: 1);
        var planner = new LimitPlanner(consumer);
        var plan = planner.Plan(false, [source], now: now);
        check(plan is { SharedCapacityMbps: 800, SharedDownloadBudgetMbps: 630, DownloadLimitMbps: 315,
            SharedUploadCapacityMbps: 120, SharedUploadBudgetMbps: 96 },
            "unmeasured consumer uses authenticated PC download/upload capacity without its numerical local defaults");
        check(UploadLimit.Compute(plan, null, 65, null, true, false) == 48,
            "unmeasured receiving PC shapes uploads from the shared PC proposal and source headroom");
        check(consumer is { CapacityMeasured: false, LineMbps: 200, UploadCapacityMbps: 8 },
            "negotiation never changes source settings or promotes a consumer into a measured publisher");
        var manual = new LimitPlanner(consumer with { ManualDownloadLimitMbps = 25 }).Plan(false, [source], now: now);
        check(manual.DownloadLimitMbps == 25 && UploadLimit.Compute(manual, null, 65, 5, true, false) == 5,
            "local manual download/upload maxima only reduce remotely supplied automatic shares");
        foreach (var peers in new[] { Array.Empty<PeerAdvertisement>(), new[] { source with { LastSeenUtc = now.AddSeconds(-11) } },
            new[] { source with { LastSeenUtc = now.AddSeconds(6) } } })
        {
            var unavailable = planner.Plan(true, peers, now: now);
            check(unavailable is { DownloadLimitMbps: null, SharedCapacityMbps: null, SharedUploadCapacityMbps: null } &&
                UploadLimit.Compute(unavailable, null, 65, null, true, false) is null,
                "absent, expired or future PC publications cannot supply consumer download/upload capacity");
        }
        var legacy = planner.Plan(false, [source with { BudgetProposal = new(800, 20, 10) }], now: now);
        check(legacy.DownloadLimitMbps == 315 && legacy.SharedUploadCapacityMbps is null &&
            UploadLimit.Compute(legacy, null, 65, null, true, false) is null,
            "legacy proposals supply download while missing upload remains unknown");
        check(!JsonSerializer.Serialize(new DownloadBudgetProposal(800, 20, 10)).Contains("UploadCapacityMbps") &&
            JsonSerializer.Deserialize<DownloadBudgetProposal>("{\"LineMbps\":800,\"HeadroomPercent\":20,\"GamingReserveMbps\":10}") is { UploadCapacityMbps: null, IsValid: true },
            "optional upload metadata preserves legacy proposal JSON and constructor compatibility");
        foreach (var upload in new[] { 0d, -.1, double.NaN, double.PositiveInfinity, 100001 })
            check(!new DownloadBudgetProposal(800, 20, 10, upload).IsValid &&
                planner.Plan(false, [source with { BudgetProposal = new(800, 20, 10, upload) }], now: now).SharedCapacityMbps is null,
                "invalid uploaded capacity rejects the complete untrusted proposal: " + upload);
        var latest = planner.Plan(false, [source, source with { LastSeenUtc = now.AddSeconds(1), BudgetProposal = null }], now: now);
        check(latest.SharedCapacityMbps is null && latest.SharedUploadCapacityMbps is null,
            "freshest identity publication replaces old proposals instead of retaining stale capacity");
        var local = consumer with { CapacityMeasured = true, LineMbps = 500, UploadCapacityMbps = 40, HeadroomPercent = 20, GamingReserveMbps = 10 };
        var shared = new LimitPlanner(local).Plan(false, [source], now: now);
        check(shared.SharedCapacityMbps == 500 && shared.SharedUploadCapacityMbps == 40 && shared.SharedUploadBudgetMbps == 32 &&
            UploadLimit.Compute(shared, 40, 80, null, true, false) == 16,
            "measured PCs retain conservative original local capacities and minimize the reserve-adjusted upload pool");
        var idle = planner.Plan(false, [source with { IsGaming = false }], now: now);
        check(idle is { SharedCapacityMbps: 800, SharedUploadCapacityMbps: 120, DownloadLimitMbps: null, SharedUploadBudgetMbps: null } &&
            UploadLimit.Compute(idle, null, 65, null, true, false) is null,
            "game end retains fresh capacity metadata but releases both automatic directions");
    }
}
