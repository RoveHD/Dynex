using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dynex.Core;
using Dynex.Desktop;

var checks = 0;
void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); Console.WriteLine("PASS " + description); checks++; }
static int FreePort() { using var socket = BoundSocket(); return ((IPEndPoint)socket.LocalEndPoint!).Port; }
static Socket BoundSocket() { var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); return socket; }
static async Task Until(Func<bool> condition)
{
    var clock = Stopwatch.StartNew();
    while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Fixture did not converge."); await Task.Delay(20); }
}

foreach (var automatic in new[] { false, true })
{
    var key = GroupKey.Generate(); var signingA = AutoLanIdentity.GenerateSigningKey(); var signingB = AutoLanIdentity.GenerateSigningKey();
    var idA = automatic ? AutoLanIdentity.GetDeviceId(signingA) : Guid.NewGuid(); var idB = automatic ? AutoLanIdentity.GetDeviceId(signingB) : Guid.NewGuid();
    var settingsA = new DynexSettings { DeviceId = idA, DeviceName = "PC A", GroupKeyBase64 = key, LoopbackOnly = true,
        Port = FreePort(), HeartbeatInterval = TimeSpan.FromMilliseconds(100), PeerTtl = TimeSpan.FromSeconds(1), AppVersion = "1.0.8" };
    var settingsB = settingsA with { DeviceId = idB, DeviceName = "PC B", Port = FreePort(), DirectPeers = [new(IPAddress.Loopback, settingsA.Port)] };
    await using IPeerService a = automatic ? new AutoLanPeerService(settingsA, signingA) : new PeerService(settingsA);
    await using IPeerService b = automatic ? new AutoLanPeerService(settingsB, signingB) : new PeerService(settingsB);
    a.Publish(false); b.Publish(true);
    await a.StartAsync(); await b.StartAsync();
    try { await Until(() => a.Snapshot().Count == 1 && b.Snapshot().Count == 1); } catch { Console.WriteLine(JsonSerializer.Serialize(new { automatic, settingsA.Port, BPort = settingsB.Port, A = a.GetNetworkDiagnostics(), B = b.GetNetworkDiagnostics() })); throw; }
    var result = await a.TestConnectionAsync(idB, TimeSpan.FromSeconds(2));
    Check(result.Succeeded && result.Stages.Count == 5, $"{automatic}: real authenticated two-peer request/response and reciprocal challenge, version confirmed");
    var missing = await a.TestConnectionAsync(Guid.NewGuid(), TimeSpan.FromSeconds(1));
    Check(!missing.Succeeded && missing.Stages.All(s => s.State == ConnectionTestState.Unknown), $"{automatic}: unknown/wrong selected identity cannot pass");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    var cancelResult = await a.TestConnectionAsync(idB, TimeSpan.FromSeconds(2), cancelled.Token);
    Check(!cancelResult.Succeeded && cancelResult.Stages.Any(s => s.State == ConnectionTestState.Cancelled), $"{automatic}: cancellation is bounded and explicit");
    await b.StopAsync();
    var clock = Stopwatch.StartNew();
    var timedOut = await a.TestConnectionAsync(idB, TimeSpan.FromMilliseconds(220));
    Check(!timedOut.Succeeded && timedOut.Stages[1].State == ConnectionTestState.Unknown && clock.Elapsed < TimeSpan.FromSeconds(2), $"{automatic}: departed peer timeout stays unknown");
    await Until(() => a.Snapshot().Count == 0);
    Check(a.Snapshot().Count == 0, $"{automatic}: departed peer expires offline");
    var offline = await a.TestConnectionAsync(idB, new IPEndPoint(IPAddress.Loopback, settingsB.Port), TimeSpan.FromMilliseconds(150));
    Check(offline.Stages[0].State == ConnectionTestState.Passed && offline.Stages[1].State == ConnectionTestState.Unknown,
        $"{automatic}: saved offline endpoint receives bounded active test without inventing success");
    await b.StartAsync(); await Until(() => a.Snapshot().Count == 1);
    a.SetExcludedDevices([idB]);
    Check(a.Snapshot().Count == 0 && a.GetNetworkDiagnostics().LearnedEndpoints == 0, $"{automatic}: exclusion immediately removes budget participant and active return route");
    await Task.Delay(350);
    Check(a.Snapshot().Count == 0, $"{automatic}: blocked fresh heartbeats cannot recreate the peer");
    var denied = await b.TestConnectionAsync(idA, TimeSpan.FromMilliseconds(220));
    Check(!denied.Succeeded && denied.Stages[1].State == ConnectionTestState.Unknown, $"{automatic}: blocked identity cannot receive probe response");
    a.SetExcludedDevices([]); await Until(() => a.Snapshot().Count == 1);
    Check(a.Snapshot().Single().DeviceId == idB, $"{automatic}: unblocking resumes authenticated status without resetting replay checks");
    Check(a.Snapshot().Single().RemotePort == settingsB.Port, $"{automatic}: only authenticated source UDP port becomes persistent test target");
    a.SetNetworkAccessEnabled(false);
    var sentBefore = a.GetNetworkDiagnostics().SentDatagrams;
    var acceptedBefore = a.GetNetworkDiagnostics().AcceptedStatusDatagrams;
    await Task.Delay(250);
    Check(a.Snapshot().Count == 0 && a.GetNetworkDiagnostics().LearnedEndpoints == 0 && a.GetNetworkDiagnostics().SentDatagrams == sentBefore &&
        a.GetNetworkDiagnostics().AcceptedStatusDatagrams == acceptedBefore && a.NetworkStatus?.Contains("gesperrt") == true,
        $"{automatic}: unreadable exclusion policy fails closed for receive, routes, budgets and all discovery sends");
    var closed = await a.TestConnectionAsync(idB, new IPEndPoint(IPAddress.Loopback, settingsB.Port), TimeSpan.FromMilliseconds(150));
    Check(!closed.Succeeded && closed.Stages.All(s => s.State == ConnectionTestState.Unknown), $"{automatic}: fail-closed policy prevents explicit offline probes too");
}

