using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dynex.Desktop;
using Dynex.Shaper;

internal static class HelperRecoveryChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        await CommandAcknowledgement(check);
        foreach (var released in new[] { true, false }) await StopBeforeAcknowledgement(check, released);
        await BufferedTerminalFrame(check);
        await WatchdogRelease(check);
        await InvalidCommandRelease(check);
        await RearmAfterIdle(check);
    }

    private static async Task CommandAcknowledgement(Action<bool, string> check)
    {
        using var fixture = await ControllerPipe.CreateAsync();
        var applying = fixture.Controller.ApplyAsync(8, fixture.Timeout.Token, qosEnabled: true);
        using var command = JsonDocument.Parse((await fixture.Input.ReadLineAsync(fixture.Timeout.Token))!);
        var id = command.RootElement.GetProperty("commandId").GetInt64();
        check(!applying.IsCompleted && !fixture.Controller.Status.Active,
            "writing a command alone does not confirm activation or complete Apply");
        await fixture.Output.WriteLineAsync(JsonSerializer.Serialize(new { active = false, commandId = id - 1, error = "stale ACK" }));
        await Until(() => fixture.Controller.Status.Message == "stale ACK");
        check(!applying.IsCompleted, "an old command ACK cannot acknowledge the current apply");
        await fixture.Output.WriteLineAsync(JsonSerializer.Serialize(new { active = true, mbps = 8, qosActive = true, commandId = id }));
        await applying.WaitAsync(fixture.Timeout.Token);
        check(fixture.Controller.Status is { Active: true, Mbps: 8, QosActive: true },
            "matching authenticated status confirms the requested activation");
        await fixture.ShutdownAsync();
    }

    private static async Task StopBeforeAcknowledgement(Action<bool, string> check, bool released)
    {
        using var fixture = await ControllerPipe.CreateAsync();
        var applying = fixture.Controller.ApplyAsync(8, fixture.Timeout.Token, qosEnabled: true);
        await fixture.Input.ReadLineAsync(fixture.Timeout.Token);
        const string cause = "Netzwerk geändert; Paketverarbeitung für die Aktualisierung der LAN-Ausnahmen beendet.";
        await fixture.Output.WriteLineAsync(JsonSerializer.Serialize(new { active = false, mbps = (double?)null, stopped = true,
            releaseError = released ? null : "injected adapter reset failure", error = cause, stopReason = "network-change" }));
        fixture.Client.Dispose();
        await applying.WaitAsync(fixture.Timeout.Token);
        check(!fixture.Controller.Status.Active && fixture.Controller.CaptureReleased == released &&
            fixture.Controller.Status.Message.Contains(released ? "Netzwerk geändert" : "adapter reset failure"),
            "stop before ACK never reports success or replaces the real cause with Broken pipe: " + released);
        await fixture.Controller.ApplyAsync(null, fixture.Timeout.Token);
        check(fixture.Controller.StartAttempted && fixture.Controller.Status.Message.Contains(released ? "Netzwerk geändert" : "adapter reset failure"),
            "idle after unexpected termination preserves diagnosis and never launches a helper: " + released);
        check(!fixture.Controller.IsAvailable, "recovery fixture has no packaged helper and cannot trigger elevation");
        await fixture.Controller.ApplyAsync(8, fixture.Timeout.Token, qosEnabled: true, protectionStillNeeded: () => false);
        check(Field<int>(fixture.Controller, "recoveryAttempts") == 0,
            "a no-longer-needed gaming policy cannot trigger recovery: " + released);
        await fixture.Controller.ApplyAsync(8, fixture.Timeout.Token, qosEnabled: true, protectionStillNeeded: () => true);
        check(Field<int>(fixture.Controller, "recoveryAttempts") == (released ? 1 : 0) && fixture.Controller.StartAttempted,
            "new gaming permits exactly one recovery only after confirmed clean release: " + released);
        var diagnosis = fixture.Controller.Status.Message;
        await fixture.Controller.ApplyAsync(null, fixture.Timeout.Token);
        await fixture.Controller.ApplyAsync(8, fixture.Timeout.Token, qosEnabled: true);
        check(Field<int>(fixture.Controller, "recoveryAttempts") == (released ? 1 : 0) && fixture.Controller.Status.Message == diagnosis,
            "repeated gaming heartbeats never loop helper launches or overwrite the failure: " + released);
        await fixture.Controller.StopAsync();
    }

    private static async Task BufferedTerminalFrame(Action<bool, string> check)
    {
        using var fixture = await ControllerPipe.CreateAsync();
        Set(fixture.Controller, "captureRequested", true);
        Set(fixture.Controller, "writer", new StreamWriter(new FailingWriteStream(), new UTF8Encoding(false)) { AutoFlush = true });
        var stopping = fixture.Controller.StopAsync();
        const string cause = "Download-Helfer sicher beendet: Seit 7 Sekunden kein Steuerbefehl von DYNEX empfangen.";
        await fixture.Output.WriteLineAsync(JsonSerializer.Serialize(new { active = false, mbps = (double?)null, stopped = true,
            releaseError = (string?)null, error = cause, stopReason = "heartbeat-timeout" }));
        fixture.Client.Dispose();
        await stopping.WaitAsync(fixture.Timeout.Token);
        check(fixture.Controller.CaptureReleased && fixture.Controller.Status.Message == cause,
            "failed shutdown write still drains the final release ACK and preserves the watchdog diagnosis");
    }

    private static async Task WatchdogRelease(Action<bool, string> check)
    {
        var device = new FakeDevice(_ => { });
        using var fixture = await HelperPipe.CreateAsync(() => device);
        await fixture.Input.ReadLineAsync(fixture.Timeout.Token);
        await fixture.Output.WriteLineAsync("{\"mbps\":8,\"commandId\":1}");
        await fixture.Input.ReadLineAsync(fixture.Timeout.Token);
        using var final = JsonDocument.Parse((await fixture.Input.ReadLineAsync(fixture.Timeout.Token))!);
        check(final.RootElement.GetProperty("stopped").GetBoolean() && final.RootElement.GetProperty("captureReleased").GetBoolean() &&
            final.RootElement.GetProperty("stopReason").GetString() == "heartbeat-timeout" &&
            final.RootElement.GetProperty("error").GetString()!.Contains("7 Sekunden") && device.Disposed,
            "unchanged seven-second lease releases capture and reports its precise terminal reason");
        check(await fixture.Helper.WaitAsync(fixture.Timeout.Token) == 0,
            "watchdog termination confirms successful native release instead of pretending an IPC crash");
    }

    private static async Task InvalidCommandRelease(Action<bool, string> check)
    {
        var device = new FakeDevice(_ => { });
        using var fixture = await HelperPipe.CreateAsync(() => device);
        await fixture.Input.ReadLineAsync(fixture.Timeout.Token);
        await fixture.Output.WriteLineAsync("{\"mbps\":8,\"commandId\":1}");
        await fixture.Input.ReadLineAsync(fixture.Timeout.Token);
        await fixture.Output.WriteLineAsync("{\"mbps\":8,\"commandId\":-1}");
        using var final = JsonDocument.Parse((await fixture.Input.ReadLineAsync(fixture.Timeout.Token))!);
        check(final.RootElement.GetProperty("stopped").GetBoolean() && final.RootElement.GetProperty("captureReleased").GetBoolean() &&
            final.RootElement.GetProperty("stopReason").GetString() == "helper-error" &&
            final.RootElement.GetProperty("error").GetString()!.Contains("FormatException") && device.Disposed,
            "invalid command final frame retains the actual protocol failure after safe release");
        check(await fixture.Helper.WaitAsync(fixture.Timeout.Token) == 1, "helper error returns a failure exit code even with successful release");
    }

    private static async Task RearmAfterIdle(Action<bool, string> check)
    {
        var created = 0;
        using var fixture = await HelperPipe.CreateAsync(() => ++created == 1
            ? throw new IOException("injected initial device failure") : new FakeDevice(_ => { }));
        await fixture.Input.ReadLineAsync(fixture.Timeout.Token);
        async Task<JsonDocument> Send(string command)
        {
            await fixture.Output.WriteLineAsync(command);
            return JsonDocument.Parse((await fixture.Input.ReadLineAsync(fixture.Timeout.Token))!);
        }
        using (var failed = await Send("{\"mbps\":8,\"commandId\":1}"))
            check(created == 1 && failed.RootElement.GetProperty("error").GetString() == "injected initial device failure",
                "capture setup failure is reported with its original cause");
        using (await Send("{\"mbps\":8,\"commandId\":2}"))
            check(created == 1, "continuous policy heartbeats do not repeatedly recreate a failed device");
        using (var idle = await Send("{\"mbps\":null,\"commandId\":3}"))
            check(idle.RootElement.GetProperty("captureReleased").GetBoolean(), "idle explicitly confirms release after failed capture setup");
        using (await Send("{\"mbps\":8,\"commandId\":4}"))
            check(created == 2, "fresh gaming after confirmed idle can create capture again");
        using (await Send("{\"mbps\":null,\"shutdown\":true}")) { }
        await fixture.Helper.WaitAsync(fixture.Timeout.Token);
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!ready()) await Task.Delay(5, timeout.Token);
    }

    private sealed class ControllerPipe : IDisposable
    {
        public NamedPipeServerStream Server { get; }
        public NamedPipeClientStream Client { get; }
        public CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(12));
        public ShaperController Controller { get; } = new();
        public StreamReader Input { get; private set; } = null!;
        public StreamWriter Output { get; private set; } = null!;
        private ControllerPipe(string name)
        {
            Server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Client = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        }
        public static async Task<ControllerPipe> CreateAsync()
        {
            var fixture = new ControllerPipe("dynex-controller-recovery-" + Guid.NewGuid().ToString("N"));
            var accept = fixture.Server.WaitForConnectionAsync(fixture.Timeout.Token);
            await fixture.Client.ConnectAsync(fixture.Timeout.Token); await accept;
            fixture.Input = new(fixture.Client, leaveOpen: true);
            fixture.Output = new(fixture.Client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            Set(fixture.Controller, "pipe", fixture.Server);
            Set(fixture.Controller, "writer", new StreamWriter(fixture.Server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true });
            Set(fixture.Controller, "startAttempted", true);
            var lifetime = new CancellationTokenSource();
            Set(fixture.Controller, "cancellation", lifetime);
            Set(fixture.Controller, "reader", typeof(ShaperController).GetMethod("ReadStatusAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(fixture.Controller, [fixture.Server, lifetime.Token])!);
            return fixture;
        }
        public async Task ShutdownAsync()
        {
            var stopping = Controller.StopAsync();
            await Input.ReadLineAsync(Timeout.Token);
            await Output.WriteLineAsync("{\"active\":false,\"mbps\":null,\"stopped\":true,\"releaseError\":null}");
            Client.Dispose();
            await stopping.WaitAsync(Timeout.Token);
        }
        public void Dispose() { Timeout.Cancel(); Client.Dispose(); Server.Dispose(); Timeout.Dispose(); }
    }

    private sealed class HelperPipe : IDisposable
    {
        private readonly NamedPipeServerStream server;
        private readonly NamedPipeClientStream client;
        private readonly CancellationTokenSource stop = new();
        private readonly Process parent = Process.GetCurrentProcess();
        public CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(12));
        public StreamReader Input { get; private set; } = null!;
        public StreamWriter Output { get; private set; } = null!;
        public Task<int> Helper { get; private set; } = null!;
        private HelperPipe(string name)
        {
            server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            client = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        }
        public static async Task<HelperPipe> CreateAsync(Func<IPacketDevice> createDevice)
        {
            var fixture = new HelperPipe("dynex-helper-recovery-" + Guid.NewGuid().ToString("N"));
            var accept = fixture.server.WaitForConnectionAsync(fixture.Timeout.Token);
            await fixture.client.ConnectAsync(fixture.Timeout.Token); await accept;
            fixture.Input = new(fixture.server, leaveOpen: true);
            fixture.Output = new(fixture.server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            fixture.Helper = (Task<int>)typeof(PacketPump).Assembly.GetType("Dynex.Shaper.Program")!
                .GetMethod("RunConnectedAsync", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [fixture.parent, new StreamReader(fixture.client, leaveOpen: true),
                    new StreamWriter(fixture.client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true }, fixture.stop, createDevice])!;
            return fixture;
        }
        public void Dispose() { stop.Cancel(); client.Dispose(); server.Dispose(); Timeout.Dispose(); stop.Dispose(); parent.Dispose(); }
    }

    private sealed class FailingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("injected broken pipe write");
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
