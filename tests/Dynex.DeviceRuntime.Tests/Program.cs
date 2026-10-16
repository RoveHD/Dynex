using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using Dynex.Core;
using Dynex.Desktop;

var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); Console.WriteLine("PASS " + name); checks++; }
var directory = Path.Combine(Path.GetTempPath(), "Dynex-device-runtime-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(new UserSettings { CapacityMeasuredAtUtc = DateTimeOffset.UtcNow, DriverEnabled = false, AutoStart = false }));
    File.WriteAllText(Path.Combine(directory, "devices.json"), "invalid registry sentinel");
    await using (var runtime = new DynexRuntime(directory))
    {
        Check(runtime.DeviceRegistryError?.Contains("Netzwerk bleibt gesperrt") == true, "corrupt persisted blocks do not crash runtime construction and report fail-closed status");
        await runtime.StartAsync();
        await Task.Delay(350);
        Check(runtime.DeviceRegistryError?.Contains("Netzwerk bleibt gesperrt") == true, "first observe loop cannot clear initial device-policy load error");
        var diagnostics = runtime.GetNetworkDiagnostics();
        Check(diagnostics is { SentDatagrams: 0, AcceptedStatusDatagrams: 0, AcceptedHandshakes: 0, LearnedEndpoints: 0 }, "isolated runtime starts with all peer traffic disabled on corrupt registry");
        var probe = await runtime.TestPeerConnectionAsync(Guid.NewGuid());
        Check(!probe.Succeeded && probe.Stages.All(s => s.State == ConnectionTestState.Unknown), "invalid device policy prevents an explicit runtime probe");
    }
    Check(File.ReadAllText(Path.Combine(directory, "devices.json")) == "invalid registry sentinel", "runtime shutdown preserves corrupt file and saved-policy evidence for repair");
    File.Delete(Path.Combine(directory, "devices.json"));
    var signing = AutoLanIdentity.GenerateSigningKey();
    using var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    var port = ((IPEndPoint)reservation.LocalEndPoint!).Port; reservation.Close();
    var remoteId = AutoLanIdentity.GetDeviceId(signing);
    var known = new KnownDevice(remoteId, "Fixture PC", "Spiele-PC", DateTimeOffset.UtcNow, "127.0.0.1", "1.0.8", RemotePort: port);
    File.WriteAllText(Path.Combine(directory, "devices.json"), JsonSerializer.Serialize(new[] { known }));
    await using var remote = new AutoLanPeerService(new DynexSettings { DeviceId = remoteId, DeviceName = "Fixture PC", Port = port,
        LoopbackOnly = true, AppVersion = typeof(DynexRuntime).Assembly.GetName().Version!.ToString(3) }, signing);
    await remote.StartAsync();
    await using (var healthy = new DynexRuntime(directory))
    {
        await healthy.StartAsync();
        var full = await healthy.TestPeerConnectionAsync(remoteId);
        if (!full.Succeeded) Console.WriteLine(JsonSerializer.Serialize(new { full, Diagnostics = healthy.GetNetworkDiagnostics(), Remote = remote.GetNetworkDiagnostics(), healthy.Settings.AutoConnectLan }));
        Check(full.Succeeded && full.Stages.Count == 8, "runtime combines read-only local diagnosis with real targeted authenticated probe to saved offline peer");
        Check(full.Stages[0].State == ConnectionTestState.Unknown && !full.Stages[0].Required && full.Stages[1].State == ConnectionTestState.Unknown,
            "isolated local firewall/profile remain explicitly unknown while proven bidirectional UDP test passes");
        await healthy.SetDevicePreferencesAsync(remoteId, "Arbeits-PC", DeviceDisposition.Blocked);
        var blocked = await healthy.TestPeerConnectionAsync(remoteId);
        Check(!blocked.Succeeded && healthy.Devices.Single().DisplayName == "Arbeits-PC", "runtime preference save applies persistent block and local alias immediately");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var probe = await healthy.TestPeerConnectionAsync(Guid.NewGuid(), cancelled.Token);
        Check(probe.Stages.All(s => s.State == ConnectionTestState.Cancelled), "runtime cancels bounded local network diagnosis explicitly");
    }
    await using (var restarted = new DynexRuntime(directory))
        Check(restarted.Devices.Single().Device.Disposition == DeviceDisposition.Blocked && restarted.Devices.Single().DisplayName == "Arbeits-PC" && !restarted.Devices.Single().Online,
            "new runtime restores persistent local alias and block with honest offline state");
}
finally
{
    foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
    Directory.Delete(directory);
}
Console.WriteLine($"{checks} checks passed.");
