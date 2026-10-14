using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Buffers.Binary;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Dynex.Desktop.Games;
using Microsoft.Win32.SafeHandles;

if (args.Length == 2 && args[0] == "--ping-patterns") return await PingProbeComparison.RunAsync(args[1]);

if (args.Length != 3 || args[0] is not ("--live" or "--watchdog" or "--stop" or "--qos" or "--ping"))
{
    Console.Error.WriteLine("Explicit live test only: --live|--watchdog|--stop|--qos|--ping FULL_HELPER_PATH OUTPUT_DIRECTORY. Requires approved filter installation and captures real adapter traffic. --live downloads 22 MB; --qos downloads 4 MB and uploads 1 MB synthetic data; --ping compares ICMP replies with capture off/on and changing limits, without a test download.");
    return 2;
}
var helperPath = Path.GetFullPath(args[1]);
var directory = Path.GetFullPath(args[2]);
Directory.CreateDirectory(directory);
var samples = new List<object>();
var states = new List<string>();
var gate = new object();
string? latest = null;
double? requestedRate = null;
var protectTest = false;
var requestQos = false;
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(args[0] == "--ping" ? 240 : 100));
using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
var pipeName = "DYNEX-live-" + Guid.NewGuid().ToString("N");
using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
using var self = Process.GetCurrentProcess();
using var helper = Process.Start(new ProcessStartInfo(helperPath)
{
    UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
    Arguments = $"--pipe {pipeName} --parent {Environment.ProcessId} --parent-start {self.StartTime.ToUniversalTime().Ticks}"
}) ?? throw new Exception("Helper did not start.");
StreamWriter? writer = null;
Task? readTask = null, heartbeat = null;
var success = false;
try
{
    await pipe.WaitForConnectionAsync(lifetime.Token).WaitAsync(TimeSpan.FromSeconds(30));
    if (!Interop.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != helper.Id)
        throw new Exception("Unexpected helper process connected.");
    writer = new(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
    readTask = Task.Run(async () =>
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
        while (await reader.ReadLineAsync(lifetime.Token) is { } line)
            lock (gate) { latest = line; states.Add(line); }
    });
    heartbeat = Task.Run(async () =>
    {
        do
        {
            var games = GameDetector.ScanAll("").Where(g => g.ProcessId.HasValue)
                .Select(g => new Identity(g.ProcessId!.Value, g.ProcessStartedAt?.UtcTicks)).ToList();
            double? mbps;
            bool qosEnabled;
            lock (gate)
            {
                mbps = requestedRate;
                qosEnabled = requestQos;
                if (protectTest) games.Add(new(Environment.ProcessId, self.StartTime.ToUniversalTime().Ticks));
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { mbps, protectedProcesses = games, qosEnabled }).AsMemory(), heartbeatStop.Token);
            await Task.Delay(750, heartbeatStop.Token);
        } while (!heartbeatStop.IsCancellationRequested);
    });
    await WaitStatus(false, null);
    if (args[0] == "--ping")
    {
        foreach (var phase in new[] { "off-before", "qos-first", "off-middle", "qos-second", "adaptive-rates", "off-after" })
        {
            var enabled = !phase.StartsWith("off-");
            lock (gate) { requestQos = enabled; requestedRate = phase == "adaptive-rates" ? 8 : null; }
            await WaitStatus(phase == "adaptive-rates", phase == "adaptive-rates" ? 8 : null, enabled);
            var observations = new List<PingObservation>();
            for (var i = 0; i < 30; i++)
            {
                if (phase == "adaptive-rates" && i % 3 == 0)
                    lock (gate) requestedRate = 8 + i % 6;
                var cycle = Stopwatch.StartNew();
                observations.AddRange(await Task.WhenAll(ProbePing("192.168.1.1", i), ProbePing("1.1.1.1", i)));
                var remaining = TimeSpan.FromMilliseconds(500) - cycle.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, lifetime.Token);
            }
            var summary = observations.GroupBy(p => p.Target).Select(g => new
            {
                Target = g.Key, Sent = g.Count(), Success = g.Count(p => p.Status == "Success"),
                AverageMs = g.Where(p => p.Rtt.HasValue).Select(p => p.Rtt).Average(),
                Failures = g.Where(p => p.Status != "Success").ToArray()
            }).ToArray();
            samples.Add(new { Phase = phase, Summary = summary, Observations = observations });
            Console.WriteLine(JsonSerializer.Serialize(new { Phase = phase, Summary = summary }));
        }
        Console.WriteLine("COMPLETE: raw ICMP observations recorded; this is a comparison, not an assertion of zero packet loss.");
    }
    else if (args[0] == "--qos")
    {
        lock (gate) { requestQos = true; protectTest = true; }
        await WaitStatus(false, null, qos: true);
        var gameBefore = LatestCounter("gamePriorityPackets");
        await Download("qos-only-protected-process", 4_000_000);
        await WaitFor(() => LatestCounter("gamePriorityPackets") > gameBefore, TimeSpan.FromSeconds(5));
        var gameAfter = LatestCounter("gamePriorityPackets");
        lock (gate) protectTest = false;
        int responseCount; lock (gate) responseCount = states.Count;
        await WaitFor(() => { lock (gate) return states.Count >= responseCount + 2; }, TimeSpan.FromSeconds(5));
        await UploadSyntheticBytes();
        var voiceBefore = LatestCounter("voicePriorityPackets");
        await SendSyntheticAudio();
        await WaitFor(() => LatestCounter("voicePriorityPackets") > voiceBefore, TimeSpan.FromSeconds(5));
        var voiceAfter = LatestCounter("voicePriorityPackets");
        heartbeatStop.Cancel();
        try { await heartbeat; } catch (OperationCanceledException) { }
        await writer.WriteLineAsync("{\"mbps\":null,\"qosEnabled\":false,\"shutdown\":true}");
        await helper.WaitForExitAsync(lifetime.Token).WaitAsync(TimeSpan.FromSeconds(12));
        await readTask.WaitAsync(TimeSpan.FromSeconds(2));
        VerifyFinalRelease();
        samples.Add(new { Mode = "qos-only", GamePriorityPackets = gameAfter - gameBefore,
            VoicePriorityPackets = voiceAfter - voiceBefore, NativeExitCode = helper.ExitCode });
        Console.WriteLine($"PASS: QoS without download cap; game counter +{gameAfter - gameBefore}, voice counter +{voiceAfter - voiceBefore} during probes (may include other running apps); release acknowledged.");
    }
    else if (args[0] is "--watchdog" or "--stop")
    {
        lock (gate) { requestedRate = 8; requestQos = true; }
        await WaitStatus(true, 8, qos: true);
        heartbeatStop.Cancel();
        try { await heartbeat; } catch (OperationCanceledException) { }
        if (args[0] == "--stop") await writer.WriteLineAsync("{\"mbps\":null,\"shutdown\":true}");
        var watchdogClock = Stopwatch.StartNew();
        await helper.WaitForExitAsync(lifetime.Token).WaitAsync(TimeSpan.FromSeconds(12));
        await readTask.WaitAsync(TimeSpan.FromSeconds(2));
        VerifyFinalRelease();
        samples.Add(new { Mode = args[0], HelperExitSeconds = watchdogClock.Elapsed.TotalSeconds });
        await Download(args[0] == "--stop" ? "stop-released" : "watchdog-released", 4_000_000);
        Console.WriteLine("PASS: native helper stopped, release acknowledged and Internet download still works.");
    }
    else
    {
    var baseline = await Download("baseline", 4_000_000);
    lock (gate) { requestedRate = 8; requestQos = true; }
    await WaitStatus(true, 8, qos: true);
    var limited = await Download("limited-8mbps", 10_000_000);
    lock (gate) protectTest = true;
    // Wait for two new heartbeat responses, ensuring the new protected PID was sent.
    int count; lock (gate) count = states.Count;
    await WaitFor(() => { lock (gate) return states.Count >= count + 2; }, TimeSpan.FromSeconds(5));
    var bypass = await Download("protected-process", 4_000_000);
    lock (gate) { requestedRate = null; protectTest = false; requestQos = false; }
    await WaitStatus(false, null);
    var released = await Download("released", 4_000_000);
    if (limited.SteadyMbps > 10 || limited.SteadyMbps < .2) throw new Exception($"Limited steady throughput outside useful 0.2–10 Mbit/s range: {limited.SteadyMbps:F2}");
    if (baseline.Mbps > 16 && bypass.Mbps < limited.SteadyMbps * 1.3) throw new Exception("Protected process did not bypass the measured limit.");
    if (baseline.Mbps > 16 && released.Mbps < limited.SteadyMbps * 1.3) throw new Exception("Connection did not recover after removing limit.");
    lock (gate)
        if (!states.Any(line => { using var status = JsonDocument.Parse(line); return status.RootElement.GetProperty("sentBytes").GetInt64() > 0; }))
            throw new Exception("No native shaped packet counters were observed.");
    Console.WriteLine("PASS: real filter opened, download constrained, protected process bypassed, limit removed.");
    }
    success = true;
}
catch (Exception error)
{
    Console.WriteLine("FAIL: " + error.Message);
    lock (gate) Console.WriteLine("Last helper status: " + latest);
    samples.Add(new { Error = error.ToString() });
}
finally
{
    lifetime.Cancel();
    if (heartbeat is not null) try { await heartbeat; } catch (OperationCanceledException) { } catch (IOException) { }
    if (writer is not null)
    {
        try { await writer.WriteLineAsync("{\"mbps\":null,\"protectedProcesses\":[]}"); } catch (IOException) { }
    }
    pipe.Dispose();
    if (readTask is not null) try { await readTask; } catch (OperationCanceledException) { } catch (IOException) { } catch (ObjectDisposedException) { }
    try { await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    catch (TimeoutException) { success = false; samples.Add(new { Error = "Helper did not exit after IPC close. Manual adapter cleanup required." }); }
    lock (gate) File.WriteAllText(Path.Combine(directory, "helper-status.jsonl"), string.Join(Environment.NewLine, states));
    File.WriteAllText(Path.Combine(directory, "measurements.json"), JsonSerializer.Serialize(new { success, samples }, new JsonSerializerOptions { WriteIndented = true }));
}
return success ? 0 : 1;

async Task<PingObservation> ProbePing(string target, int index)
{
    using var ping = new Ping();
    var clock = Stopwatch.StartNew();
    var at = DateTimeOffset.UtcNow;
    try
    {
        var reply = await ping.SendPingAsync(IPAddress.Parse(target), TimeSpan.FromMilliseconds(1000), cancellationToken: lifetime.Token);
        return new(index, at, target, reply.Status.ToString(), reply.Status == IPStatus.Success ? reply.RoundtripTime : null, clock.Elapsed.TotalMilliseconds);
    }
    catch (PingException error) { return new(index, at, target, error.GetType().Name + ": " + error.Message, null, clock.Elapsed.TotalMilliseconds); }
}

async Task WaitStatus(bool active, double? rate, bool qos = false)
{
    Console.WriteLine($"Waiting: active={active}, rate={rate}");
    await WaitFor(() =>
    {
        string? state; lock (gate) state = latest;
        if (state is null) return false;
        using var doc = JsonDocument.Parse(state);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            var message = error.GetString() ?? "";
            if (!message.StartsWith("Programmzuordnung")) throw new Exception(message);
        }
        var actualRate = root.GetProperty("mbps").ValueKind == JsonValueKind.Null ? (double?)null : root.GetProperty("mbps").GetDouble();
        var actualQos = root.TryGetProperty("qosActive", out var qosValue) && qosValue.GetBoolean();
        return root.GetProperty("active").GetBoolean() == active && actualRate == rate && actualQos == qos;
    }, TimeSpan.FromSeconds(12));
}
long LatestCounter(string name)
{
    lock (gate)
    {
        using var status = JsonDocument.Parse(latest ?? "{}");
        return status.RootElement.TryGetProperty(name, out var count) ? count.GetInt64() : 0;
    }
}
void VerifyFinalRelease()
{
    if (helper.ExitCode != 0) throw new Exception("Native helper exit was not clean.");
    lock (gate)
    {
        using var final = JsonDocument.Parse(latest ?? "{}");
        var root = final.RootElement;
        if (!root.TryGetProperty("stopped", out var stopped) || !stopped.GetBoolean() ||
            root.GetProperty("releaseError").ValueKind != JsonValueKind.Null ||
            root.TryGetProperty("qosActive", out var qos) && qos.GetBoolean())
            throw new Exception("No successful final native release acknowledgement.");
    }
}
async Task SendSyntheticAudio()
{
    var gateway = NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
        .SelectMany(n => n.GetIPProperties().GatewayAddresses)
        .Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
        ?? throw new InvalidOperationException("No local IPv4 gateway for the bounded synthetic voice probe.");
    using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    udp.Bind(new IPEndPoint(IPAddress.Any, 0));
    // A local, unused high port; payload contains only synthetic G.711 silence.
    // No audio is recorded and no third-party voice service is contacted.
    var destination = new IPEndPoint(gateway, 49197);
    var packet = new byte[172]; packet[0] = 0x80; packet[1] = 0;
    packet.AsSpan(12).Fill(0xff);
    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 0x44594e58);
    for (ushort i = 0; i < 80; i++)
    {
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), i);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), (uint)i * 160);
        await udp.SendToAsync(packet, SocketFlags.None, destination, lifetime.Token);
        await Task.Delay(20, lifetime.Token);
    }
    samples.Add(new { SyntheticVoicePackets = 80, PayloadBytes = packet.Length, Destination = "local-gateway:49197" });
}
async Task UploadSyntheticBytes()
{
    using var handler = new SocketsHttpHandler { UseProxy = false };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    using var content = new ByteArrayContent(new byte[1_000_000]);
    var watch = Stopwatch.StartNew();
    using var response = await client.PostAsync("https://speed.cloudflare.com/__up", content, lifetime.Token);
    response.EnsureSuccessStatusCode();
    samples.Add(new { Name = "qos-only-unprotected-upload", Bytes = 1_000_000, Seconds = watch.Elapsed.TotalSeconds,
        Status = (int)response.StatusCode });
}
async Task WaitFor(Func<bool> condition, TimeSpan timeout)
{
    var clock = Stopwatch.StartNew();
    while (!condition())
    {
        if (clock.Elapsed > timeout || helper.HasExited) throw new TimeoutException("Helper status not reached.");
        await Task.Delay(50, lifetime.Token);
    }
}
async Task<Measurement> Download(string name, int bytes)
{
    Console.WriteLine("Download: " + name);
    using var handler = new SocketsHttpHandler { UseProxy = false, AutomaticDecompression = DecompressionMethods.None };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(35), DefaultRequestVersion = HttpVersion.Version11 };
    using var request = new HttpRequestMessage(HttpMethod.Get, $"https://speed.cloudflare.com/__down?bytes={bytes}");
    request.Headers.CacheControl = new() { NoCache = true };
    var watch = Stopwatch.StartNew();
    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
    response.EnsureSuccessStatusCode();
    await using var stream = await response.Content.ReadAsStreamAsync(lifetime.Token);
    var buffer = new byte[65536];
    long received = 0;
    long tailStartBytes = 0;
    double tailStartSeconds = 0;
    while (true)
    {
        var read = await stream.ReadAsync(buffer, lifetime.Token);
        if (read == 0) break;
        received += read;
        if (tailStartBytes == 0 && received >= bytes / 2) { tailStartBytes = received; tailStartSeconds = watch.Elapsed.TotalSeconds; }
        if (received > bytes) throw new InvalidDataException("Unexpected test response size.");
    }
    watch.Stop();
    if (received != bytes) throw new InvalidDataException($"Expected {bytes}, got {received} bytes.");
    var measurement = new Measurement(name, received, watch.Elapsed.TotalSeconds, received * 8 / watch.Elapsed.TotalSeconds / 1_000_000,
        (received - tailStartBytes) * 8 / (watch.Elapsed.TotalSeconds - tailStartSeconds) / 1_000_000);
    samples.Add(measurement);
    Console.WriteLine($"{name}: {measurement.Mbps:F2} Mbit/s overall, {measurement.SteadyMbps:F2} Mbit/s tail, {measurement.Seconds:F2}s");
    return measurement;
}
record Identity(int processId, long? startedAtUtcTicks);
record PingObservation(int Index, DateTimeOffset At, string Target, string Status, long? Rtt, double ElapsedMs);
record Measurement(string Name, long Bytes, double Seconds, double Mbps, double SteadyMbps);
static class Interop
{
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle handle, out uint id);
}
