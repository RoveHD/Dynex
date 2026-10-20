using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using Dynex.Shaper;

internal static class MediaEvidenceChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var started = DateTimeOffset.UtcNow.AddDays(-1);
        var client = new MediaEvidenceProcess(801, started.UtcTicks, @"C:\Apps\ELFIX\ELFIX.exe");
        var child = client with { ProcessId = 802 };
        MediaEvidenceProcess? Resolve(int pid) => pid == 801 ? client : pid == 802 ? child : null;
        var flow = QosChecks.Flow(false) with { Protocol = TransportProtocol.Tcp, RemoteAddress = IPAddress.Parse("203.0.113.5"), RemotePort = 443 };
        var owner = new TrafficOwner(child.ProcessId, started, "ELFIX");
        var valid = JsonSerializer.Serialize(new { version = 1, processIds = new[] { 801, 802 }, connections = new[] { new { address = "203.0.113.5", port = 443 } } });
        var bytes = Encoding.UTF8.GetBytes(valid);
        var snapshot = MediaEvidenceProtocol.Parse(bytes, client, Resolve, 100);
        foreach (var port in new[] { 80, 8443, 65535 })
        {
            var otherPort = MediaEvidenceProtocol.Parse(Encoding.UTF8.GetBytes(valid.Replace("443", port.ToString())), client, Resolve, 100);
            check(otherPort.Matches(owner, flow with { RemotePort = (ushort)port }, 100) && !otherPort.Matches(owner, flow, 100),
                $"actual media port {port} is accepted without authorizing another port");
        }
        check(snapshot.Matches(owner, flow, 100) && snapshot.Matches(owner, flow with { Protocol = TransportProtocol.Udp }, 100),
            "media evidence binds TCP/UDP to authenticated ELFIX PID, creation time and exact remote endpoint");
        check(!snapshot.Matches(owner, flow with { RemotePort = 444 }, 100) &&
              !snapshot.Matches(owner, flow with { RemoteAddress = IPAddress.Parse("203.0.113.6") }, 100) &&
              !snapshot.Matches(owner with { ProcessId = 803 }, flow, 100) &&
              !snapshot.Matches(owner with { ProcessName = "chrome" }, flow, 100) &&
              !snapshot.Matches(owner with { ProcessStartedAtUtc = started.AddTicks(1) }, flow, 100) &&
              !snapshot.Matches(owner with { ProcessStartedAtUtc = null }, flow, 100),
            "media evidence rejects unrelated IP/port, other PID/browser, reused PID and missing OS start time");
        check(snapshot.Matches(owner, flow, 114.999) && !snapshot.Matches(owner, flow, 115) && !snapshot.Matches(owner, flow, 99),
            "media evidence expires at fifteen seconds and rejects reversed time");
        var clear = MediaEvidenceProtocol.Parse(Encoding.UTF8.GetBytes("{\"version\":1,\"processIds\":[],\"connections\":[]}"), client, Resolve, 101);
        check(!clear.Matches(owner, flow, 101), "empty media connections immediately revoke old endpoint evidence");
        check(snapshot.Matches(owner, flow with { RemoteAddress = IPAddress.Parse("::ffff:203.0.113.5") }, 100),
            "media evidence normalizes IPv4-mapped addresses");
        var ipv6 = MediaEvidenceProtocol.Parse(Encoding.UTF8.GetBytes(valid.Replace("203.0.113.5", "2001:db8::5")), client, Resolve, 100);
        check(ipv6.Matches(owner, flow with { RemoteAddress = IPAddress.Parse("2001:0db8:0:0::5") }, 100),
            "media evidence matches equivalent IPv6 literal forms");
        var mapped = MediaEvidenceProtocol.Parse(Encoding.UTF8.GetBytes(valid.Replace("203.0.113.5", "::ffff:203.0.113.5")), client, Resolve, 100);
        check(mapped.Matches(owner, flow, 100), "mapped evidence matches native IPv4 packets");

        void Reject(string json, string reason, Func<int, MediaEvidenceProcess?>? resolver = null)
        {
            try { MediaEvidenceProtocol.Parse(Encoding.UTF8.GetBytes(json), client, resolver ?? Resolve, 100); }
            catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException or ArgumentException)
            { check(true, reason); return; }
            throw new Exception("Accepted unsafe evidence: " + reason);
        }
        foreach (var json in new[] { "{}", valid.Replace("\"version\":1", "\"version\":2"), valid.Replace("\"version\":1", "\"version\":\"1\""),
                     valid.Replace("\"version\":1", "\"version\":1,\"version\":1"), valid.Replace("\"version\":1", "\"version\":1,\"url\":\"secret\""),
                     valid.Replace("[801,802]", "[801,801]"), valid.Replace("[801,802]", "[0]"), valid.Replace("[801,802]", "[-1]"),
                     valid.Replace("[801,802]", "[2147483648]"), valid.Replace("[801,802]", "[801.5]"), valid.Replace("[801,802]", "[]"),
                     valid.Replace("[801,802]", "null"), valid.Replace("[801,802]", "[" + string.Join(',', Enumerable.Range(1, 33)) + "]"),
                     valid.Replace("443", "0"), valid.Replace("443", "-1"), valid.Replace("443", "65536"), valid.Replace("443", "\"443\""),
                     valid.Replace("[{\"address\":\"203.0.113.5\",\"port\":443}]", "[" + string.Join(',', Enumerable.Repeat("{\"address\":\"203.0.113.5\",\"port\":443}", 65)) + "]") })
            Reject(json, "media protocol rejects invalid version/schema/count/PID/port: " + json[..Math.Min(85, json.Length)]);
        foreach (var address in new[] { "example.com", "https://203.0.113.5", "[2001:db8::5]", "fe80::1%12", "127.1", "0x7f.0.0.1", " 203.0.113.5", "203.0.113.5 " })
            Reject(valid.Replace("203.0.113.5", address), "nonliteral/ambiguous media address rejected: " + address);
        Reject(valid, "unresolved ELFIX child rejected", pid => pid == 801 ? client : null);
        Reject(valid, "same named executable in another installation rejected", pid => pid == 801 ? client : child with { ImagePath = @"C:\Other\ELFIX.exe" });
        Reject(valid, "non-ELFIX sender rejected", pid => pid == 801 ? client with { ImagePath = @"C:\Apps\browser.exe" } : child);
        Reject(valid, "reused sender PID invalidates whole snapshot", pid => pid == 801 ? client with { StartedAtUtcTicks = started.AddSeconds(1).UtcTicks } : child);
        Reject(valid, "exited sender invalidates whole snapshot", pid => pid == 801 ? null : child);
        var badUtf8 = bytes.Concat(new byte[] { 0xc0, 0xaf }).ToArray();
        try { MediaEvidenceProtocol.Parse(badUtf8, client, Resolve, 100); throw new Exception("Malformed UTF8 accepted"); }
        catch (DecoderFallbackException) { check(true, "invalid UTF-8 fails closed"); }
        Reject(valid + new string(' ', MediaEvidenceProtocol.MaximumFrameBytes), "oversized media JSON rejected before parsing");

        var rate = new MediaEvidenceRateLimit();
        check(rate.Accept(0) && rate.Accept(.1) && rate.Accept(.2) && rate.Accept(.3) && !rate.Accept(.999) && rate.Accept(1),
            "media protocol accepts at most four frames in a sliding second");
        check(MediaEvidenceProtocol.PipeName(@"C:\Users\Alice\AppData\Local\") == MediaEvidenceProtocol.PipeName("c:/users/alice/appdata/local") &&
              MediaEvidenceProtocol.PipeName(@"C:\Users\Alice\AppData\Local") != MediaEvidenceProtocol.PipeName(@"C:\Users\Bob\AppData\Local"),
            "media pipe name normalizes separators/case/trailing slashes and isolates local profiles");

        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(valid + "\n" + valid + "\n")))
        {
            var reader = new MediaEvidenceFrameReader(stream);
            check((await reader.ReadAsync(default))!.SequenceEqual(bytes) && (await reader.ReadAsync(default))!.SequenceEqual(bytes) && await reader.ReadAsync(default) is null,
                "media byte framing preserves coalesced frames and clean EOF");
        }
        using (var stream = new OneByteStream(Encoding.UTF8.GetBytes(valid + "\n")))
            check((await new MediaEvidenceFrameReader(stream).ReadAsync(default))!.SequenceEqual(bytes), "fragmented byte stream reassembles exactly one media frame");
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(valid)))
        {
            try { await new MediaEvidenceFrameReader(stream).ReadAsync(default); throw new Exception("Partial frame accepted"); }
            catch (EndOfStreamException) { check(true, "media EOF without newline rejects incomplete snapshot"); }
        }
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', MediaEvidenceProtocol.MaximumFrameBytes + 1) + "\n")))
        {
            try { await new MediaEvidenceFrameReader(stream).ReadAsync(default); throw new Exception("Large frame accepted"); }
            catch (FormatException) { check(true, "media framing bounds bytes before JSON decoding"); }
        }
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(valid.PadRight(MediaEvidenceProtocol.MaximumFrameBytes) + "\n")))
            check((await new MediaEvidenceFrameReader(stream).ReadAsync(default))!.Length == MediaEvidenceProtocol.MaximumFrameBytes, "media frame accepts exactly sixteen KiB");

        foreach (var v6 in new[] { false, true })
        foreach (var protocol in new[] { TransportProtocol.Tcp, TransportProtocol.Udp })
        foreach (var direction in new[] { PacketDirection.Inbound, PacketDirection.Outbound })
        {
            var current = QosChecks.Flow(v6) with { Protocol = protocol, RemotePort = 443 };
            var json = valid.Replace("203.0.113.5", current.RemoteAddress.ToString());
            var evidence = MediaEvidenceProtocol.Parse(Encoding.UTF8.GetBytes(json), client, Resolve, 100);
            var now = 100d;
            var name = "ELFIX";
            var processStarted = started;
            OwnershipSnapshot Ownership() => OwnershipSnapshot.Create([new(current, 802, processStarted, name)]);
            var policy = new RealtimeQosPolicy(Ownership, () => now) { MediaEvidence = () => evidence };
            using var packet = QosChecks.Packet(current, direction, new byte[160]);
            check(policy.Classify(packet) == PacketPriority.Video, $"application media evidence resolves encrypted IPv{(v6 ? 6 : 4)} {protocol} {direction}");
            if (direction == PacketDirection.Outbound)
            {
                check(QosPacketMarker.TryMark(packet, policy.Classify(packet)), "application evidence marks outbound AF41");
                var ip = packet.Buffer.AsSpan(packet.IpOffset);
                var ds = v6 ? ((ip[0] & 15) << 4) | (ip[1] >> 4) : ip[1];
                check(ds >> 2 == 34, "application media DSCP is AF41");
            }
            policy.Protect([new(802, started.UtcTicks)]);
            check(policy.Classify(packet) == PacketPriority.Game, "protected game takes priority over application media evidence");
            policy.Protect([]);
            evidence = MediaEvidenceSnapshot.Empty;
            check(policy.Classify(packet) == PacketPriority.None, "cleared evidence immediately stops encrypted video marking");
            evidence = MediaEvidenceProtocol.Parse(Encoding.UTF8.GetBytes(json), client, Resolve, 100);
            processStarted = started.AddSeconds(1);
            check(policy.Classify(packet) == PacketPriority.None, "reused owner PID cannot inherit encrypted media priority");
            processStarted = started; name = "chrome";
            check(policy.Classify(packet) == PacketPriority.None, "browser at the same endpoint cannot borrow ELFIX priority");
            name = "ELFIX"; now = 115;
            check(policy.Classify(packet) == PacketPriority.None, "expired application evidence is not retained by SNI cache");
        }
        {
            var udp = flow with { Protocol = TransportProtocol.Udp };
            var now = 100d;
            var policy = new RealtimeQosPolicy(() => OwnershipSnapshot.Create([new(udp, 802, started, "ELFIX")]), () => now)
            { MediaEvidence = () => snapshot };
            var priority = PacketPriority.None;
            for (ushort i = 0; i < 4; i++)
            {
                var payload = new byte[172]; payload[0] = 128; payload[1] = 0;
                BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), i);
                BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)i * 160);
                BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8), 99);
                using var packet = QosChecks.Packet(udp, PacketDirection.Outbound, payload);
                priority = policy.Classify(packet); now += .02;
            }
            check(priority == PacketPriority.Voice, "validated audio RTP retains EF priority over ELFIX media evidence");
        }

        await PipeChecks(check, started);
        // Keep the normal .NET suite independent of a Node installation. Opt in
        // for the real cross-runtime pipe/hash check when testing the ELFIX bridge.
        if (Environment.GetEnvironmentVariable("DYNEX_TEST_NODE_MEDIA") == "1") await NodePipeCheck(check, started);
        else Console.WriteLine("SKIP: optional Node media bridge integration (DYNEX_TEST_NODE_MEDIA=1)");
    }

    private static async Task PipeChecks(Action<bool, string> check, DateTimeOffset started)
    {
        var clientIdentity = new MediaEvidenceProcess(Environment.ProcessId, started.UtcTicks, @"C:\Tests\ELFIX.exe");
        var flow = QosChecks.Flow(false) with { RemoteAddress = IPAddress.Parse("203.0.113.5"), RemotePort = 443 };
        var owner = new TrafficOwner(Environment.ProcessId, started, "ELFIX");
        var message = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = 1, processIds = new[] { Environment.ProcessId }, connections = new[] { new { address = "203.0.113.5", port = 443 } } }) + "\n");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pipeName = "dynex-media-test-" + Guid.NewGuid().ToString("N");
        var allow = true;
        var receiver = new MediaEvidenceReceiver(pipeName, pid => allow && pid == Environment.ProcessId ? clientIdentity : null);
        var worker = receiver.RunAsync(stop.Token);
        async Task<NamedPipeClientStream> Connect()
        {
            if (worker.IsCompleted) throw new Exception("Media listener stopped", receiver.Failure);
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(stop.Token);
            return pipe;
        }
        bool Matches() => receiver.Snapshot.Matches(owner, flow, MediaEvidenceProtocol.Now);
        try
        {
            using (var client = await Connect())
            {
                await client.WriteAsync(message, stop.Token);
                await Until(Matches, stop.Token);
                check(Matches(), "isolated native pipe authenticates actual client PID and publishes evidence");
                await client.WriteAsync(Encoding.UTF8.GetBytes("{\"version\":1,\"processIds\":[],\"connections\":[]}\n"), stop.Token);
                await Until(() => !Matches(), stop.Token);
                check(!Matches(), "live pipe empty snapshot revokes evidence");
                await client.WriteAsync(message, stop.Token);
                await Until(Matches, stop.Token);
            }
            await Until(() => !Matches(), stop.Token);
            check(!Matches(), "native pipe disconnect immediately revokes evidence");
            using (var client = await Connect())
            {
                await client.WriteAsync(message, stop.Token);
                await Until(Matches, stop.Token);
                await client.WriteAsync(Encoding.UTF8.GetBytes("{bad-json}\n"), stop.Token);
                await Until(() => !Matches(), stop.Token);
                check(!Matches(), "invalid pipe frame disconnects and revokes authenticated evidence");
            }
            using (var client = await Connect())
            {
                await client.WriteAsync(message, stop.Token);
                await Until(Matches, stop.Token);
                await client.WriteAsync(Enumerable.Repeat(message, 4).SelectMany(b => b).ToArray(), stop.Token);
                await Until(() => !Matches(), stop.Token);
                check(!Matches(), "fifth media frame within one second disconnects and revokes evidence");
            }
            using (var client = await Connect())
            {
                await client.WriteAsync(message, stop.Token);
                await Until(Matches, stop.Token);
                allow = false;
                await client.WriteAsync(message, stop.Token);
                await Until(() => !Matches(), stop.Token);
                check(!Matches(), "OS process revalidation failure on heartbeat revokes evidence");
            }
            stop.Cancel();
            await worker.WaitAsync(TimeSpan.FromSeconds(2));
            check(!Matches(), "media worker cancellation releases isolated pipe and clears evidence");
        }
        finally { stop.Cancel(); await worker.WaitAsync(TimeSpan.FromSeconds(2)); }

        var rejected = new MediaEvidenceReceiver("dynex-media-test-" + Guid.NewGuid().ToString("N"), _ => null);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await rejected.RunAsync(cancelled.Token);
        check(true, "media server handles pre-cancelled lifecycle without creating a pipe");
        using var occupied = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1);
        await new MediaEvidenceReceiver(pipeName).RunAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
        check(true, "occupied optional endpoint fails closed without crashing helper");
    }

    private static async Task NodePipeCheck(Action<bool, string> check, DateTimeOffset started)
    {
        var pipeName = "dynex-media-node-test-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int nodeId = 0;
        var receiver = new MediaEvidenceReceiver(pipeName, pid => pid == Volatile.Read(ref nodeId)
            ? new(pid, started.UtcTicks, @"C:\Tests\ELFIX.exe") : null);
        var worker = receiver.RunAsync(stop.Token);
        var script = "const net=require('net'); const s=net.createConnection('\\\\\\\\.\\\\pipe\\\\'+process.argv[1]); " +
            "s.on('connect',()=>{s.write(JSON.stringify({version:1,processIds:[process.pid],connections:[{address:'203.0.113.5',port:443}]})+'\\n'); console.log('sent');}); " +
            "s.on('error',e=>{console.error(e.code);process.exit(1)}); process.stdin.on('data',()=>s.end()); s.on('close',()=>process.exit(0));";
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add(script); start.ArgumentList.Add(pipeName);
        using var node = Process.Start(start) ?? throw new Exception("Node could not start");
        Volatile.Write(ref nodeId, node.Id);
        var flow = QosChecks.Flow(false) with { RemoteAddress = IPAddress.Parse("203.0.113.5"), RemotePort = 443 };
        var owner = new TrafficOwner(node.Id, started, "ELFIX");
        try
        {
            var output = await node.StandardOutput.ReadLineAsync(stop.Token);
            if (output != "sent") throw new Exception("Node pipe client failed: " + await node.StandardError.ReadToEndAsync(stop.Token));
            await Until(() => receiver.Snapshot.Matches(owner, flow, MediaEvidenceProtocol.Now), stop.Token);
            check(true, "real Node net client connects to isolated CurrentUserOnly duplex pipe and sends evidence");
            var expectedName = MediaEvidenceProtocol.PipeName(@"C:\Users\Alice\AppData\Local\");
            // The same hashing contract is implemented independently by the sender's runtime.
            using var hashNode = Process.Start(new ProcessStartInfo("node")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                ArgumentList = { "-e", "console.log('dynex-media-'+require('crypto').createHash('sha256').update(process.argv[1].replaceAll('/','\\\\').replace(/\\\\+$/,'').toLowerCase(),'utf8').digest('hex').slice(0,24))", @"C:\Users\Alice\AppData\Local\" }
            })!;
            check((await hashNode.StandardOutput.ReadLineAsync(stop.Token)) == expectedName, "independent Node SHA256 pipe name agrees with .NET normalization contract");
            await hashNode.WaitForExitAsync(stop.Token);
            await node.StandardInput.WriteLineAsync("stop");
            await node.WaitForExitAsync(stop.Token);
            await Until(() => !receiver.Snapshot.Matches(owner, flow, MediaEvidenceProtocol.Now), stop.Token);
            check(node.ExitCode == 0, "real Node disconnect revokes media evidence without residual listener");
        }
        finally
        {
            if (!node.HasExited) { node.Kill(); await node.WaitForExitAsync(); }
            stop.Cancel(); await worker.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task Until(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(5, token);
    }

    private sealed class OneByteStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token);
    }
}