// Deterministic protocol checks isolate version/unknown/replay evidence from UDP delivery.
foreach (var remoteVersion in new string?[] { "1.0.7", null })
{
    var signingA = AutoLanIdentity.GenerateSigningKey(); var signingB = AutoLanIdentity.GenerateSigningKey();
    var sa = new DynexSettings { DeviceId = AutoLanIdentity.GetDeviceId(signingA), DeviceName = "A", AppVersion = "1.0.8" };
    var sb = sa with { DeviceId = AutoLanIdentity.GetDeviceId(signingB), AppVersion = remoteVersion };
    var a = new ConnectionProbe(sa, signingA, _ => false); var b = new ConnectionProbe(sb, signingB, _ => false);
    var endpointA = new IPEndPoint(IPAddress.Loopback, FreePort()); var endpointB = new IPEndPoint(IPAddress.Loopback, FreePort());
    var replies = 0; byte[]? capturedRequest = null; byte[]? capturedConfirm = null;
    Task Return(byte[] bytes, IPEndPoint _, CancellationToken token) { replies++; return a.ReceiveAsync(bytes, endpointB, (_, _, _) => Task.CompletedTask, token); }
    var result = await a.TestAsync(sb.DeviceId, endpointB, TimeSpan.FromSeconds(1), (bytes, _, token) =>
    {
        if (capturedRequest is null) capturedRequest = bytes; else capturedConfirm = bytes;
        return b.ReceiveAsync(bytes, endpointA, Return, token);
    }, CancellationToken.None);
    Check(!result.Succeeded && result.Stages[1].State == ConnectionTestState.Passed && result.Stages[2].State == ConnectionTestState.Passed &&
        result.Stages[4].State == (remoteVersion is null ? ConnectionTestState.Unknown : ConnectionTestState.Failed),
        $"version {remoteVersion ?? "missing"}: transport/auth may pass while version mismatch or absence prevents overall success");
    await b.ReceiveAsync(capturedRequest!, endpointA, Return, CancellationToken.None);
    await b.ReceiveAsync(capturedConfirm!, endpointA, Return, CancellationToken.None);
    Check(replies == 3, $"version {remoteVersion ?? "missing"}: completed request stays retired; duplicate confirmation only re-sends cached final acknowledgement");
}

// A signed wrong sender with the exact live nonce and source endpoint must still fail.
{
    var signingA = AutoLanIdentity.GenerateSigningKey(); var signingB = AutoLanIdentity.GenerateSigningKey();
    var sa = new DynexSettings { DeviceId = AutoLanIdentity.GetDeviceId(signingA), DeviceName = "A", AppVersion = "1.0.8" };
    var sb = sa with { DeviceId = AutoLanIdentity.GetDeviceId(signingB) };
    var a = new ConnectionProbe(sa, signingA, _ => false); var b = new ConnectionProbe(sb, signingB, _ => false);
    var endpointA = new IPEndPoint(IPAddress.Loopback, FreePort()); var endpointB = new IPEndPoint(IPAddress.Loopback, FreePort());
    var droppedKinds = new HashSet<int> { 1, 2, 3, 4 };
    static int Kind(byte[] packet) { var length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6, 2)); using var json = JsonDocument.Parse(packet.AsMemory(8, length)); return json.RootElement.GetProperty("Kind").GetInt32(); }
    Task SendBack(byte[] packet, IPEndPoint _, CancellationToken token) => droppedKinds.Remove(Kind(packet)) ? Task.CompletedTask : a.ReceiveAsync(packet, endpointB, (_, _, _) => Task.CompletedTask, token);
    var resilient = await a.TestAsync(sb.DeviceId, endpointB, TimeSpan.FromSeconds(2), (packet, _, token) =>
        droppedKinds.Remove(Kind(packet)) ? Task.CompletedTask : b.ReceiveAsync(packet, endpointA, SendBack, token), CancellationToken.None);
    Check(resilient.Succeeded && droppedKinds.Count == 0, "each of four UDP message kinds may be lost once; bounded idempotent resend completes without resetting challenge");
}

