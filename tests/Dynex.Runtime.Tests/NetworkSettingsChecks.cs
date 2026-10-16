using Dynex.Desktop;

internal static class NetworkSettingsChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        check(new UserSettings().AutoConnectLan, "automatic LAN discovery is enabled by default");
        using var profile = new TestProfile(measured: true);
        await using var runtime = new DynexRuntime(profile.Path, new ControlledSpeedTest(), _ => []);
        var original = runtime.Settings;
        await runtime.StartAsync();
        await runtime.SaveNetworkSettingsAsync(autoConnectLan: true);
        check(runtime.Settings == original with { AutoConnectLan = true } &&
            new SettingsStore(profile.Path).Load().AutoConnectLan,
            "immediate connection mode change persists only that preference");
        check(runtime.GetNetworkDiagnostics()?.Mode == "Automatisch", "mode change replaces the actual peer transport");
        await runtime.SaveNetworkSettingsAsync(directPeers: "10.0.2.2");
        check(runtime.Settings == original with { AutoConnectLan = true, DirectPeers = "10.0.2.2" },
            "connection check target update preserves all other settings and measured capacity");
        var saved = runtime.Settings;
        var rejected = false;
        try { await runtime.SaveNetworkSettingsAsync(directPeers: "1.1.1.1"); }
        catch (ArgumentException) { rejected = true; }
        check(rejected && runtime.Settings == saved && new SettingsStore(profile.Path).Load() == saved,
            "invalid peer addresses leave persisted and running network settings intact");
        var diagnostics = runtime.GetNetworkDiagnostics();
        using (var locked = new FileStream(Path.Combine(profile.Path, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await runtime.SaveNetworkSettingsAsync(directPeers: "10.0.2.2");
            var writeRejected = false;
            try { await runtime.SaveNetworkSettingsAsync(autoConnectLan: false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { writeRejected = true; }
            check(writeRejected && runtime.Settings.AutoConnectLan && runtime.GetNetworkDiagnostics()?.Mode == "Automatisch",
                "failed mode persistence preserves the currently active automatic transport");
        }
        check(runtime.GetNetworkDiagnostics()?.AcceptedHandshakes == diagnostics?.AcceptedHandshakes,
            "unchanged connection check does not rewrite settings or reconnect");

        var requested = new List<string>();
        var pings = await NetworkConnectionCheck.ProbePeersAsync(
            ["10.0.2.2", "10.0.2.2", "10.0.2.1", "1.1.1.1", "invalid", "192.168.1.4"], ["10.0.2.1"],
            CancellationToken.None, (address, _) =>
            {
                requested.Add(address);
                return Task.FromResult(address == "10.0.2.2" ? (long?)7 : null);
            });
        check(requested.SequenceEqual(new[] { "10.0.2.2", "192.168.1.4" }) && pings[0].RoundtripMs == 7 && pings[1].RoundtripMs is null,
            "ping probes only distinct private remote targets, retaining unknown versus numeric latency");
        check(pings[0].ToString().Contains("separat bestätigt") && pings[1].ToString().Contains("ICMP kann gesperrt"),
            "ping success never claims authenticated DYNEX pairing and missing ICMP never claims unreachable PC");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledObserved = false;
        try { await NetworkConnectionCheck.ProbePeersAsync(["10.0.2.2"], [], cancelled.Token, (_, _) => throw new Exception("must not probe")); }
        catch (OperationCanceledException) { cancelledObserved = true; }
        check(cancelledObserved, "cancelled network checks do not start a peer ping");
    }
}
