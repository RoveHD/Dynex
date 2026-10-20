using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Dynex.Native;
using Microsoft.Win32.SafeHandles;

namespace Dynex.Shaper;

/// <summary>Optional local ELFIX evidence. All authentication and I/O run outside the packet thread.</summary>
internal sealed class MediaEvidenceReceiver
{
    private readonly string _pipeName;
    private readonly Func<int, MediaEvidenceProcess?> _resolve;
    private MediaEvidenceSnapshot _snapshot = MediaEvidenceSnapshot.Empty;
    internal MediaEvidenceSnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal Exception? Failure { get; private set; }

    internal MediaEvidenceReceiver(string? pipeName = null, Func<int, MediaEvidenceProcess?>? resolve = null)
    {
        _pipeName = pipeName ?? MediaEvidenceProtocol.PipeName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        _resolve = resolve ?? ResolveProcess;
    }

    internal async Task RunAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                // FirstPipeInstance prevents quietly joining a pipe namespace already owned by another server.
                // Node's net client opens duplex handles even though evidence only travels toward DYNEX.
                using var pipe = NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance,
                    0, 0, null, HandleInheritability.None, PipeAccessRights.ChangePermissions | PipeAccessRights.TakeOwnership);
                RestrictToCurrentUser(pipe);
                await pipe.WaitForConnectionAsync(stop);
                try
                {
                    if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid is 0 or > int.MaxValue) continue;
                    var client = _resolve((int)pid);
                    if (client is null || !MediaEvidenceProtocol.IsElfix(client)) continue;
                    var reader = new MediaEvidenceFrameReader(pipe);
                    var rate = new MediaEvidenceRateLimit();
                    while (!stop.IsCancellationRequested)
                    {
                        // An idle or partial-frame client must not monopolize the single server indefinitely.
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                        timeout.CancelAfter(TimeSpan.FromSeconds(MediaEvidenceSnapshot.MaximumAgeSeconds));
                        var frame = await reader.ReadAsync(timeout.Token);
                        if (frame is null) break;
                        var now = MediaEvidenceProtocol.Now;
                        if (!rate.Accept(now)) break;
                        var snapshot = MediaEvidenceProtocol.Parse(frame, client, _resolve, now);
                        Volatile.Write(ref _snapshot, snapshot);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or
                    System.Text.Json.JsonException or InvalidOperationException or ArgumentException or OperationCanceledException) { }
                finally { Volatile.Write(ref _snapshot, MediaEvidenceSnapshot.Empty); }
            }
        }
        // Failure of this optional endpoint must never stop game QoS or the helper.
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) { Failure = e; }
        finally { Volatile.Write(ref _snapshot, MediaEvidenceSnapshot.Empty); }
    }

    private const uint QueryLimitedInformation = 0x1000;

    private static void RestrictToCurrentUser(NamedPipeServerStream pipe)
    {
        // .NET's CurrentUserOnly ACL uses token Owner, which becomes Administrators
        // under UAC. Bind explicitly to User so a normal Electron client can connect.
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new UnauthorizedAccessException("Missing media pipe user.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(sid);
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        pipe.SetAccessControl(security);
    }

    private static MediaEvidenceProcess? ResolveProcess(int pid)
    {
        // Read the system table first: never open handles to games or unrelated applications.
        if (!ProcessTable.Snapshot().TryGetValue(pid, out var identity) || !string.Equals(identity.Name, "ELFIX", StringComparison.OrdinalIgnoreCase)) return null;
        using var process = OpenProcess(QueryLimitedInformation, false, pid);
        if (process.IsInvalid) return null;
        if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token)) return null;
        using (token)
        using (var clientIdentity = new WindowsIdentity(token.DangerousGetHandle()))
        using (var serverIdentity = WindowsIdentity.GetCurrent())
            if (clientIdentity.User is null || clientIdentity.User != serverIdentity.User) return null;
        var path = new StringBuilder(32768);
        var size = path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref size) || !GetProcessTimes(process, out var created, out var exited, out _, out _) || exited != 0) return null;
        var started = DateTimeOffset.FromFileTime(created).UtcTicks;
        if (started != identity.StartedAtUtc.UtcTicks) return null;
        var result = new MediaEvidenceProcess(pid, started, path.ToString());
        return MediaEvidenceProtocol.IsElfix(result) ? result : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
}