// A signed wrong sender with the exact live nonce and source endpoint must still fail.
{
    var signingA = AutoLanIdentity.GenerateSigningKey(); var signingB = AutoLanIdentity.GenerateSigningKey(); var signingC = AutoLanIdentity.GenerateSigningKey();
    var settings = new DynexSettings { DeviceId = AutoLanIdentity.GetDeviceId(signingA), DeviceName = "A", AppVersion = "1.0.8" };
    var engine = new ConnectionProbe(settings, signingA, _ => false);
    var endpoint = new IPEndPoint(IPAddress.Loopback, FreePort());
    var result = await engine.TestAsync(AutoLanIdentity.GetDeviceId(signingB), endpoint, TimeSpan.FromMilliseconds(150), async (packet, _, token) =>
    {
        var length = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6, 2));
        var node = JsonNode.Parse(packet.AsSpan(8, length))!.AsObject();
        node["Sender"] = AutoLanIdentity.GetDeviceId(signingC); node["Target"] = settings.DeviceId; node["Kind"] = 2; node["Challenge"] = Guid.NewGuid();
        using var signer = AutoLanIdentity.ImportPrivate(signingC);
        node["PublicKey"] = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
        var body = JsonSerializer.SerializeToUtf8Bytes(node);
        var response = new byte[body.Length + 72]; "DYNP"u8.CopyTo(response); response[4] = 1; response[5] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6), (ushort)body.Length); body.CopyTo(response, 8);
        signer.SignData(response.AsSpan(0, body.Length + 8), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation).CopyTo(response, body.Length + 8);
        await engine.ReceiveAsync(response, endpoint, (_, _, _) => Task.CompletedTask, token);
    }, CancellationToken.None);
    Check(!result.Succeeded && result.Stages[3].State == ConnectionTestState.Unknown, "signed wrong peer with matching endpoint and live challenge cannot authenticate selected peer");
}

var directory = Path.Combine(Path.GetTempPath(), "Dynex-device-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var id = Guid.NewGuid(); var lastSeen = DateTimeOffset.UtcNow;
    var registry = new DeviceRegistry(directory);
    registry.Observe([new(id, "Original", false, null, null, lastSeen, "127.0.0.1")]);
    registry.SetPreferences(id, "Spiele-PC", DeviceDisposition.Blocked);
    var reopened = new DeviceRegistry(directory);
    var saved = reopened.Snapshot().Single();
    Check(saved.Device.DisplayName == "Spiele-PC" && saved.Device.DeviceName == "Original" && saved.Device.Disposition == DeviceDisposition.Blocked && !saved.Online,
        "alias, blocked preference and offline known device persist independently of observed name");
    Check(reopened.ExcludedIds.Contains(id) && saved.Device.LastSeenUtc == lastSeen, "persisted exclusion and exact last authenticated sighting restored");
    reopened.Observe([new(id, "Spoofed update", true, 10, 20, lastSeen.AddMinutes(1))]);
    Check(reopened.Snapshot().Single().Device.DeviceName == "Original", "excluded identity cannot update last-seen or name");
    reopened.SetPreferences(id, null, DeviceDisposition.Ignored);
    Check(new DeviceRegistry(directory).ExcludedIds.Contains(id), "ignored preference is persistent and excluded as well");
    reopened.SetPreferences(id, "", DeviceDisposition.Normal);
    reopened.Observe([new(id, "Original", false, null, null, lastSeen.AddSeconds(1))]);
    reopened.Observe([]); reopened.Flush();
    Check(!reopened.Snapshot().Single().Online && reopened.Snapshot().Single().Device.Alias is null, "expired snapshot marks offline and empty alias restores authenticated name");
    var fullRegistry = Enumerable.Range(0, 512).Select(_ => new KnownDevice(Guid.NewGuid(), new string('界', 80), new string('語', 80), lastSeen,
        "192.168.1.2", "1.0.10", DeviceDisposition.Blocked, 39274)).ToArray();
    File.WriteAllText(Path.Combine(directory, "devices.json"), JsonSerializer.Serialize(fullRegistry, new JsonSerializerOptions { WriteIndented = true }));
    var full = new DeviceRegistry(directory);
    full.SetPreferences(fullRegistry[0].DeviceId, new string('語', 80), DeviceDisposition.Ignored);
    Check(new DeviceRegistry(directory).ExcludedIds.Count == 512, "maximum registry with Unicode names round-trips without rejecting its own valid size or losing exclusions");
    File.WriteAllText(Path.Combine(directory, "devices.json"), "broken");
    var rejected = false; try { _ = new DeviceRegistry(directory); } catch (JsonException) { rejected = true; }
    Check(rejected && File.ReadAllText(Path.Combine(directory, "devices.json")) == "broken", "corrupt registry cannot silently erase persisted blocks");
}
finally
{
    foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
    Directory.Delete(directory);
}
Console.WriteLine($"{checks} checks passed.");
