#if DEBUG
using System.Net;
using System.Net.Sockets;
using Dynex.Core;
using Dynex.Mobile;
using Dynex.Mobile.Tests;
using global::Android.App;
using global::Android.OS;
using global::Android.Widget;

namespace Dynex.Android;

// Explicit test entry point, omitted from release builds and manifests.
[Activity(Name = "de.dynex.mobile.DebugSelfTestActivity", Exported = true)]
public sealed class DebugSelfTestActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var label = new TextView(this) { Text = "DYNEX · isolierte Android-Prüfung läuft" };
        SetContentView(label);
        var resultFile = Path.Combine(FilesDir!.AbsolutePath, "self-test.txt");
        File.WriteAllText(resultFile, "TEST_ACTIVITY_STARTED\n");
        global::Android.Util.Log.Info("DYNEX-TEST", "Test activity created");
        _ = Task.Run(async () =>
        {
            var output = new List<string>();
            void Report(string line)
            {
                lock (output) output.Add(line);
                File.AppendAllText(resultFile, line + "\n");
            }
            try
            {
                Report("TEST_WORKER_STARTED");
                await DebugRuntimeDiagnostics.RunAsync(Report).ConfigureAwait(false);
                await MobileProxyChecks.RunAsync(["--require-half-close"], Report);
                await CheckCoreInteropAsync(Report);
                Report("ANDROID_SELF_TEST_PASS");
            }
            catch (Exception error) { Report("ANDROID_SELF_TEST_FAIL: " + error); }
            RunOnUiThread(() => label.Text = output.Last());
        }).ContinueWith(task => File.AppendAllText(resultFile, "WORKER_FAULT: " + task.Exception),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private static async Task CheckCoreInteropAsync(Action<string> report)
    {
        var desktopKey = AutoLanIdentity.GenerateSigningKey();
        var phoneKey = AutoLanIdentity.GenerateSigningKey();
        int Port()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)socket.LocalEndPoint!).Port;
        }
        var a = Port(); var b = Port();
        while (a == b) b = Port();
        var desktopSettings = new DynexSettings
        {
            DeviceId = AutoLanIdentity.GetDeviceId(desktopKey), DeviceName = "Isolierter PC",
            LoopbackOnly = true, Port = a, DirectPeers = [new(IPAddress.Loopback, b)],
            HeartbeatInterval = TimeSpan.FromMilliseconds(250), PeerTtl = TimeSpan.FromSeconds(2), LineMbps = 200
        };
        var phoneSettings = desktopSettings with { DeviceId = AutoLanIdentity.GetDeviceId(phoneKey), DeviceName = "Android",
            Port = b, DirectPeers = [new(IPAddress.Loopback, a)], CapacityMeasured = false };
        await using var desktop = new AutoLanPeerService(desktopSettings, desktopKey);
        await using var phone = new AutoLanPeerService(phoneSettings, phoneKey,
            () => AutoLanNetworkSnapshot.Empty, lowPowerDiscovery: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await desktop.StartAsync(timeout.Token); await phone.StartAsync(timeout.Token);
        desktop.Publish(true); phone.Publish(false);
        while (!phone.Snapshot().Any(p => p.IsGaming && p.BudgetProposal is not null)) await Task.Delay(25, timeout.Token);
        var decision = MobileLimitPolicy.Plan(phoneSettings.DeviceId, phone.Snapshot());
        if (decision.DownloadLimitMbps is not > 0) throw new InvalidOperationException("Gaming must plan a mobile limit.");
        report("PASS: Android crypto interoperates with desktop Core; authenticated gaming plans a real budget.");
        while (desktop.Snapshot().Count == 0) await Task.Delay(25, timeout.Token);
        var remotePhone = desktop.Snapshot().Single();
        if (remotePhone.IsGaming || remotePhone.BudgetProposal is not null || remotePhone.DownloadMbps is not null)
            throw new InvalidOperationException("Phone must not advertise invented gaming/capacity/traffic.");
        report("PASS: Mobile advertisement has no gaming, capacity or fabricated throughput.");
        desktop.Publish(false);
        while (phone.Snapshot().Any(p => p.IsGaming)) await Task.Delay(25, timeout.Token);
        if (MobileLimitPolicy.Plan(phoneSettings.DeviceId, phone.Snapshot()).DownloadLimitMbps is not null)
            throw new InvalidOperationException("Game end must clear limit.");
        desktop.Publish(true);
        while (!phone.Snapshot().Any(p => p.IsGaming)) await Task.Delay(25, timeout.Token);
        await desktop.StopAsync();
        while (phone.Snapshot().Count != 0) await Task.Delay(25, timeout.Token);
        if (MobileLimitPolicy.Plan(phoneSettings.DeviceId, phone.Snapshot()).DownloadLimitMbps is not null)
            throw new InvalidOperationException("Disconnected desktop must clear limit.");
        report("PASS: Game end and peer expiry release the mobile limit.");
    }
}
#endif
