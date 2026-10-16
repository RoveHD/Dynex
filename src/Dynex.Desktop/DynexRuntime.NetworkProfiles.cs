using System.Diagnostics;

namespace Dynex.Desktop;

public sealed partial class DynexRuntime
{
    private Func<NetworkIdentity?> readNetworkIdentity = () => null;
    private bool networkProfilesEnabled;
    private int profileCheckRunning;
    private long profileCheckAt;
    private string? speedTestNetworkProfileId;
    private bool networkIdentityConfirmed;
    public string NetworkProfileStatus { get; private set; } = "Netzwerk wird ermittelt …";
    public string ActiveNetworkName => Settings.NetworkProfiles.FirstOrDefault(p => p.Id == Settings.ActiveNetworkProfileId)?.Name ?? "Noch nicht zugeordnet";

    // Called under lifecycle; persistence is one atomic settings.json replacement, including all profiles.
    private bool ApplyNetworkIdentity(NetworkIdentity? network)
    {
        if (network is null)
        {
            networkIdentityConfirmed = false;
            NetworkProfileStatus = "Windows-Netzwerk nicht eindeutig erkannt. Gespeicherte Profile bleiben erhalten.";
            return false;
        }
        if (network.Id == Settings.ActiveNetworkProfileId)
        {
            networkIdentityConfirmed = true;
            NetworkProfileStatus = "Aktiv: " + ActiveNetworkName;
            return false;
        }
        var next = SettingsStore.Normalize(NetworkProfiles.Activate(Settings, network, DateTimeOffset.UtcNow));
        store.Save(next);
        Settings = next;
        networkIdentityConfirmed = true;
        activePlanner = null; plannerPolicy = null;
        lock (history) history.Clear();
        NetworkProfileStatus = "Aktiv: " + ActiveNetworkName + (next.CapacityMeasuredAtUtc is null ? " · Speedtest steht aus" : " · Messwerte wiederhergestellt");
        return true;
    }

    private void ScheduleNetworkProfileCheck()
    {
        if (!networkProfilesEnabled) return;
        if (profileCheckAt != 0 && Stopwatch.GetElapsedTime(profileCheckAt) < TimeSpan.FromSeconds(10)) return;
        if (Interlocked.Exchange(ref profileCheckRunning, 1) != 0) return;
        profileCheckAt = Stopwatch.GetTimestamp();
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshNetworkProfileAsync(reconnect: false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { NetworkProfileStatus = "Netzwerkprofil konnte nicht gewechselt werden: " + ex.Message; }
            finally { Interlocked.Exchange(ref profileCheckRunning, 0); }
        });
    }

    // Cancel only the test belonging to the superseded network. A concurrent refresh
    // may already have started a fresh test for the observed network.
    private Task? CancelSupersededNetworkTest(NetworkIdentity? network)
    {
        lock (speedTestSync)
        {
            if (network is null || speedTestNetworkProfileId == network.Id || speedTestTask is not { IsCompleted: false } pending)
                return null;
            speedTestCancellation?.Cancel();
            return pending;
        }
    }

    private async Task RefreshNetworkProfileAsync(bool reconnect)
    {
        var observed = await Task.Run(readNetworkIdentity);
        var interrupted = false;
        if (CancelSupersededNetworkTest(observed) is { } ending)
        { await ending; interrupted = true; }
        while (true)
        {
            Task? finishing = null;
            await lifecycle.WaitAsync();
            try
            {
                if (!started || stopping) return;
                // The pre-gate probe may have waited behind a save or the entire speedtest.
                // Never apply its old identity after acquiring the lifecycle gate.
                var current = readNetworkIdentity();
                var changed = current is not null && current.Id != Settings.ActiveNetworkProfileId;
                if (changed && CancelSupersededNetworkTest(current) is { } pending)
                    finishing = pending;
                else
                {
                    if (!changed && (!reconnect || IsSpeedTestRunning || IsSpeedTestWaiting))
                    {
                        var wasConfirmed = networkIdentityConfirmed;
                        ApplyNetworkIdentity(current);
                        // The network may have returned to A while its old waiting test
                        // was canceled, or become identifiable after an unknown startup.
                        if (interrupted || !wasConfirmed) StartNetworkSpeedTestIfNeeded(current);
                        return;
                    }
                    ApplyNetworkIdentity(current);
                    await StopWorkerAsync();
                    // Also reset a helper stopped by its network-address watchdog, even
                    // when Windows retained the same network GUID after standby/DHCP.
                    await shaper.StopAsync();
                    await StartWorkerAsync();
                    StartNetworkSpeedTestIfNeeded(current);
                    return;
                }
            }
            finally { lifecycle.Release(); }
            // A waiting test's finally needs this same gate. Await the identified task
            // only after releasing it, then verify the network again before applying.
            await finishing!;
            interrupted = true;
        }
    }

    private void StartNetworkSpeedTestIfNeeded(NetworkIdentity? current)
    {
        if (automaticSpeedTestEnabled && !Settings.AutomaticSpeedTestAttempted && Settings.CapacityMeasuredAtUtc is null &&
            (!networkProfilesEnabled || current is not null && current.Id == Settings.ActiveNetworkProfileId) &&
            speedTestTask is not { IsCompleted: false }) _ = BeginSpeedTest(automatic: true);
    }
    public async Task RenameNetworkProfileAsync(string id, string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 128 || name.Any(char.IsControl)) throw new ArgumentException("Netzwerkname: 1–128 sichtbare Zeichen.");
        await lifecycle.WaitAsync();
        try
        {
            if (!Settings.NetworkProfiles.Any(p => p.Id == id)) throw new ArgumentException("Netzwerkprofil nicht gefunden.");
            var next = Settings with { NetworkProfiles = Settings.NetworkProfiles.Select(p => p.Id == id ? p with { Name = name } : p).ToArray() };
            store.Save(next); Settings = next;
            if (id == Settings.ActiveNetworkProfileId && networkIdentityConfirmed)
                NetworkProfileStatus = "Aktiv: " + ActiveNetworkName;
        }
        finally { lifecycle.Release(); }
    }
}
