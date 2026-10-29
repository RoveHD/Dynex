using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Dynex.Native;

/// <summary>The only operation exposed by the installed privileged broker is to start its own worker.</summary>
internal static class PrioritizationServiceClient
{
    internal const string ServiceName = "DYNEX.Prioritization";
    internal const string BrokerPipeName = "DYNEX-Prioritization-v1";
    internal const string RegistryPath = @"SOFTWARE\DYNEX\Prioritization";
    internal const string SetupMessage = "Netzwerkpriorisierung bitte einmal über das aktuelle DYNEX-Setup einrichten oder reparieren.";

    internal sealed record LaunchRequest(string PipeName, long ParentStartedAtUtcTicks);
    internal sealed record LaunchReply(int ProcessId, long StartedAtUtcTicks, string? Error = null);
    internal static readonly JsonSerializerOptions JsonOptions = new()
    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    internal static bool IsSessionPipeName(string? name) => name is { Length: 38 } && name.StartsWith("DYNEX-", StringComparison.Ordinal) &&
        name.AsSpan(6).IndexOfAnyExcept("0123456789abcdef".AsSpan()) < 0;

    internal static PipeSecurity SessionSecurity(SecurityIdentifier user)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    internal static async Task<WorkerProcess> LaunchAsync(string pipeName, CancellationToken cancellation)
    {
        if (!IsSessionPipeName(pipeName)) throw new ArgumentException("Ungültige DYNEX-Verbindung.", nameof(pipeName));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var servicePid = await EnsureRunningAsync(timeout.Token);
        // Generic write also requests FILE_CREATE_PIPE_INSTANCE. Request only client rights so
        // interactive users cannot create an additional instance of the privileged broker endpoint.
        using var pipe = new NamedPipeClientStream(".", BrokerPipeName, PipeAccessRights.ReadWrite,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None);
        await pipe.ConnectAsync(timeout.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != servicePid || serverPid != GetRunningServiceProcessId())
            throw new UnauthorizedAccessException("Die Netzwerkverbindung stammt nicht vom installierten DYNEX-Dienst.");
        using var current = Process.GetCurrentProcess();
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 1024, true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new LaunchRequest(pipeName, current.StartTime.ToUniversalTime().Ticks)).AsMemory(), timeout.Token);
        var reply = JsonSerializer.Deserialize<LaunchReply>(await ReadFrameAsync(reader, timeout.Token), JsonOptions)
            ?? throw new IOException("Der Netzwerkdienst hat keine Startbestätigung geliefert.");
        if (reply.Error is not null)
        {
            await writer.WriteLineAsync("received".AsMemory(), timeout.Token);
            throw new IOException(reply.Error);
        }
        if (reply.ProcessId <= 0 || reply.StartedAtUtcTicks <= 0) throw new IOException("Ungültige Startbestätigung des Netzwerkdiensts.");
        var worker = WorkerProcess.Open(reply.ProcessId, reply.StartedAtUtcTicks);
        try
        {
            await writer.WriteLineAsync("received".AsMemory(), timeout.Token);
            return worker;
        }
        catch { worker.Dispose(); throw; }
    }

    /// <summary>A pinned worker identity with only the query/wait rights granted by the broker.</summary>
    internal sealed class WorkerProcess : IDisposable
    {
        private const uint QueryLimitedInformation = 0x1000, Synchronize = 0x100000;
        private readonly ProcessExitWaitHandle exit;
        internal int Id { get; }
        internal long StartedAtUtcTicks { get; }
        internal bool HasExited => exit.WaitOne(0);
        internal int ExitCode
        {
            get
            {
                if (!HasExited) throw new InvalidOperationException("Der Netzwerkhelfer läuft noch.");
                if (!GetExitCodeProcess(exit.SafeWaitHandle, out var code)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return unchecked((int)code);
            }
        }

        private WorkerProcess(int id, long started, ProcessExitWaitHandle exit)
        { Id = id; StartedAtUtcTicks = started; this.exit = exit; }

        internal static WorkerProcess Open(int id, long expectedStartedAtUtcTicks)
        {
            // Process.WaitForExitAsync also requests PROCESS_QUERY_INFORMATION (0x400),
            // which the SYSTEM worker deliberately does not grant to desktop users.
            // Retain the exact process object with QUERY_LIMITED_INFORMATION | SYNCHRONIZE.
            var handle = OpenProcess(QueryLimitedInformation | Synchronize, false, id);
            if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            var wait = new ProcessExitWaitHandle(handle);
            try
            {
                if (!GetProcessTimes(handle, out var created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var started = DateTime.FromFileTimeUtc(created).Ticks;
                if (started != expectedStartedAtUtcTicks || wait.WaitOne(0))
                    throw new IOException("Die Identität des Netzwerkhelfers hat sich geändert oder der Helfer wurde bereits beendet.");
                return new(id, started, wait);
            }
            catch { wait.Dispose(); throw; }
        }

        internal async Task WaitForExitAsync(CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ThreadPool.RegisterWaitForSingleObject(exit, static (state, _) =>
                ((TaskCompletionSource)state!).TrySetResult(), completion, Timeout.Infinite, executeOnlyOnce: true);
            try { await completion.Task.WaitAsync(cancellation).ConfigureAwait(false); }
            finally { registration.Unregister(null); }
        }

        public void Dispose() => exit.Dispose();
        private sealed class ProcessExitWaitHandle : WaitHandle
        { internal ProcessExitWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle; }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeWaitHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(SafeWaitHandle process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint code);
    }

    internal static async Task<string> ReadFrameAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1025];
        var one = new char[1];
        var length = 0;
        while (await reader.ReadAsync(one.AsMemory(), token) != 0)
        {
            if (one[0] == '\n') return new string(buffer, 0, length).TrimEnd('\r');
            if (length == 1024) throw new IOException("Die Nachricht des Netzwerkdiensts ist zu lang.");
            buffer[length++] = one[0];
        }
        throw new EndOfStreamException("Die Verbindung zum Netzwerkdienst wurde beendet.");
    }

    internal static uint GetRunningServiceProcessId() => GetServiceProcessId(requireRunning: true);

    internal static uint GetServiceProcessId(bool requireRunning = false)
    {
        using var manager = OpenSCManager(null, null, 1);
        using var service = OpenService(manager, ServiceName, 4);
        if (manager.IsInvalid || service.IsInvalid) return 0;
        return QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _) &&
            (!requireRunning || status.State == 4) ? status.ProcessId : 0;
    }

    private static async Task<uint> EnsureRunningAsync(CancellationToken token)
    {
        using var manager = OpenSCManager(null, null, 1);
        using var service = OpenService(manager, ServiceName, 1 | 4 | 16);
        if (manager.IsInvalid || service.IsInvalid) throw new IOException(SetupMessage);
        ValidateConfiguration(service);
        if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (status.State == 1 && !StartService(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056)
            throw new IOException(SetupMessage);
        // SCM start-pending is an explicit state; wait only for this bounded operation, not a launch retry loop.
        while (true)
        {
            if (!QueryServiceStatusEx(service, 0, out status, Marshal.SizeOf<ServiceStatusProcess>(), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (status.State == 4 && status.ProcessId != 0) return status.ProcessId;
            if (status.State is not (2 or 4)) throw new IOException(SetupMessage);
            await Task.Delay(100, token);
        }
    }

    private static void ValidateConfiguration(ServiceHandle service)
    {
        QueryServiceConfig(service, IntPtr.Zero, 0, out var required);
        if (required <= 0 || required > 65536) throw new IOException(SetupMessage);
        var buffer = Marshal.AllocHGlobal(required);
        try
        {
            if (!QueryServiceConfig(service, buffer, required, out _)) throw new IOException(SetupMessage);
            var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
            if (config.Type != 0x10 || !IsProtectedServiceCommand(Marshal.PtrToStringUni(config.BinaryPath), Marshal.PtrToStringUni(config.Account)))
                throw new UnauthorizedAccessException(SetupMessage);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static bool IsProtectedServiceCommand(string? command, string? account)
    {
        if (!string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase) || command is null ||
            !command.StartsWith('"') || !command.EndsWith("\" --service", StringComparison.Ordinal)) return false;
        var path = command[1..^11];
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DYNEX", "Prioritization") + Path.DirectorySeparatorChar;
        if (!Path.IsPathFullyQualified(path) || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        var relative = path[root.Length..];
        return relative.Length == 64 + 1 + "Dynex.Shaper.exe".Length && relative.AsSpan(0, 64).IndexOfAnyExcept("0123456789abcdefABCDEF".AsSpan()) < 0 &&
            relative[64] == Path.DirectorySeparatorChar && string.Equals(relative[65..], "Dynex.Shaper.exe", StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        internal uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint, ProcessId, Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfig
    {
        internal uint Type, Start, ErrorControl;
        internal IntPtr BinaryPath, LoadOrderGroup;
        internal uint TagId;
        internal IntPtr Dependencies, Account, DisplayName;
    }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr service);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatusProcess status, int size, out int needed);
    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(ServiceHandle service, IntPtr config, int size, out int needed);
    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(ServiceHandle service, int count, IntPtr args);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
