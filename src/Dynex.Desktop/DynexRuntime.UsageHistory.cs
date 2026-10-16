using System.Diagnostics;
using System.IO;
using Dynex.Core;
using System.Windows;

namespace Dynex.Desktop;

public sealed partial class DynexRuntime
{
    private readonly UsageRateAccumulator usageAccumulator = new();
    private UsageHistoryStore? usageHistory;
    private long usageFlushTick;

    private void InitializeUsageHistory()
    {
        usageHistory = new UsageHistoryStore(store.DirectoryPath);
    }

    /// <summary>Opens the local persistent estimate history for daily or seven-day review.</summary>
    public void ShowUsageHistory(Window? owner = null)
    {
        if (usageHistory is null) return;
        var window = new UsageHistoryWindow(usageHistory);
        if (owner is not null) window.Owner = owner;
        window.Show();
        window.Activate();
    }

    private void RecordUsageSample(NetworkMonitor.Measurement local, IReadOnlyList<PeerAdvertisement> peersNow, DateTimeOffset now)
    {
        if (usageHistory is null) return;
        var mode = Settings.AutoConnectLan ? "lan" : "group";
        var ownId = Settings.AutoConnectLan
            ? AutoLanIdentity.GetDeviceId(identity.SigningPrivateKeyBase64!) : identity.DeviceId;
        var current = new Dictionary<string, UsageObservation>(StringComparer.Ordinal)
        {
            [$"{mode}:{ownId:N}"] = new($"{Settings.DeviceName} · dieser PC", local.Down, local.Up, now)
        };
        var aliases = Devices.ToDictionary(d => d.DeviceId, d => d.DisplayName);
        foreach (var peer in peersNow)
        {
            var fresh = UsageRateAccumulator.IsFreshSample(peer.LastSeenUtc, now);
            current[$"{mode}:{peer.DeviceId:N}"] = new(aliases.GetValueOrDefault(peer.DeviceId, peer.DeviceName),
                fresh ? peer.DownloadMbps : null, fresh ? peer.UploadMbps : null, peer.LastSeenUtc);
        }

        foreach (var (id, observation) in current)
        {
            var interval = usageAccumulator.Observe(id, observation.Name, observation.DownMbps, observation.UpMbps,
                observation.SourceUtc, now, id != $"{mode}:{ownId:N}");
            if (interval is not null) usageHistory.Add(interval);
        }

        if (usageFlushTick == 0 || Stopwatch.GetElapsedTime(usageFlushTick) >= TimeSpan.FromMinutes(5))
        {
            usageFlushTick = Stopwatch.GetTimestamp();
            try { usageHistory.Flush(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Keep buffered estimates for the next flush. */ }
        }
    }

    private void FlushUsageHistory()
    {
        try { usageHistory?.Flush(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Shutdown must not block peer cleanup. */ }
        usageAccumulator.Clear();
    }

    private sealed record UsageObservation(string Name, double? DownMbps, double? UpMbps, DateTimeOffset SourceUtc);
}
