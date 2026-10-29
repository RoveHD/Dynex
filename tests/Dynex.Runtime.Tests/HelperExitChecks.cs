using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Dynex.Desktop;
using Dynex.Native;
using Microsoft.Win32.SafeHandles;

internal static class HelperExitChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        using var standard = StandardToken();
        await WindowsIdentity.RunImpersonatedAsync(standard, async () =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            check(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
                "helper exit regression runs with an effective standard-user token, including on elevated CI");
            using var child = StartChild(identity.User!, 259);
            var started = child.StartTime.ToUniversalTime().Ticks;
            foreach (var rights in new uint[] { 0x400, 1, 0x20 })
            {
                using var denied = OpenProcess(rights, false, child.Id);
                var error = Marshal.GetLastWin32Error();
                check(denied.IsInvalid && error == 5, $"worker ACL denies excess process right 0x{rights:x} to the standard user");
            }
            using (var legacy = Process.GetProcessById(child.Id))
            {
                var denied = false;
                try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1)); await legacy.WaitForExitAsync(timeout.Token); }
                catch (Win32Exception error) when (error.NativeErrorCode == 5) { denied = true; }
                check(denied, "old Process.WaitForExitAsync reproduces access denied against the exact worker ACL");
            }
            var rejectedIdentity = false;
            try { using var wrong = PrioritizationServiceClient.WorkerProcess.Open(child.Id, started + 1); }
            catch (IOException) { rejectedIdentity = true; }
            check(rejectedIdentity, "worker handle refuses a reused PID or mismatching authenticated start time");
            using var worker = PrioritizationServiceClient.WorkerProcess.Open(child.Id, started);
            check(worker.Id == child.Id && worker.StartedAtUtcTicks == started && !worker.HasExited,
                "minimal-rights handle pins and validates the live authenticated worker identity");
            using (var cancelled = new CancellationTokenSource())
            {
                var wait = worker.WaitForExitAsync(cancelled.Token);
                cancelled.Cancel();
                var observed = false;
                try { await wait; } catch (OperationCanceledException) { observed = true; }
                check(observed && !worker.HasExited, "cancelled worker wait does not falsely confirm process exit");
            }
            var exit = worker.WaitForExitAsync();
            check(!exit.IsCompleted, "native wait remains pending until the actual process object is signalled");
            await child.StandardInput.WriteLineAsync("exit");
            await exit.WaitAsync(TimeSpan.FromSeconds(5));
            child.Dispose();
            check(worker.HasExited && worker.ExitCode == 259,
                "retained handle proves exit and preserves even exit code 259 after the original handle closes");
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
            check(true, "waiting again on an exited retained worker needs no PID lookup or stronger process rights");

            await ControllerShutdownAsync(identity.User!, finalAcknowledgement: true, check);
            await ControllerShutdownAsync(identity.User!, finalAcknowledgement: false, check);
        });
    }

    private static async Task ControllerShutdownAsync(SecurityIdentifier sid, bool finalAcknowledgement, Action<bool, string> check)
    {
        using var child = StartChild(sid, 0);
        using var worker = PrioritizationServiceClient.WorkerProcess.Open(child.Id, child.StartTime.ToUniversalTime().Ticks);
        var name = "dynex-exit-regression-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(pipe.WaitForConnectionAsync(), client.ConnectAsync(5000));
        using var lifetime = new CancellationTokenSource();
        var controller = new ShaperController();
        void Set(string field, object value) => typeof(ShaperController).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, value);
        Set("process", worker); Set("pipe", pipe); Set("cancellation", lifetime); Set("captureRequested", true);
        Set("writer", new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true });
        Set("reader", (Task)typeof(ShaperController).GetMethod("ReadStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(controller, [pipe, lifetime.Token])!);
        using var input = new StreamReader(client, leaveOpen: true);
        var output = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var shutdown = controller.StopAsync(requireExit: true);
        var command = await input.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
        check(command?.Contains("\"shutdown\":true", StringComparison.Ordinal) == true,
            "controller requests cooperative shutdown before waiting on the protected process");
        await output.WriteLineAsync("{\"active\":false,\"qosActive\":false,\"mbps\":null,\"captureReleased\":true,\"releaseError\":null,\"stopped\":" +
            (finalAcknowledgement ? "true" : "false") + "}");
        if (finalAcknowledgement)
        {
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!controller.CaptureReleased) await Task.Delay(10, ready.Token);
            check(!shutdown.IsCompleted, "authenticated final release frame alone cannot permit an update while the worker remains alive");
        }
        output.Dispose();
        client.Dispose();
        await child.StandardInput.WriteLineAsync("exit");
        var rejected = false;
        try { await shutdown.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (InvalidOperationException) when (!finalAcknowledgement) { rejected = true; }
        check(finalAcknowledgement ? !rejected && controller.CaptureReleased : rejected && !controller.CaptureReleased,
            finalAcknowledgement ? "standard-user controller shutdown confirms BOTH the final release frame and real worker exit" :
            "worker exit without its authenticated final release frame still blocks the update");
    }

    private static Process StartChild(SecurityIdentifier sid, int exitCode)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add("--helper-exit-child"); start.ArgumentList.Add(exitCode.ToString());
        var child = Process.Start(start)!;
        try
        {
            // Only this harmless stdin-gated test child is changed. Keep its original full-access
            // creation handle for cleanup; future opens receive the production worker's exact DACL.
            var descriptor = new RawSecurityDescriptor($"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00101000;;;{sid.Value})");
            var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
            if (!SetKernelObjectSecurity(child.SafeHandle, 0x80000004, bytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return child;
        }
        catch { child.StandardInput.Dispose(); child.Dispose(); throw; }
    }

    private static SafeAccessTokenHandle StandardToken()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0);
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            if (!CreateRestrictedToken(identity.AccessToken, 1, 1, [new SidAndAttributes { Sid = memory }], 0, IntPtr.Zero,
                0, IntPtr.Zero, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return token;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes { internal IntPtr Sid; internal uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existing, uint flags, uint disabledCount,
        SidAndAttributes[] disabled, uint deletedCount, IntPtr deleted, uint restrictedCount, IntPtr restricted, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeProcessHandle process, uint information, byte[] descriptor);
}
