using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Dynex.Desktop;
using Dynex.Native;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

// The wrapper stages this executable as DYNEX.exe only on a disposable GitHub-hosted runner.
return await ServiceLifecycleChecks.RunAsync(args);

internal static class ServiceLifecycleChecks
{
    private const string Receipt = @"SOFTWARE\DYNEX\Prioritization";
    private const string ServiceRegistry = @"SYSTEM\CurrentControlSet\Services\DYNEX.Prioritization";
    private static readonly List<string> Checks = [];

    internal static async Task<int> RunAsync(string[] args)
    {
        var attemptedSetup = false;
        var removed = false;
        var workerRuns = 0;
        string? error = null, cleanupError = null, workerSkip = null;
        string? report = null;
        var application = Environment.ProcessPath ?? "";
        try
        {
            if (args is not ["--github-service-smoke", var reportArgument]) throw new ArgumentException("Use tests/service-lifecycle-smoke.ps1 on GitHub-hosted Windows only.");
            AssertHostedRunner();
            var workspace = Path.GetFullPath(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")!);
            var reportPath = Path.GetFullPath(reportArgument);
            var reportRoot = Path.Combine(workspace, "artifacts", "service-lifecycle-smoke") + Path.DirectorySeparatorChar;
            if (!reportPath.StartsWith(reportRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected report directory.");
            report = reportPath;
            var fixture = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DYNEX");
            Check(string.Equals(application, Path.Combine(fixture, "DYNEX.exe"), StringComparison.OrdinalIgnoreCase), "fixture runs as registered DYNEX.exe at the original user's standard install path");
            Check(File.ReadAllText(Path.Combine(fixture, ".github-service-fixture")) == Environment.GetEnvironmentVariable("GITHUB_RUN_ID"), "fixture belongs to this disposable GitHub run");
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            Check(!KeyExists(machine, Receipt) && !KeyExists(machine, ServiceRegistry) &&
                !Directory.Exists(Path.GetDirectoryName(PrioritizationServiceSetup.Root)), "no preexisting service, receipt or protected DYNEX installation is modified");
            var payload = PrioritizationPayload.Load();
            payload.VerifyDirectory(Path.Combine(AppContext.BaseDirectory, "shaper"), allowReceipt: false);
            Check(true, "real embedded helper manifest authenticates all staged source files before service provisioning");
            attemptedSetup = true;
            await PrioritizationServiceSetup.EnsureAsync(application);
            Check(PrioritizationServiceSetup.IsConfigured(application), "production EnsureAsync provisions a fully recognized protected installation");
            var firstPid = PrioritizationServiceClient.GetRunningServiceProcessId();
            Check(firstPid != 0 && Native.ProcessIsSystem((int)firstPid), "actual SCM service is running as LocalSystem");
            var protectedExecutable = Path.Combine(PrioritizationServiceSetup.Root, payload.Hash, "Dynex.Shaper.exe");
            Check(string.Equals(Native.ProcessImage((int)firstPid), protectedExecutable, StringComparison.OrdinalIgnoreCase), "SCM executes the verified protected payload");
            await PrioritizationServiceSetup.EnsureAsync(application);
            Check(PrioritizationServiceClient.GetRunningServiceProcessId() == firstPid, "repeated setup reuses the running service without a second host");

            using var current = Process.GetCurrentProcess();
            if (current.SessionId == 0)
            {
                workerSkip = "Runner is in Session 0; production broker correctly requires an interactive session. Authenticated worker lifecycle was not exercised.";
                Console.WriteLine("SKIP: " + workerSkip);
            }
            else
            {
                for (var cycle = 1; cycle <= 2; cycle++)
                {
                    await IdleWorkerAsync(protectedExecutable, cycle);
                    workerRuns++;
                }
            }
            await InvokeProductionAsync("StopAsync");
            Check(PrioritizationServiceClient.GetRunningServiceProcessId() == 0, "production StopAsync confirms clean SCM shutdown");
            await InvokeProductionAsync("StartAsync");
            var restartedPid = PrioritizationServiceClient.GetRunningServiceProcessId();
            Check(restartedPid != 0 && Native.ProcessIsSystem((int)restartedPid), "production StartAsync restarts the real protected SYSTEM service");
            if (workerSkip is null)
            {
                await IdleWorkerAsync(protectedExecutable, 3);
                workerRuns++;
            }
            await PrioritizationServiceSetup.RemoveAsync(application);
            removed = true;
            Check(!KeyExists(machine, Receipt) && !KeyExists(machine, ServiceRegistry) && !Directory.Exists(PrioritizationServiceSetup.Root),
                "production RemoveAsync removes its SCM service, registry receipt and verified payload");
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            Console.Error.WriteLine(error);
        }
        finally
        {
            if (attemptedSetup && !removed)
            {
                try { await PrioritizationServiceSetup.RemoveAsync(application); removed = true; }
                catch (Exception exception) { cleanupError = exception.ToString(); Console.Error.WriteLine("Owned fixture cleanup failed: " + cleanupError); }
            }
            if (report is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
                {
                    success = error is null && cleanupError is null,
                    checks = Checks, workerRuns, workerSkip,
                    kernelFilterOpened = false, driverInstalled = false, firewallChanged = false,
                    removed, error, cleanupError
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        Console.WriteLine($"Service lifecycle checks: {Checks.Count}; authenticated idle worker runs: {workerRuns}; removed: {removed}.");
        return error is null && cleanupError is null ? 0 : 1;
    }

    private static async Task IdleWorkerAsync(string executable, int cycle)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var pipeName = "DYNEX-" + Guid.NewGuid().ToString("N");
        using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, PrioritizationServiceClient.SessionSecurity(identity.User!));
        using var worker = await PrioritizationServiceClient.LaunchAsync(pipeName, timeout.Token);
        // The elevated fixture queries ExitCode after shutdown. Retain the process
        // handle while it is alive so the result survives broker cleanup/PID reuse.
        _ = worker.SafeHandle;
        await pipe.WaitForConnectionAsync(timeout.Token);
        Check(Native.ClientPid(pipe.SafePipeHandle) == worker.Id && Native.ProcessIsSystem(worker.Id) &&
            string.Equals(Native.ProcessImage(worker.Id), executable, StringComparison.OrdinalIgnoreCase),
            $"cycle {cycle}: authenticated worker is the protected SYSTEM child, not a per-user executable");
        using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        using (var initial = await StatusAsync(reader, timeout.Token)) AssertIdle(initial.RootElement);
        await writer.WriteLineAsync("{\"mbps\":null,\"qosEnabled\":false,\"observeGameTraffic\":false,\"commandId\":1}".AsMemory(), timeout.Token);
        using (var idle = await StatusAsync(reader, timeout.Token))
        {
            AssertIdle(idle.RootElement);
            Check(idle.RootElement.GetProperty("commandId").GetInt64() == 1, $"cycle {cycle}: idle command is acknowledged without packet capture");
        }
        await writer.WriteLineAsync("{\"mbps\":null,\"shutdown\":true,\"commandId\":2}".AsMemory(), timeout.Token);
        var stopped = false;
        for (var frame = 0; frame < 3 && !stopped; frame++)
        {
            using var status = await StatusAsync(reader, timeout.Token);
            AssertIdle(status.RootElement);
            stopped = status.RootElement.GetProperty("stopped").GetBoolean();
            if (stopped)
                Check(status.RootElement.GetProperty("captureReleased").GetBoolean() && status.RootElement.GetProperty("releaseError").ValueKind == JsonValueKind.Null,
                    $"cycle {cycle}: final worker frame confirms release before exit");
        }
        Check(stopped, $"cycle {cycle}: explicit shutdown acknowledgement received");
        await worker.WaitForExitAsync(timeout.Token);
        Check(worker.ExitCode == 0, $"cycle {cycle}: idle worker exits successfully without a UAC launch");
    }

    private static void AssertIdle(JsonElement status)
    {
        if (status.GetProperty("active").GetBoolean() || status.GetProperty("qosActive").GetBoolean() || status.GetProperty("observingGameTraffic").GetBoolean() ||
            status.GetProperty("receivedBytes").GetInt64() != 0 || status.GetProperty("sentBytes").GetInt64() != 0 ||
            status.GetProperty("error").ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("Idle service fixture unexpectedly reports capture or an error: " + status.GetRawText());
    }

    private static async Task<JsonDocument> StatusAsync(StreamReader reader, CancellationToken cancellation)
        => JsonDocument.Parse(await PrioritizationServiceClient.ReadFrameAsync(reader, cancellation));

    private static Task InvokeProductionAsync(string name) => (Task)(typeof(PrioritizationServiceSetup)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!);

    private static bool KeyExists(RegistryKey machine, string name) { using var key = machine.OpenSubKey(name); return key is not null; }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        Checks.Add(message);
        Console.WriteLine("PASS: " + message);
    }

    private static void AssertHostedRunner()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            !long.TryParse(Environment.GetEnvironmentVariable("GITHUB_RUN_ID"), out _) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")))
            throw new InvalidOperationException("System service smoke tests may run only on a disposable GitHub-hosted Windows runner.");
        using var user = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator)) throw new InvalidOperationException("Hosted runner must already be elevated; this test never displays UAC.");
    }

    private static class Native
    {
        internal static bool ProcessIsSystem(int pid)
        {
            using var process = OpenProcess(0x1000, false, pid);
            if (process.IsInvalid || !OpenProcessToken(process, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle())) return identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
        }
        internal static string ProcessImage(int pid)
        {
            using var process = OpenProcess(0x1000, false, pid);
            var image = new StringBuilder(32768);
            var length = image.Capacity;
            if (process.IsInvalid || !QueryFullProcessImageName(process, 0, image, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return image.ToString();
        }
        internal static int ClientPid(SafePipeHandle pipe) => GetNamedPipeClientProcessId(pipe, out var pid) ? checked((int)pid) : throw new Win32Exception(Marshal.GetLastWin32Error());
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref int length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    }
}
