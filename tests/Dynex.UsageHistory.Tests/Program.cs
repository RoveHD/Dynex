using Dynex.Desktop;

var root = Path.Combine(AppContext.BaseDirectory, "usage-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var store = new UsageHistoryStore(root);
    var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    var day = new DateOnly(2027, 3, 14); // Spring-forward day is 23 hours in this zone.
    var before = new DateTimeOffset(2027, 3, 14, 4, 59, 58, TimeSpan.Zero);
    var inside = new DateTimeOffset(2027, 3, 14, 5, 0, 0, TimeSpan.Zero);
    var endInside = new DateTimeOffset(2027, 3, 15, 3, 59, 58, TimeSpan.Zero);
    var nextDay = new DateTimeOffset(2027, 3, 15, 4, 0, 0, TimeSpan.Zero);
    store.Add(new("pc", "PC", before, before.AddSeconds(2), 0.001, 0.002));
    store.Add(new("pc", "PC", inside, inside.AddSeconds(2), 0.008, null));
    store.Add(new("pc", "PC", endInside, endInside.AddSeconds(2), 0.004, 0.006));
    store.Add(new("pc", "PC", nextDay, nextDay.AddSeconds(2), 0.016, 0.016));
    store.Add(new("unknown", "Unknown PC", inside, inside.AddSeconds(2), null, null));
    store.Add(new("offline", "Offline PC", inside.AddDays(-7), inside.AddDays(-7).AddSeconds(2), 0.001, 0.001));
    var totals = store.Query(day, day, zone, nextDay);
    var total = totals.Single(x => x.PcId == "pc");
    Check(total.DownloadGb is { } down && Math.Abs(down - 0.012) < 0.000001, "day boundary and DST local date clipping");
    Check(total.UploadGb is { } up && Math.Abs(up - 0.006) < 0.000001 && total.UploadGap == TimeSpan.FromHours(23) - TimeSpan.FromSeconds(2), "null direction and unobserved periods are visible gaps, not zero");
    Check(total.DownloadGap == TimeSpan.FromHours(23) - TimeSpan.FromSeconds(4), "download gaps are measured independently");
    Check(totals.Single(x => x.PcId == "unknown") is { DownloadGb: null, UploadGb: null }, "both unknown directions stay unknown");
    Check(totals.Single(x => x.PcId == "offline") is { DownloadGb: null, UploadGb: null, DownloadGap: null, UploadGap: null },
        "offline PC with no samples in the selected period remains listed as no measurement");
    store.Flush();

    var restarted = new UsageHistoryStore(root);
    var loaded = restarted.Query(day, day, zone, nextDay).Single(x => x.PcId == "pc");
    Check(loaded == total, "persisted estimates and gaps survive restart");
    var week = restarted.Query(day.AddDays(-6), day, zone, nextDay).Single(x => x.PcId == "pc");
    Check(week.DownloadGb is { } weekDown && total.DownloadGb is { } dayDown && weekDown >= dayDown && Math.Abs(weekDown - dayDown - 0.001) < 0.000001,
        "seven-day range includes surrounding dates and the selected day");

    var rates = new UsageRateAccumulator();
    var t = DateTimeOffset.UtcNow;
    Check(UsageRateAccumulator.IsFreshSample(t, t.AddSeconds(5)) && !UsageRateAccumulator.IsFreshSample(t, t.AddSeconds(6)),
        "peer metrics expire after the freshness window");
    _ = rates.Observe("peer", "Peer", 8, 4, t, t);
    Check(rates.Observe("peer", "Peer", 8, 4, t, t.AddSeconds(2), true) is null, "stale duplicate peer sample is deduplicated");
    var stale = rates.Observe("peer", "Peer", null, null, t, t.AddSeconds(4), true);
    Check(stale is { DownloadGb: null, UploadGb: null }, "expired peer creates an explicit measurement gap");
    Check(rates.Observe("peer", "Peer", null, null, t, t.AddSeconds(6), true) is null, "repeated stale peer sample is not duplicated");
    var resumed = rates.Observe("peer", "Peer", 0, null, t.AddSeconds(8), t.AddSeconds(8), true);
    Check(resumed is { DownloadGb: null, UploadGb: null }, "resume after a gap does not bridge offline time");
    Check(new UsageRateAccumulator().Observe("peer", "Peer", 0, 0, t.AddSeconds(20), t.AddSeconds(20)) is null,
        "restart starts a fresh baseline without estimating downtime");

    var corruptRoot = Path.Combine(root, "corrupt");
    Directory.CreateDirectory(corruptRoot);
    var corruptPath = Path.Combine(corruptRoot, "usage-history.json");
    File.WriteAllText(corruptPath, "{broken");
    var corrupt = new UsageHistoryStore(corruptRoot);
    Check(corrupt.LoadError is not null, "corrupt history is reported");
    corrupt.Add(new("pc", "PC", inside, inside.AddSeconds(2), 0.01, 0.01));
    corrupt.Flush();
    Check(File.ReadAllText(corruptPath) == "{broken", "corrupt original is never overwritten");

    var current = DateTimeOffset.UtcNow;
    var currentStore = new UsageHistoryStore(Path.Combine(root, "current"));
    currentStore.Add(new("fast", "Fast PC", current.AddSeconds(-2), current, 2, 1.5));
    var currentDay = DateOnly.FromDateTime(current.UtcDateTime);
    var currentTotal = currentStore.Query(currentDay, currentDay, TimeZoneInfo.Utc, current).Single();
    Check(currentTotal.DownloadGb == 2 && currentTotal.UploadGb == 1.5 && currentTotal.DownloadGap == TimeSpan.Zero,
        "unfinished current bucket counts all measured bytes without scaling by wall-clock fraction");
    currentStore.Flush();
    Check(new UsageHistoryStore(Path.Combine(root, "current")).Query(currentDay, currentDay, TimeZoneInfo.Utc, current).Single() == currentTotal,
        "more than one GB in a bucket survives persistence");
    File.WriteAllText(corruptPath, "{\"Version\":3,\"Buckets\":[null]}");
    Check(new UsageHistoryStore(corruptRoot).LoadError is not null, "null records report corruption without crashing app startup");

    Console.WriteLine("Usage history checks passed.");
}
finally
{
    try { Directory.Delete(root, true); } catch (IOException) { }
}

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("Failed: " + name);
}
