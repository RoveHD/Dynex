using Dynex.Core;

internal static class ComfortTests
{
    internal static void Run(Action<bool, string> check)
    {
        // Ping alarm
        var alarm = new PingAlarm(TimeSpan.FromMinutes(2));
        var t = DateTimeOffset.UtcNow;
        for (var i = 0; i < 10; i++) alarm.Observe(20, true, [], t.AddSeconds(2 * i));
        check(alarm.Baseline == 20, "ping alarm learns the normal ping");
        LoadSource[] loads = [new("PC 2", 80, 2, "steam"), new("PC 3", 3, 1)];
        check(alarm.Observe(90, true, loads, t.AddSeconds(30)) is null && alarm.Observe(95, true, loads, t.AddSeconds(32)) is null,
            "single high samples do not alarm");
        var message = alarm.Observe(92, true, loads, t.AddSeconds(34));
        check(message is not null && message.Contains("„PC 2“") && message.Contains("82 Mbit/s") && message.Contains("steam"),
            "sustained spike names the busiest PC and program as likely cause");
        check(alarm.Observe(93, true, loads, t.AddSeconds(36)) is null, "alarm respects its cooldown");
        var quiet = new PingAlarm();
        for (var i = 0; i < 10; i++) quiet.Observe(20, true, [], t);
        quiet.Observe(90, true, [], t); quiet.Observe(90, true, [], t);
        check(quiet.Observe(90, true, [], t)!.Contains("Anbieter"), "without LAN load the provider/server is named");
        var idle = new PingAlarm();
        for (var i = 0; i < 10; i++) idle.Observe(20, false, [], t);
        for (var i = 0; i < 5; i++) check(idle.Observe(90, false, loads, t) is null, "no alarm while nobody plays");

        // Schedule
        var night = new ScheduleSettings(true, new TimeOnly(1, 0), new TimeOnly(8, 0), "Entspannt");
        check(Schedule.Evaluate(night, new DateTime(2026, 10, 14, 2, 30, 0)) is { LimitsOff: true }, "night window frees limits");
        check(Schedule.Evaluate(night, new DateTime(2026, 10, 14, 9, 0, 0)) is { LimitsOff: false, Profile: null }, "outside the window nothing changes on weekdays");
        check(Schedule.Evaluate(night, new DateTime(2026, 10, 17, 15, 0, 0)) is { Profile: "Entspannt" }, "weekend applies its profile");
        check(Schedule.InWindow(new TimeOnly(23, 30), new TimeOnly(23, 0), new TimeOnly(6, 0)) &&
            !Schedule.InWindow(new TimeOnly(12, 0), new TimeOnly(23, 0), new TimeOnly(6, 0)), "windows across midnight work");

        // Quality log
        var quality = new QualityMonitor();
        QualityEvent? ended = null;
        for (var i = 0; i < 10; i++) quality.Observe(20, 2, t.AddSeconds(2 * i));
        for (var i = 10; i < 15; i++) quality.Observe(null, 2, t.AddSeconds(2 * i));
        ended = quality.Observe(21, 2, t.AddSeconds(30));
        check(ended is { Kind: QualityEvent.Outage } && ended.Duration.TotalSeconds == 8, "internet outage with a working router is recorded");
        quality.Observe(null, null, t.AddSeconds(32)); quality.Observe(null, null, t.AddSeconds(34)); quality.Observe(null, null, t.AddSeconds(36));
        check(quality.Observe(20, 2, t.AddSeconds(38)) is null, "local outage (router silent too) is not blamed on the provider");
        for (var i = 0; i < 3; i++) quality.Observe(400, 2, t.AddSeconds(40 + 2 * i));
        check(quality.Observe(20, 2, t.AddSeconds(46)) is { Kind: QualityEvent.Spike, PeakMs: 400 }, "sustained ping spikes are recorded with their peak");
        check(ended!.Csv.Split(';').Length == 4, "events export as CSV lines");

        // Router guide
        check(RouterGuide.For("AVM", "FRITZ!Box 7590", null).Steps[1].Contains("Priorisierung"), "FRITZ!Box guide names the prioritization page");
        check(RouterGuide.For(null, null, "OpenWrt").Steps.Any(s => s.Contains("cake")), "OpenWrt guide uses SQM/cake");
        check(RouterGuide.For(null, null, null).Router == "Internetrouter nicht erkannt" && RouterGuide.For(null, null, null).Steps.Count == 3, "unidentified Internet routers are stated honestly and get generic steps");

        // Weekly report
        SessionSummary[] sessions = [
            new(t.AddDays(-1), TimeSpan.FromHours(2), "CS2", 25, 60, 0, TimeSpan.FromHours(1)),
            new(t.AddDays(-2), TimeSpan.FromHours(1), "Valorant", 35, 80, 0, TimeSpan.Zero),
            new(t.AddDays(-10), TimeSpan.FromHours(5), "Old", 10, 10, 0, TimeSpan.Zero)];
        var report = WeeklyReport.Build(sessions, [ended!], t);
        check(report.Contains("3 h in 2 Sitzungen") && report.Contains("meistgespielt: CS2") && report.Contains("Ø 30 ms") &&
            !report.Contains("begrenzt", StringComparison.OrdinalIgnoreCase) && report.Contains("1 Ausfälle"),
            "weekly report summarizes the last seven days without removed limiting statistics");
        check(WeeklyReport.Build([], [], t).Contains("noch keine"), "empty week is stated plainly");
    }
}

internal static class IsolationTests
{
    internal static void Run(Action<bool, string> check)
    {
        var silent = new PeerNetworkDiagnostics("Automatisch", ["192.168.1.2"], []);
        check(silent.Assess(lan: new("FRITZ!Box Gastzugang")) is { Kind: ConnectionProblemKind.ClientIsolation } guest && guest.Cause.Contains("Gastnetz"),
            "a guest Wi-Fi name explains why PCs cannot see each other");
        check(silent.Assess(lan: new("Heimnetz", 0)) is { Kind: ConnectionProblemKind.ClientIsolation } isolation && isolation.NextStep.Contains("Isolation"),
            "no other LAN device answering points to client isolation");
        check(silent.Assess(lan: new("Heimnetz", 4)).Kind == ConnectionProblemKind.NoResponse, "other devices answering keeps the generic diagnosis");
        check(new PeerNetworkDiagnostics("Automatisch", ["192.168.1.2"], [], ConnectedPeers: 1).Assess(lan: new("Gast")).Kind == ConnectionProblemKind.None,
            "connected PCs are never reported as isolated");
    }
}
