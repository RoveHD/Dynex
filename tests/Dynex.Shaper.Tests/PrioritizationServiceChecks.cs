using System.IO.Pipes;
using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Dynex.Native;
using Dynex.Shaper;

internal static class PrioritizationServiceChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var validPipe = "DYNEX-" + Guid.NewGuid().ToString("N");
        check(PrioritizationServiceClient.IsSessionPipeName(validPipe), "broker accepts only generated session pipe names");
        foreach (var invalid in new string?[] { null, "", "DYNEX-", "DYNEX-" + new string('g', 32), validPipe + " --service", @"\\server\pipe\name", "DYNEX-../escape" })
            check(!PrioritizationServiceClient.IsSessionPipeName(invalid), "broker rejects malformed/path/argument pipe name: " + invalid);
        check(PrioritizationBroker.IsRegisteredApplication(@"C:\Users\Test\DYNEX.exe", @"c:\users\test\dynex.exe"), "registered desktop path matches case-insensitively");
        check(!PrioritizationBroker.IsRegisteredApplication(@"C:\Users\Other\DYNEX.exe", @"C:\Users\Test\DYNEX.exe"), "unregistered desktop path cannot launch a privileged worker");
        check(!PrioritizationBroker.IsRegisteredApplication(@"C:\Users\Test\evil.exe", @"C:\Users\Test\evil.exe"), "even an incorrect registration cannot approve an arbitrary executable name");
        check(!PrioritizationBroker.IsRegisteredApplication(@"C:\Users\Test\DYNEX.exe", "DYNEX.exe"), "relative registered paths fail closed");
        var binary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DYNEX", "Prioritization", new string('a', 64), "Dynex.Shaper.exe");
        var command = "\"" + binary + "\" --service";
        check(PrioritizationServiceClient.IsProtectedServiceCommand(command, "LocalSystem"), "client accepts fixed protected service command and SYSTEM account");
        foreach (var invalid in new[] { command + " --other", binary + " --service", "\"C:\\Users\\Test\\Dynex.Shaper.exe\" --service",
            command.Replace(new string('a', 64), ".."), command.Replace("Dynex.Shaper.exe", "other.exe") })
            check(!PrioritizationServiceClient.IsProtectedServiceCommand(invalid, "LocalSystem"), "client rejects service paths outside exact protected payload grammar");
        check(!PrioritizationServiceClient.IsProtectedServiceCommand(command, "TestUser"), "client rejects service running as an unexpected account");
        var parsed = JsonSerializer.Deserialize<PrioritizationServiceClient.LaunchRequest>(JsonSerializer.Serialize(new PrioritizationServiceClient.LaunchRequest(validPipe, 123)), PrioritizationServiceClient.JsonOptions);
        check(parsed is { ParentStartedAtUtcTicks: 123 } && parsed.PipeName == validPipe, "broker start protocol round-trips only pipe and parent creation time");
        try
        {
            JsonSerializer.Deserialize<PrioritizationServiceClient.LaunchRequest>("{\"PipeName\":\"" + validPipe + "\",\"ParentStartedAtUtcTicks\":123,\"Executable\":\"cmd.exe\"}", PrioritizationServiceClient.JsonOptions);
            check(false, "broker must reject arbitrary execution fields");
        }
        catch (JsonException) { check(true, "broker rejects arbitrary executable fields instead of ignoring them"); }
        using (var bytes = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 1025) + "\n")))
        using (var reader = new StreamReader(bytes))
        {
            try { await PrioritizationServiceClient.ReadFrameAsync(reader, default); check(false, "oversized broker frame must fail"); }
            catch (IOException) { check(true, "broker frames are bounded before JSON parsing"); }
        }
        using (var bytes = new MemoryStream(Encoding.UTF8.GetBytes("partial")))
        using (var reader = new StreamReader(bytes))
        {
            try { await PrioritizationServiceClient.ReadFrameAsync(reader, default); check(false, "truncated broker frame must fail"); }
            catch (EndOfStreamException) { check(true, "truncated broker frames cannot start a worker"); }
        }
        try { await PrioritizationServiceClient.LaunchAsync("not-a-session", default); check(false, "invalid launch must fail before SCM access"); }
        catch (ArgumentException) { check(true, "invalid launch fails before any service or process operation"); }
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!;
        var security = PrioritizationServiceClient.SessionSecurity(sid);
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
        check(rules.Any(r => r.AccessControlType == AccessControlType.Deny && ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.NetworkSid)),
            "worker pipe denies NETWORK identities even for its owner");
        check(rules.Where(r => r.AccessControlType == AccessControlType.Allow).All(r => r.IdentityReference == sid || ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.LocalSystemSid)),
            "worker pipe permits only its desktop user and SYSTEM");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var server = NamedPipeServerStreamAcl.Create(validPipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
        using var client = new NamedPipeClientStream(".", validPipe, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));
        check(server.IsConnected && server.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) == sid,
            "real isolated worker pipe accepts desktop user and preserves authenticated owner with explicit SYSTEM access");
        using var clientWriter = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var serverReader = new StreamReader(server, leaveOpen: true);
        await Task.WhenAll(clientWriter.WriteLineAsync("request".AsMemory(), timeout.Token),
            PrioritizationServiceClient.ReadFrameAsync(serverReader, timeout.Token));
        SecurityIdentifier? authenticated = null;
        server.RunAsClient(() => { using var caller = WindowsIdentity.GetCurrent(); authenticated = caller.User; });
        check(authenticated == sid, "broker identification-level handshake binds the consumed request to its actual Windows SID");
        await BrokerAclHandshake(check, sid);
        await WorkerLifetime(check);
    }

    private static async Task WorkerLifetime(Action<bool, string> check)
    {
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
            start.ArgumentList.Add("--process-lifetime-child");
            using var launched = Process.Start(start)!;
            using var tracked = PrioritizationBroker.TrackWorker(launched.Id);
            // Explicit IPC releases the harmless child only after tracking has been established.
            await launched.StandardInput.WriteLineAsync("exit");
            await launched.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            launched.Dispose(); // Mirrors closing the original CreateProcess handle in the broker.
            check(tracked.HasExited && tracked.ExitCode == 0,
                $"broker retains actual worker exit code after the child and original process handle are gone, cycle {cycle}");
        }
    }

    private static async Task BrokerAclHandshake(Action<bool, string> check, SecurityIdentifier sid)
    {
        var name = "dynex-broker-acl-test-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 1024, 1024, PrioritizationServiceClient.SessionSecurity(sid),
            HandleInheritability.None, PipeAccessRights.ChangePermissions);
        // Keep the existing server handle, then apply the actual broker ACL; only its owner
        // differs because this test deliberately never creates a SYSTEM service or impersonates one.
        server.SetAccessControl(PrioritizationBroker.CreateSecurity(sid));
        try
        {
            using var impostor = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            check(false, "interactive user must not be able to create another broker server instance");
        }
        catch (UnauthorizedAccessException) { check(true, "actual broker ACL denies an interactive user creating another server instance"); }
        using var client = new NamedPipeClientStream(".", name, PipeAccessRights.ReadWrite, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification, HandleInheritability.None);
        await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(server, leaveOpen: true);
        await Task.WhenAll(writer.WriteLineAsync("request".AsMemory(), timeout.Token), PrioritizationServiceClient.ReadFrameAsync(reader, timeout.Token));
        SecurityIdentifier? authenticated = null;
        server.RunAsClient(() => { using var identity = WindowsIdentity.GetCurrent(); authenticated = identity.User; });
        check(authenticated == sid, "specific-rights client completes real broker ACL and identification handshake without CreateNewInstance permission");
    }
}
