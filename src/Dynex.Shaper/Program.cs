using System.Diagnostics;
using System.ComponentModel;
using System.IO.Pipes;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Dynex.Shaper;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

    public static async Task<int> Main(string[] args)
    {
        Dynex.Native.ProcessEfficiency.UseLatencyCriticalMode();
        try { return await RunAsync(args); }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 6 || args[0] != "--pipe" || args[2] != "--parent" || args[4] != "--parent-start" ||
            args[1].Length is < 1 or > 128 || args[1].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
            !int.TryParse(args[3], out var parentId) || !long.TryParse(args[5], out var parentTicks))
            throw new ArgumentException("Aufruf: Dynex.Shaper --pipe NAME --parent PID --parent-start UTC_TICKS");
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Der Download-Helfer benötigt Windows x64.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Der Download-Helfer benötigt Administratorrechte.");
        using var parent = Process.GetProcessById(parentId);
        if (parent.StartTime.ToUniversalTime().Ticks != parentTicks || parent.HasExited)
            throw new InvalidOperationException("Elternprozess stimmt nicht überein.");
        var userSid = identity.User ?? throw new UnauthorizedAccessException("Benutzeridentität des Helfers fehlt.");
        if (!OpenProcessToken(parent.SafeHandle, 0x0008 /* TOKEN_QUERY */, out var parentToken))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Benutzeridentität des Elternprozesses nicht lesbar.");
        using (parentToken)
        using (var parentIdentity = new WindowsIdentity(parentToken.DangerousGetHandle()))
            if (parentIdentity.User != userSid)
                throw new UnauthorizedAccessException("Elternprozess und Helfer müssen zum gleichen Windows-Benutzer gehören.");

        using var stop = new CancellationTokenSource();
        // CurrentUserOnly on a .NET client compares token Owner, which UAC changes
        // to Administrators. Validate the actual User SID explicitly instead;
        // the server retains its CurrentUserOnly ACL and both ends verify PID.
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        await pipe.ConnectAsync(10000, stop.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverId) || serverId != parentId)
            throw new UnauthorizedAccessException("Pipe-Server ist nicht der erwartete Elternprozess.");
        if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != parentTicks)
            throw new UnauthorizedAccessException("Elternprozess wurde während der Pipe-Verbindung beendet oder ersetzt.");
        if (pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) != userSid)
            throw new UnauthorizedAccessException("Pipe gehört nicht dem Benutzer. DYNEX muss ohne Administratorrechte gestartet werden.");
        using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        try
        {
            return await RunConnectedAsync(parent, reader, writer, stop);
        }
        catch (Exception e)
        {
            // UAC hides stderr. Report only a bounded, non-sensitive diagnostic
            // after authenticating the server and after session cleanup completes.
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    active = false,
                    qosActive = false,
                    gamePriorityPackets = 0L, voicePriorityPackets = 0L,
                    mbps = (double?)null,
                    error = $"Download-Helfer konnte nicht initialisiert oder fortgesetzt werden ({e.GetType().Name}, 0x{e.HResult:X8}).",
                    phase = "helper-error",
                    receivedBytes = 0L, sentBytes = 0L, bypassedBytes = 0L, droppedPackets = 0L
                }).AsMemory(), timeout.Token);
            }
            catch (Exception reportError) when (reportError is IOException or OperationCanceledException or ObjectDisposedException) { }
            throw;
        }
    }

    private static async Task<int> RunConnectedAsync(Process parent, StreamReader reader, StreamWriter writer, CancellationTokenSource stop,
        Func<IPacketDevice>? createDevice = null)
    {
        using var writeLock = new SemaphoreSlim(1);
        PacketPump? session = null;
        double? rate = null;
        double? uploadRate = null;
        var qosEnabled = false;
        var captureOutbound = false;
        string? error = null;
        string? releaseError = null;
        long acknowledgedCommandId = 0;
        using var ownership = new TrafficOwnership();
        var bulkPolicy = new BulkDownloadPolicy(() => ownership.Snapshot);
        var accounting = new TrafficAccounting();
        var qosPolicy = new RealtimeQosPolicy(() => ownership.Snapshot, accounting: accounting);
        var ownershipWorker = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            try
            {
                do { ownership.Refresh(); } while (await timer.WaitForNextTickAsync(stop.Token));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        long lastHeartbeat = Stopwatch.GetTimestamp();
        var networkChanged = 0;
        NetworkAddressChangedEventHandler onNetworkChange = (_, _) => Interlocked.Exchange(ref networkChanged, 1);
        NetworkChange.NetworkAddressChanged += onNetworkChange;

        async Task ReleaseAsync(PacketPump current)
        {
            await current.DisposeAsync();
            if (current.CleanupError is { } cleanupError)
                Interlocked.CompareExchange(ref releaseError, "Freigabe der Downloadbegrenzung nicht bestätigt: " + cleanupError, null);
        }

        async Task StatusAsync(bool terminating = false)
        {
            // The final release acknowledgement must survive watchdog cancellation.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token,
                terminating ? CancellationToken.None : stop.Token);
            await writeLock.WaitAsync(linked.Token);
            try
            {
                var current = Volatile.Read(ref session);
                var ownershipReady = ownership.Snapshot.IsFresh;
                var top = accounting.Snapshot();
                var cleanupError = Volatile.Read(ref releaseError) ?? (current?.CleanupError is { } cleanup
                    ? "Freigabe der Downloadbegrenzung nicht bestätigt: " + cleanup : null);
                var status = new
                {
                    active = current?.Active == true && current.HasDownloadLimit && ownershipReady,
                    mbps = current?.Active == true && current.HasDownloadLimit && ownershipReady ? rate : null,
                    qosActive = current?.Active == true && current.QosEnabled && ownershipReady,
                    uploadActive = current?.Active == true && current.HasUploadLimit && ownershipReady,
                    uploadMbps = current?.Active == true && current.HasUploadLimit && ownershipReady ? uploadRate : null,
                    uploadSentBytes = current?.UploadSentBytes ?? 0,
                    topPrograms = current?.Active == true ? top.Select(p => new { name = p.Name, downMbps = p.DownMbps, upMbps = p.UpMbps }).ToArray() : [],
                    gamePriorityPackets = current?.GamePriorityPackets ?? 0,
                    voicePriorityPackets = current?.VoicePriorityPackets ?? 0,
                    error = cleanupError ?? current?.Failure?.Message ?? error ?? (current?.Active == true && !ownershipReady
                        ? "Programmzuordnung nicht verfügbar; Downloads werden freigegeben. " + ownership.LastError : null),
                    releaseError = cleanupError,
                    commandId = Interlocked.Read(ref acknowledgedCommandId),
                    captureReleased = current is null && cleanupError is null,
                    stopped = terminating,
                    receivedBytes = current?.ReceivedBytes ?? 0,
                    sentBytes = current?.SentBytes ?? 0,
                    bypassedBytes = current?.BypassedBytes ?? 0,
                    droppedPackets = current?.DroppedPackets ?? 0
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(status).AsMemory(), linked.Token);
            }
            finally { writeLock.Release(); }
        }

        // This watchdog never waits for IPC or the command loop to release a lock.
        var watchdog = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(250, stop.Token);
                    if (parent.HasExited || Stopwatch.GetElapsedTime(Interlocked.Read(ref lastHeartbeat)) > ControlProtocol.HeartbeatTimeout)
                        break;
                    if (Volatile.Read(ref networkChanged) != 0)
                    {
                        error = "Netzwerk geändert. Schutz erneut einschalten, um LAN-Ausnahmen zu aktualisieren.";
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                stop.Cancel();
                var current = Volatile.Read(ref session);
                if (current is not null) await ReleaseAsync(current);
            }
        });

        try
        {
            await StatusAsync();
            while (!stop.IsCancellationRequested)
            {
                var line = await ControlProtocol.ReadLineAsync(reader, stop.Token);
                if (line is null) break;
                var command = ControlProtocol.ParseCommand(line);
                var requested = command.Mbps;
                var requestedQos = command.QosEnabled;
                using var commandJson = JsonDocument.Parse(line);
                var commandId = commandJson.RootElement.TryGetProperty("commandId", out var commandValue)
                    ? commandValue.GetInt64() : 0;
                if (commandId < 0) throw new FormatException("Ungültige Befehlsbestätigung.");
                var shutdown = commandJson.RootElement.TryGetProperty("shutdown", out var shutdownValue) && shutdownValue.GetBoolean();
                var requestedUpload = command.UploadMbps;
                if (shutdown && (requested is not null || requestedQos || requestedUpload is not null))
                    throw new FormatException("Abschaltung erfordert mbps:null, uploadMbps:null und qosEnabled:false.");
                // Upload pacing and QoS marking both need outbound capture; download-only does not.
                var wantsOutbound = requestedQos || requestedUpload.HasValue;
                var wantsActive = requested.HasValue || requestedQos || requestedUpload.HasValue;
                bulkPolicy.Protect(command.ProtectedProcesses);
                qosPolicy.Protect(command.ProtectedProcesses);
                Interlocked.Exchange(ref lastHeartbeat, Stopwatch.GetTimestamp());
                var current = Volatile.Read(ref session);
                if (current?.Active == true && requestedQos == qosEnabled && wantsOutbound == captureOutbound && wantsActive)
                {
                    // Keep native mode/events/queues intact across adaptive rate
                    // changes and cap <-> QoS-only transitions.
                    if (requested != rate) current.UpdateLimit(requested);
                    if (requestedUpload != uploadRate) current.UpdateUploadLimit(requestedUpload);
                    rate = requested;
                    uploadRate = requestedUpload;
                }
                else if (requested != rate || requestedUpload != uploadRate || requestedQos != qosEnabled || wantsOutbound != captureOutbound || !wantsActive)
                {
                    var old = Interlocked.Exchange(ref session, null);
                    if (old is not null) await ReleaseAsync(old);
                    stop.Token.ThrowIfCancellationRequested();
                    rate = requested;
                    uploadRate = requestedUpload;
                    qosEnabled = requestedQos;
                    captureOutbound = wantsOutbound;
                    error = null;
                    if (wantsActive && Volatile.Read(ref releaseError) is null)
                    {
                        try
                        {
                            var device = createDevice?.Invoke() ?? new NdisPacketDevice(wantsOutbound);
                            try
                            {
                                stop.Token.ThrowIfCancellationRequested();
                                var created = new PacketPump(device, requested, bulkPolicy.ShouldLimit, requestedQos ? qosPolicy.Classify : null,
                                    requestedUpload, bulkPolicy.ShouldLimitUpload);
                                Volatile.Write(ref session, created);
                                if (stop.IsCancellationRequested)
                                {
                                    await ReleaseAsync(created);
                                    stop.Token.ThrowIfCancellationRequested();
                                }
                            }
                            catch
                            {
                                device.Dispose();
                                if (device.CleanupError is { } cleanupError)
                                    throw new PacketReleaseException("Freigabe nach fehlgeschlagener Einrichtung nicht bestätigt: " + cleanupError);
                                throw;
                            }
                        }
                        catch (Exception e)
                        {
                            error = e.Message;
                            if (e is PacketReleaseException) Interlocked.CompareExchange(ref releaseError, e.Message, null);
                        }
                    }
                }
                // A failed session remains disabled until {mbps:null}, then re-arm.
                Interlocked.Exchange(ref acknowledgedCommandId, commandId);
                await StatusAsync();
                if (shutdown) break;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            NetworkChange.NetworkAddressChanged -= onNetworkChange;
            stop.Cancel();
            var current = Interlocked.Exchange(ref session, null);
            if (current is not null) await ReleaseAsync(current);
            await watchdog;
            await ownershipWorker;
            rate = null;
            uploadRate = null;
            try { await StatusAsync(terminating: true); }
            catch (Exception reportError) when (reportError is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        return Volatile.Read(ref releaseError) is null ? 0 : 1;
    }
}
