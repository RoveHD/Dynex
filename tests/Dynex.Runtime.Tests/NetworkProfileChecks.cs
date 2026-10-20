using Dynex.Desktop;
using Dynex.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class NetworkProfileChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var a = new NetworkIdentity(Guid.NewGuid().ToString("D"), "Zuhause");
        var b = new NetworkIdentity(Guid.NewGuid().ToString("D"), "Hotspot");
        var original = new UserSettings { CapacityMbps = 80, CapacityMeasuredAtUtc = now, AutomaticSpeedTestAttempted = true,
            Profile = "Turnier", BackgroundPercent = 40, ManualLimitMbps = 30, UploadCapacityMbps = 12,
            DirectPeers = "10.0.0.4", AutoConnectLan = false, GameProcesses = "ExampleGame",
            ApplicationRules = [new("steam", 30)] };
        var home = SettingsStore.Normalize(NetworkProfiles.Activate(original, a, now));
        check(home.CapacityMbps == 80 && home.ActiveNetworkProfileId == a.Id && home.BackgroundPercent == 40,
            "profile migration retains measured capacity and selected policy on first verified network");
        var hotspot = SettingsStore.Normalize(NetworkProfiles.Activate(home, b, now.AddMinutes(1)));
        check(hotspot.CapacityMeasuredAtUtc is null && !hotspot.AutomaticSpeedTestAttempted && hotspot.DirectPeers == "" && hotspot.AutoConnectLan,
            "new network has its own unmeasured capacity and automatic discovery without old direct peers");
        check(hotspot.GameProcesses == original.GameProcesses && hotspot.ApplicationRules.Length == 0,
            "network switch preserves computer-wide games while removing obsolete program rules");
        hotspot = SettingsStore.Normalize(hotspot with { CapacityMbps = 45, CapacityMeasuredAtUtc = now.AddMinutes(2), AutomaticSpeedTestAttempted = true });
        var restored = SettingsStore.Normalize(NetworkProfiles.Activate(hotspot, a, now.AddMinutes(3)));
        check(restored.CapacityMbps == 80 && restored.ManualLimitMbps is null && restored.UploadCapacityMbps == 12 && restored.DirectPeers == "10.0.0.4",
            "return to home restores its measured speeds and connection settings");
        check(NetworkProfiles.Activate(restored, b, now.AddMinutes(4)).CapacityMbps == 45,
            "each network keeps independent latest measurement");
        using var profile = new TestProfile(measured: true);
        var store = new SettingsStore(profile.Path);
        store.Save(restored);
        var settingsPath = System.IO.Path.Combine(profile.Path, "settings.json");
        var legacy = JsonNode.Parse(File.ReadAllText(settingsPath))!;
        legacy["SharedDownloadMaximumMbps"] = 25;
        foreach (var saved in legacy["NetworkProfiles"]!.AsArray()) saved!["Policy"]!["SharedDownloadMaximumMbps"] = 25;
        File.WriteAllText(settingsPath, legacy.ToJsonString());
        var loaded = store.Load();
        check(loaded.NetworkProfiles.Length == 2 && loaded.NetworkProfiles.Single(p => p.Id == b.Id).Policy.CapacityMbps == 45,
            "atomic settings persistence restores all network policies");
        check(!JsonSerializer.Serialize(loaded).Contains("SharedDownloadMaximumMbps"),
            "loading old settings removes obsolete maximum from current settings and every saved network policy");
        var restoredLegacy = SettingsStore.Normalize(NetworkProfiles.Activate(
            SettingsStore.Normalize(NetworkProfiles.Activate(loaded, b, now)), a, now));
        check(restoredLegacy.ManualLimitMbps is null && restoredLegacy.ReservePerGamerMbps == 0 &&
            restoredLegacy.NetworkProfiles.All(p => p.Policy.ManualLimitMbps is null && !p.Policy.UploadLimitEnabled) && restoredLegacy.CapacityMeasuredAtUtc == now,
            "network switches preserve measured diagnostic capacity and cannot resurrect any saved shaping policy");
        store.Save(restoredLegacy);
        check(!File.ReadAllText(settingsPath).Contains("SharedDownloadMaximumMbps"),
            "saving migrated settings strips obsolete maximum from disk and inactive profiles");

        // Both simulated networks use loopback targets; these tests never probe the actual LAN/Internet.
        var local = new UserSettings { CapacityMbps = 80, CapacityMeasuredAtUtc = now, AutomaticSpeedTestAttempted = true,
            DriverEnabled = false, AutoStart = false, AutoConnectLan = false, RouterTarget = "127.0.0.1", ExternalTarget = "127.0.0.1" };
        var configured = NetworkProfiles.Activate(local, a, now);
        configured = NetworkProfiles.Activate(configured, b, now) with { CapacityMbps = 45, CapacityMeasuredAtUtc = now,
            AutomaticSpeedTestAttempted = true, AutoConnectLan = false, RouterTarget = "127.0.0.1", ExternalTarget = "127.0.0.1" };
        configured = NetworkProfiles.Activate(configured, a, now);
        store.Save(configured);
        NetworkIdentity current = a;
        var speed = new ControlledSpeedTest();
        await using var runtime = new DynexRuntime(profile.Path, speed, _ => [], networkIdentityReader: () => current);
        await runtime.StartAsync();
        var draft = runtime.Settings;
        current = b;
        await runtime.ReconnectNetworkAsync();
        check(runtime.Settings.ActiveNetworkProfileId == b.Id && runtime.Settings.CapacityMbps == 45, "runtime selects matching network profile on reconnect");
        try { await runtime.SaveSettingsAsync(draft); check(false, "stale network draft rejected"); }
        catch (InvalidOperationException) { check(true, "stale network draft cannot overwrite current network"); }
        await runtime.RenameNetworkProfileAsync(b.Id, "Mein Hotspot");
        check(runtime.ActiveNetworkName == "Mein Hotspot", "network aliases persist independently of Windows names");
        var backup = runtime.ExportSettings();
        check(!backup.Contains(a.Id) && !backup.Contains(b.Id), "portable backup excludes network identifiers and saved network measurements");
        var oldBackup = JsonNode.Parse(backup)!;
        oldBackup["Settings"]!["SharedDownloadMaximumMbps"] = 25;
        oldBackup["Settings"]!["NetworkProfiles"] = legacy["NetworkProfiles"]!.DeepClone();
        await runtime.ImportSettingsAsync(oldBackup.ToJsonString());
        check(runtime.Settings.CapacityMbps == 45 && runtime.Settings.ManualLimitMbps is null && runtime.Settings.ApplicationRules.Length == 0 &&
            !runtime.ExportSettings().Contains("SharedDownloadMaximumMbps") && !File.ReadAllText(settingsPath).Contains("SharedDownloadMaximumMbps"),
            "importing a legacy maximum and saved profiles preserves local measurement and priority-only policy without writing obsolete limits");
        var measurement = runtime.MeasureCapacityAsync();
        var request = await speed.NextAsync().WaitAsync(TimeSpan.FromSeconds(12));
        current = a;
        request.Complete(999);
        await measurement.WaitAsync(TimeSpan.FromSeconds(12));
        check(runtime.Settings.CapacityMbps == 45, "speed test result from changing network is discarded");
        await runtime.ReconnectNetworkAsync();
        check(runtime.Settings.CapacityMbps == 80, "original network capacity survives canceled cross-network speed test");
        await runtime.StopAsync();
        await NetworkProfileRaceChecks.RunAsync(check);
    }
}
