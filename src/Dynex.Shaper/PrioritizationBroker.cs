using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Dynex.Native;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Dynex.Shaper;

internal sealed class PrioritizationBroker : IDisposable
{
    private Process? worker;
    private EventWaitHandle? workerStop;
    private readonly string executable = Environment.ProcessPath ?? throw new IOException("Dienstpfad fehlt.");

    internal NamedPipeServerStream CreateEndpoint()
        => NamedPipeServerStreamAcl.Create(PrioritizationServiceClient.BrokerPipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 1024, 1024, CreateSecurity());

    internal static PipeSecurity CreateSecurity(SecurityIdentifier? owner = null)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(owner ?? new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        // Microsoft recommends denying NETWORK for local-only named pipes. Interactive also includes local RDP sessions.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    internal async Task RunAsync(NamedPipeServerStream endpoint, CancellationToken stop)
    {
        using (endpoint)
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await endpoint.WaitForConnectionAsync(stop);
                try { await HandleAsync(endpoint, stop); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or
                    ArgumentException or JsonException or OperationCanceledException or Win32Exception) { }
                finally { endpoint.Disconnect(); }
            }
        }
        finally
        {
            workerStop?.Set();
            if (worker is not null)
            {
                // Stop is cooperative: RunConnectedAsync always drains the native capture and sends its release ACK.
                // Never terminate an in-flight packet worker merely to finish an update.
                await worker.WaitForExitAsync();
                if (worker.ExitCode != 0)
                    throw new IOException("Die sichere Abschaltung des Netzwerkhelfers wurde nicht bestätigt.");
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid is 0 or > int.MaxValue)
            throw new UnauthorizedAccessException("Lokale Clientidentität fehlt.");
        using var parent = Process.GetProcessById((int)pid);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 1024, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        // ImpersonateNamedPipeClient binds to the security context of the last message read.
        // Consume only a bounded frame before authenticating; do not perform any requested action yet.
        var frame = await PrioritizationServiceClient.ReadFrameAsync(reader, timeout.Token);
        var identity = ReadParentIdentity(parent);
        string? impersonatedSid = null;
        pipe.RunAsClient(() => { using var user = WindowsIdentity.GetCurrent(); impersonatedSid = user.User?.Value; });
        if (identity.Sid.Value != impersonatedSid || parent.HasExited || parent.SessionId == 0)
            throw new UnauthorizedAccessException("Clientidentität stimmt nicht überein.");
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var registration = registry.OpenSubKey(PrioritizationServiceClient.RegistryPath + @"\Clients\" + identity.Sid.Value);
        var registered = registration?.GetValue("ApplicationPath") as string;
        var image = QueryImage(parent.SafeHandle);
        if (!IsRegisteredApplication(image, registered)) throw new UnauthorizedAccessException("DYNEX ist für diesen Benutzer nicht eingerichtet.");
        var request = JsonSerializer.Deserialize<PrioritizationServiceClient.LaunchRequest>(frame, PrioritizationServiceClient.JsonOptions);
        if (request is null || !PrioritizationServiceClient.IsSessionPipeName(request.PipeName) || request.ParentStartedAtUtcTicks != parent.StartTime.ToUniversalTime().Ticks || parent.HasExited)
            throw new UnauthorizedAccessException("Ungültige Netzwerkhelfer-Anforderung.");
        PrioritizationServiceClient.LaunchReply reply;
        if (worker is not null && !worker.HasExited)
            reply = new(0, 0, "Die Netzwerkpriorisierung wird bereits von einer DYNEX-Sitzung verwendet.");
        else if (worker is not null && worker.ExitCode != 0)
            reply = new(0, 0, "Der Netzwerkhelfer wurde mit einem Fehler beendet. Die sichere Freigabe ist nicht bestätigt; Windows bitte neu starten.");
        else
        {
            worker?.Dispose(); worker = null;
            workerStop?.Dispose(); workerStop = null;
            var stopName = @"Global\DYNEX-WorkerStop-" + Guid.NewGuid().ToString("N");
            workerStop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName, out var created);
            if (!created) throw new IOException("Die Abschaltverbindung ist bereits belegt.");
            worker = StartWorker(executable, request.PipeName, parent.Id, request.ParentStartedAtUtcTicks, identity.Sid, stopName);
            reply = new(worker.Id, worker.StartTime.ToUniversalTime().Ticks);
        }
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(reply).AsMemory(), timeout.Token);
            // DisconnectNamedPipe discards unread bytes. A bounded receipt confirms the client consumed the reply.
            if (await PrioritizationServiceClient.ReadFrameAsync(reader, timeout.Token) != "received")
                throw new IOException("Startbestätigung wurde nicht empfangen.");
        }
        catch { if (reply.ProcessId != 0) workerStop?.Set(); throw; }
    }

    internal static bool IsRegisteredApplication(string image, string? registered) =>
        !string.IsNullOrWhiteSpace(registered) && Path.IsPathFullyQualified(registered) &&
        string.Equals(Path.GetFileName(image), "DYNEX.exe", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetFullPath(image), Path.GetFullPath(registered), StringComparison.OrdinalIgnoreCase);

    internal sealed record ParentIdentity(SecurityIdentifier Sid, string LocalAppData);
    internal static ParentIdentity ReadParentIdentity(Process parent)
    {
        if (!OpenProcessToken(parent.SafeHandle, 0x0008 | 0x0004, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            var sid = identity.User ?? throw new UnauthorizedAccessException("Benutzeridentität fehlt.");
            var folderId = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
            var result = SHGetKnownFolderPath(ref folderId, 0x4000, token, out var path);
            if (result != 0) Marshal.ThrowExceptionForHR(result);
            try { return new(sid, Marshal.PtrToStringUni(path) ?? throw new IOException("Benutzerprofil fehlt.")); }
            finally { Marshal.FreeCoTaskMem(path); }
        }
    }

    private static string QueryImage(SafeProcessHandle process)
    {
        var result = new StringBuilder(32768);
        var size = result.Capacity;
        if (!QueryFullProcessImageName(process, 0, result, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return result.ToString();
    }

    private static Process StartWorker(string executable, string pipe, int parent, long started, SecurityIdentifier sid, string stopEvent)
    {
        // Fixed executable, fixed argument grammar, no shell, and no client-selected executable, working directory or environment.
        // The caller can observe exit/time only. It cannot terminate, inject into or change the SYSTEM worker.
        var descriptor = new RawSecurityDescriptor($"O:SYG:SYD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00101000;;;{sid.Value})");
        var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
        var securityPointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, securityPointer, bytes.Length);
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = securityPointer };
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            var command = new StringBuilder($"\"{executable}\" --service-worker {pipe} --parent {parent} --parent-start {started} --broker {Environment.ProcessId} --stop-event {stopEvent}");
            if (!CreateProcess(executable, command, ref attributes, IntPtr.Zero, false, 0x08000000, IntPtr.Zero,
                AppContext.BaseDirectory, ref startup, out var information)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using var processHandle = new SafeProcessHandle(information.Process, true);
            using var threadHandle = new SafeWaitHandle(information.Thread, true);
            return Process.GetProcessById(information.ProcessId);
        }
        finally { Marshal.FreeHGlobal(securityPointer); }
    }

    public void Dispose() { worker?.Dispose(); workerStop?.Dispose(); }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size; internal string? Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        internal ushort Show, ReservedSize; internal IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { internal IntPtr Process, Thread; internal int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, ref SecurityAttributes processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, SafeAccessTokenHandle token, out IntPtr path);
}
