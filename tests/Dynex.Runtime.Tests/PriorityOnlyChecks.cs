using System.Text.Json;
using Dynex.Desktop;

internal static class PriorityOnlyChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        using var profile = new TestProfile(measured: true);
        var profileId = Guid.NewGuid().ToString("D");
        var legacy = new SettingsStore(profile.Path).Load() with
        {
            ManualLimitMbps = 25, UploadLimitEnabled = true, ManualUploadLimitMbps = 5,
            Adaptive = true, ReservePerGamerMbps = 10, NightFree = true, WeekendProfile = "Turnier",
            GameProcesses = "testgame", EarlyLimitGames = "testgame",
            ApplicationRules = [new("steam", 12, 3)],
            NetworkProfiles = [new(profileId, "Saved LAN", DateTimeOffset.UtcNow,
                new NetworkProfilePolicy { ManualLimitMbps = 4, UploadLimitEnabled = true, ManualUploadLimitMbps = 2 })]
        };
        // Write the actual old representation, bypassing the new migration on Save.
        File.WriteAllText(Path.Combine(profile.Path, "settings.json"), JsonSerializer.Serialize(legacy));
        var speed = new ControlledSpeedTest();
        await using (var runtime = new DynexRuntime(profile.Path, speed, _ => []))
        {
            CheckNoLimits(runtime.Settings, check, "upgrade");
            check(runtime.Settings.GameProcesses == "testgame" && runtime.Settings.EarlyLimitGames == "testgame" &&
                runtime.Settings.CapacityMbps == legacy.CapacityMbps && runtime.Settings.CapacityMeasuredAtUtc == legacy.CapacityMeasuredAtUtc,
                "upgrade preserves game recognition and existing diagnostic measurements");
            await runtime.StartAsync();
            var snapshot = await runtime.RefreshStatusAsync();
            check(speed.Calls == 0 && !runtime.IsSpeedTestRunning && !runtime.IsSpeedTestWaiting,
                "startup prioritization never needs automatic speedtest traffic");
            check(snapshot is { DriverActive: false, LimitMbps: null, UploadLimitMbps: null,
                    SharedDownloadBudgetMbps: null, ApplicationRulesActive: false } && runtime.PlannedDownloadLimitMbps is null,
                "runtime exposes no download, upload, application or shared budget despite old saved caps");
            check(snapshot.Devices.Count == 1 && snapshot.History.Count > 0 && snapshot.External.Count > 0,
                "device overview, measurements and ping history survive removal of limits");
            await runtime.ImportSettingsAsync(JsonSerializer.Serialize(new { Format = 1, Version = "1.0.15", Settings = legacy }));
            CheckNoLimits(runtime.Settings, check, "legacy import");
            CheckNoLimits(new SettingsStore(profile.Path).Load(), check, "persisted migration");
        }

        using var fixture = await CaptureFixture.CreateAsync();
        await fixture.Controller.ApplyAsync(25, CancellationToken.None, qosEnabled: true, uploadMbps: 5,
            appRules: [new("steam", 12, 3)]);
        // A command ACK can precede the first completed OS ownership snapshot.
        // Require the actual fresh status, rather than depending on task order.
        using (var ready = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            while (!fixture.Controller.Status.QosActive)
            {
                await Task.Delay(20, ready.Token);
                await fixture.Controller.ApplyAsync(null, ready.Token, qosEnabled: true);
            }
        check(fixture.Created == 1 && fixture.Controller.Status is { QosActive: true, Active: false, Mbps: null,
                UploadMbps: null, ApplicationRulesActive: false },
            "real controller/helper IPC retains priority while discarding legacy download/upload/app arguments");
        await fixture.Controller.ApplyAsync(25, CancellationToken.None, uploadMbps: 5, appRules: [new("steam", 12, 3)]);
        check(fixture.Created == 1 && fixture.Disposed == 1 && fixture.Controller.CaptureReleased,
            "cap-only legacy requests cannot keep packet capture alive after priority ends");
        await fixture.Controller.ApplyAsync(null, CancellationToken.None, observeGameTraffic: true);
        check(fixture.Created == 2 && fixture.Controller.Status is { ObservingGameTraffic: true, QosActive: false, Active: false },
            "passive game traffic observation still works without shaping or priority");
        await fixture.Controller.StopAsync(requireExit: true);
        check(fixture.Disposed == 2 && fixture.Controller.CaptureReleased &&
            await fixture.Helper.WaitAsync(TimeSpan.FromSeconds(8)) == 0,
            "priority-only helper retains acknowledged release and clean shutdown");
    }

    private static void CheckNoLimits(UserSettings settings, Action<bool, string> check, string operation)
    {
        check(settings.ManualLimitMbps is null && settings.ManualUploadLimitMbps is null &&
            !settings.UploadLimitEnabled && !settings.Adaptive && settings.ReservePerGamerMbps == 0 &&
            settings.ApplicationRules.Length == 0 && !settings.NightFree && settings.WeekendProfile == "",
            operation + " disables every legacy local shaping path");
        check(settings.NetworkProfiles.All(p => p.Policy.ManualLimitMbps is null &&
                p.Policy.ManualUploadLimitMbps is null && !p.Policy.UploadLimitEnabled && p.Policy.ReservePerGamerMbps == 0),
            operation + " also disables caps in inactive saved network profiles");
    }
}
