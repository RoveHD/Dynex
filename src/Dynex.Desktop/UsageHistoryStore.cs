using System.IO;
using System.Text.Json;

namespace Dynex.Desktop;

internal sealed record UsageInterval(string PcId, string PcName, DateTimeOffset StartUtc,
    DateTimeOffset EndUtc, double? DownloadGb, double? UploadGb);
internal sealed record UsageTotal(string PcId, string PcName, double? DownloadGb, double? UploadGb,
    TimeSpan? DownloadGap, TimeSpan? UploadGap);

/// <summary>Persists bounded UTC quarter-hour aggregates of short rate-derived estimates.</summary>
internal sealed class UsageHistoryStore
{
    private const int Version = 3;
    private const int MaximumBuckets = 30_000;
    private const int BucketMinutes = 15;
    private const double BucketSeconds = BucketMinutes * 60;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private readonly string path;
    private readonly Dictionary<(string PcId, long Minute), UsageBucket> buckets = [];
    private readonly object sync = new();
    private readonly string? loadError;
    private string? writeError;
    private bool retentionLimited;
    private long lastPruneUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
    private bool dirty;

    internal UsageHistoryStore(string directory)
    {
        path = Path.Combine(directory, "usage-history.json");
        if (!File.Exists(path)) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 64 * 1024 * 1024) throw new InvalidDataException("Verlaufsdatei ist zu groß.");
            var data = JsonSerializer.Deserialize<UsageHistoryData>(File.ReadAllText(path));
            if (data is null || data.Version != Version || data.Buckets is null) throw new InvalidDataException("Unbekanntes oder ungültiges Verlaufsformat.");
            if (data.Buckets.Any(b => !IsValid(b))) throw new InvalidDataException("Ungültige Messwerte im gespeicherten Verlauf.");
            retentionLimited = data.RetentionLimited;
            var cutoff = DateTimeOffset.UtcNow.Add(-Retention).UtcTicks / TimeSpan.TicksPerMinute;
            var valid = data.Buckets.Where(IsValid).Where(x => x.Minute >= cutoff).ToArray();
            if (valid.Length > MaximumBuckets) retentionLimited = true;
            foreach (var bucket in valid.OrderBy(x => x.Minute).TakeLast(MaximumBuckets))
                buckets[(bucket.PcId, bucket.Minute)] = bucket;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { loadError = ex.Message; }
    }

    internal string? Error => loadError ?? writeError;
    internal string? LoadError => loadError;
    internal bool RetentionLimited => retentionLimited;

    internal void Add(UsageInterval interval)
    {
        if (!IsValid(interval)) return;
        lock (sync)
        {
            var duration = (interval.EndUtc - interval.StartUtc).TotalSeconds;
            var cursor = interval.StartUtc;
            var createdBucket = false;
            while (cursor < interval.EndUtc)
            {
                var minute = cursor.UtcTicks / TimeSpan.TicksPerMinute / BucketMinutes * BucketMinutes;
                var minuteStart = new DateTimeOffset(minute * TimeSpan.TicksPerMinute, TimeSpan.Zero);
                var segmentEnd = interval.EndUtc < minuteStart.AddMinutes(BucketMinutes) ? interval.EndUtc : minuteStart.AddMinutes(BucketMinutes);
                var seconds = (segmentEnd - cursor).TotalSeconds;
                var fraction = seconds / duration;
                var key = (interval.PcId, minute);
                buckets.TryGetValue(key, out var existing);
                createdBucket |= existing is null;
                existing ??= new(interval.PcId, interval.PcName, minute, cursor.UtcTicks, segmentEnd.UtcTicks, 0, 0, null, null, 0, 0);
                existing = existing with
                {
                    PcName = interval.PcName,
                    FirstUtcTicks = Math.Min(existing.FirstUtcTicks, cursor.UtcTicks),
                    LastUtcTicks = Math.Max(existing.LastUtcTicks, segmentEnd.UtcTicks),
                    DownloadGb = AddAmount(existing.DownloadGb, interval.DownloadGb, fraction),
                    UploadGb = AddAmount(existing.UploadGb, interval.UploadGb, fraction),
                    DownloadMeasuredSeconds = existing.DownloadMeasuredSeconds + (interval.DownloadGb is null ? 0 : seconds),
                    UploadMeasuredSeconds = existing.UploadMeasuredSeconds + (interval.UploadGb is null ? 0 : seconds),
                    DownloadGapSeconds = existing.DownloadGapSeconds + (interval.DownloadGb is null ? seconds : 0),
                    UploadGapSeconds = existing.UploadGapSeconds + (interval.UploadGb is null ? seconds : 0)
                };
                buckets[key] = existing;
                cursor = segmentEnd;
            }
            if (createdBucket && (buckets.Count > MaximumBuckets || DateTimeOffset.UtcNow.UtcTicks - lastPruneUtcTicks >= TimeSpan.TicksPerMinute * BucketMinutes))
                Prune();
            dirty = true;
        }
    }

    internal IReadOnlyList<UsageTotal> Query(DateOnly from, DateOnly through, TimeZoneInfo? zone = null, DateTimeOffset? now = null)
    {
        zone ??= TimeZoneInfo.Local;
        var start = LocalMidnight(from, zone).ToUniversalTime();
        var end = LocalMidnight(through.AddDays(1), zone).ToUniversalTime();
        now ??= DateTimeOffset.UtcNow;
        if (through >= DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now.Value, zone).DateTime) && now < end) end = now.Value;
        lock (sync)
        {
            return buckets.Values.GroupBy(b => b.PcId, StringComparer.Ordinal).Select(group =>
            {
                var all = group.ToArray();
                var items = all.Where(b => b.LastUtcTicks > start.UtcTicks && b.FirstUtcTicks < end.UtcTicks).ToArray();
                var name = all.OrderByDescending(x => x.Minute).First().PcName;
                if (items.Length == 0) return new UsageTotal(group.Key, name, null, null, null, null);
                var firstObserved = new DateTimeOffset(items.Min(x => x.FirstUtcTicks), TimeSpan.Zero);
                var lastObserved = new DateTimeOffset(items.Max(x => x.LastUtcTicks), TimeSpan.Zero);
                var first = firstObserved > start ? firstObserved : start;
                var last = lastObserved < end ? lastObserved : end;
                // A currently selected period remains visibly incomplete through now for PCs that stopped reporting.
                if (through >= DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now.Value, zone).DateTime) && last < end) last = end;
                double? down = null, up = null;
                var downMeasured = 0d; var upMeasured = 0d;
                foreach (var item in items)
                {
                    var observedStart = new DateTimeOffset(item.FirstUtcTicks, TimeSpan.Zero);
                    var observedEnd = new DateTimeOffset(item.LastUtcTicks, TimeSpan.Zero);
                    var overlap = Clip(observedStart, observedEnd, start, end).TotalSeconds;
                    var fraction = overlap / (observedEnd - observedStart).TotalSeconds;
                    if (item.DownloadGb is { } d) down = (down ?? 0) + d * fraction;
                    if (item.UploadGb is { } u) up = (up ?? 0) + u * fraction;
                    downMeasured += Math.Min(overlap, item.DownloadMeasuredSeconds * fraction);
                    upMeasured += Math.Min(overlap, item.UploadMeasuredSeconds * fraction);
                }
                var observed = Math.Max(0, (last - first).TotalSeconds);
                return new UsageTotal(group.Key, name, down, up,
                    TimeSpan.FromSeconds(Math.Max(0, observed - downMeasured)),
                    TimeSpan.FromSeconds(Math.Max(0, observed - upMeasured)));
            }).OrderBy(x => x.PcName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
    }

    internal void Flush()
    {
        lock (sync)
        {
            if (!dirty || loadError is not null) return;
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temp = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, new UsageHistoryData(Version, retentionLimited, buckets.Values.OrderBy(x => x.Minute).ToArray())); stream.Flush(true); }
                File.Move(temp, path, true);
                dirty = false;
                writeError = null;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
                writeError = ex.Message;
                throw;
            }
        }
    }

    private void Prune()
    {
        var cutoffMinute = DateTimeOffset.UtcNow.Add(-Retention).UtcTicks / TimeSpan.TicksPerMinute;
        foreach (var key in buckets.Keys.Where(k => k.Minute < cutoffMinute).ToArray()) buckets.Remove(key);
        if (buckets.Count > MaximumBuckets)
        {
            retentionLimited = true;
            foreach (var key in buckets.Keys.OrderBy(k => k.Minute).Take(buckets.Count - MaximumBuckets).ToArray()) buckets.Remove(key);
        }
        lastPruneUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
    }
    private static double? AddAmount(double? prior, double? amount, double fraction) => amount is null
        ? prior : (prior ?? 0) + amount.Value * fraction;
    private static DateTimeOffset BucketStart(UsageBucket b) => new(b.Minute * TimeSpan.TicksPerMinute, TimeSpan.Zero);
    private static DateTimeOffset BucketEnd(UsageBucket b) => BucketStart(b).AddMinutes(BucketMinutes);
    private static DateTimeOffset LocalMidnight(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
    private static TimeSpan Clip(DateTimeOffset a, DateTimeOffset b, DateTimeOffset start, DateTimeOffset end) =>
        (b < end ? b : end) - (a > start ? a : start);
    private static bool IsValid(UsageInterval? x) => x is not null && x.PcId is { Length: > 0 and <= 100 } && x.PcName is { Length: > 0 and <= 100 } &&
        x.EndUtc > x.StartUtc && (x.EndUtc - x.StartUtc) <= TimeSpan.FromSeconds(10) && Valid(x.DownloadGb, 125) && Valid(x.UploadGb, 125);
    private static bool IsValid(UsageBucket? x) => x is not null && x.PcId is { Length: > 0 and <= 100 } && x.PcName is { Length: > 0 and <= 100 } &&
        x.Minute > 0 && x.Minute < DateTime.MaxValue.Ticks / TimeSpan.TicksPerMinute - BucketMinutes &&
        x.Minute % BucketMinutes == 0 && Valid(x.DownloadGb, 11250) && Valid(x.UploadGb, 11250) &&
        x.FirstUtcTicks >= x.Minute * TimeSpan.TicksPerMinute &&
        x.LastUtcTicks <= x.Minute * TimeSpan.TicksPerMinute + TimeSpan.TicksPerMinute * BucketMinutes &&
        x.LastUtcTicks > x.FirstUtcTicks &&
        x.DownloadMeasuredSeconds >= 0 && x.DownloadMeasuredSeconds <= BucketSeconds &&
        x.UploadMeasuredSeconds >= 0 && x.UploadMeasuredSeconds <= BucketSeconds &&
        x.DownloadGapSeconds >= 0 && x.DownloadGapSeconds <= BucketSeconds && x.UploadGapSeconds >= 0 && x.UploadGapSeconds <= BucketSeconds;
    private static bool Valid(double? value, double maximum) => value is null || double.IsFinite(value.Value) && value.Value >= 0 && value.Value <= maximum;
    private sealed record UsageBucket(string PcId, string PcName, long Minute, long FirstUtcTicks, long LastUtcTicks,
        double DownloadGapSeconds, double UploadGapSeconds, double? DownloadGb, double? UploadGb,
        double DownloadMeasuredSeconds, double UploadMeasuredSeconds);
    private sealed record UsageHistoryData(int Version, bool RetentionLimited, UsageBucket[] Buckets);
}
