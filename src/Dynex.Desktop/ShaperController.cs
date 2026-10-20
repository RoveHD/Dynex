using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Dynex.Desktop.Games;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dynex.Desktop;

/// <summary>Busiest program on this PC as counted (read-only) by the helper.</summary>
public sealed record ProgramTraffic(string Name, double DownMbps, double UpMbps);
public sealed record GameTrafficIdentity(int ProcessId, long StartedAtUtcTicks);

internal sealed class ShaperController : IAsyncDisposable
{
    private const int MaximumStatusLineLength = 32768;
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    private NamedPipeServerStream? pipe;
    private StreamWriter? writer;
    private Process? process;
    private CancellationTokenSource? cancellation;
    private Task? reader;
    private readonly object sync = new();
    private readonly SemaphoreSlim writeGate = new(1);
    private readonly SemaphoreSlim stopGate = new(1);
    private int stopping;
    private bool captureRequested, shutdownConfirmed;
    private bool observationRequested;
    private bool startAttempted, captureReleased;
    private long nextCommandId, pendingReleaseId;
    private string? releaseFailure;
    private string? terminationReason;
    private bool connectionEnded, recoveryAvailable;
    private int recoveryAttempts;
    private long pendingCommandId;
    private TaskCompletionSource<bool>? pendingAcknowledgement;
    private ShaperStatus status = new(false, null, "Paketpriorisierung ausgeschaltet");
    private long lastStatus;
    private Dictionary<GameTrafficIdentity, long> activeGameExpirations = [];
    public bool IsAvailable => File.Exists(Path.Combine(AppContext.BaseDirectory, "shaper", "Dynex.Shaper.exe"));
    public bool CaptureReleased { get { lock (sync) return releaseFailure is null && (!captureRequested || captureReleased); } }
    internal bool StartAttempted { get { lock (sync) return startAttempted; } }
    public ShaperStatus Status
    {
        get
        {
            lock (sync)
            {
                if ((status.Active || status.QosActive || status.ObservingGameTraffic || status.ApplicationRulesActive || observationRequested ||
                    status.LimitedDownloadMbps.HasValue) &&
                    Stopwatch.GetElapsedTime(lastStatus).TotalSeconds > 6)
                    return new(false, null, "Netzwerkpriorisierung antwortet nicht mehr");
                if (status.ActiveGameProcesses is null) return status;
                var now = Stopwatch.GetTimestamp();
                return status with { ActiveGameProcesses = activeGameExpirations.Where(pair => pair.Value > now).Select(pair => pair.Key).ToArray() };
            }
        }
    }
    public async Task StartAsync()
    {
        lock (sync) if (releaseFailure is not null) { Set(new(false, null, releaseFailure)); return; }
        // One attempt per explicit enable/app lifecycle. A refused UAC prompt or
        // failed helper must not be retried by every monitoring heartbeat.
        lock (sync) { if (startAttempted) return; startAttempted = true; terminationReason = null; }
        if (pipe != null) return;
        if (process is not null)
        {
            if (!process.HasExited)
            { Set(new(false, null, "Die vorherige Paketpriorisierung wird noch beendet.")); return; }
            process.Dispose(); process = null;
        }
        var helperPath = Path.Combine(AppContext.BaseDirectory, "shaper", "Dynex.Shaper.exe");
        if (!File.Exists(helperPath)) { Set(new(false, null, "Eine Programmkomponente fehlt. DYNEX bitte neu installieren.")); return; }
        if (!IsAvailable)
        { Set(new(false, null, "Paketpriorisierung nicht verfügbar: Die benötigte Komponente fehlt oder wurde von Windows blockiert.")); return; }
        if (!PacketFilterSetup.IsInstalled)
        { Set(new(false, null, "Paketpriorisierung bitte einmal einrichten.")); return; }
        var pipeName = "DYNEX-" + Guid.NewGuid().ToString("N");
        lock (sync)
        {
            captureRequested = false; shutdownConfirmed = false; captureReleased = false; pendingReleaseId = 0;
            connectionEnded = false; terminationReason = null; recoveryAvailable = false;
        }
        Volatile.Write(ref stopping, 0);
        pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        cancellation = new CancellationTokenSource();
        try
        {
            using var current = Process.GetCurrentProcess();
            process = Process.Start(new ProcessStartInfo(helperPath)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"--pipe {pipeName} --parent {Environment.ProcessId} --parent-start {current.StartTime.ToUniversalTime().Ticks}"
            });
            if (process == null) throw new IOException("Helfer konnte nicht gestartet werden.");
            await pipe.WaitForConnectionAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(30));
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) || clientPid != process.Id)
                throw new UnauthorizedAccessException("Die Helferverbindung stammt nicht vom gestarteten DYNEX-Modul.");
            writer = new(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            Set(new(false, null, "Paketpriorisierung bereit."));
            reader = ReadStatusAsync(pipe, cancellation.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await StopAsync(resetStartAttempt: false);
            Set(new(false, null, "Windows konnte die Paketpriorisierung nicht starten: " + ex.Message));
        }
    }
    public async Task ApplyAsync(double? mbps, CancellationToken token, IReadOnlyList<GameDetectionResult>? games = null, bool qosEnabled = false,
        Func<bool>? protectionStillNeeded = null, double? uploadMbps = null, bool observeGameTraffic = false,
        Func<bool>? observationStillNeeded = null, IReadOnlyList<ApplicationRule>? appRules = null,
        Func<bool>? applicationRulesStillNeeded = null)
    {
        // Defense at the IPC boundary: legacy callers, profiles and imports cannot
        // request bandwidth shaping. Only classification and observation remain.
        mbps = null;
        uploadMbps = null;
        ApplicationRule[] rules = [];
        // A helper can deliberately fail open (watchdog/network change). Dispose
        // that connection first, then allow one fresh authenticated start only
        // after both native release and process exit have been confirmed.
        bool ended;
        lock (sync) ended = connectionEnded;
        if (ended && writer is not null) await StopAsync(resetStartAttempt: false);
        var wantsCapture = mbps.HasValue || qosEnabled || uploadMbps.HasValue || observeGameTraffic || rules.Length != 0;
        bool canRecover;
        lock (sync) canRecover = wantsCapture && recoveryAvailable && recoveryAttempts == 0 &&
            releaseFailure is null && shutdownConfirmed && process is null;
        var recoveryStillNeeded = canRecover &&
            (((mbps.HasValue || qosEnabled || uploadMbps.HasValue) && protectionStillNeeded?.Invoke() != false) ||
             (observeGameTraffic && observationStillNeeded?.Invoke() != false) ||
             (rules.Length != 0 && applicationRulesStillNeeded?.Invoke() != false));
        lock (sync)
        {
            if (wantsCapture && recoveryStillNeeded && recoveryAvailable && recoveryAttempts == 0 && releaseFailure is null &&
                shutdownConfirmed && process is null)
            {
                recoveryAttempts++;
                recoveryAvailable = false;
                startAttempted = false;
            }
        }
        if (wantsCapture && !StartAttempted) await StartAsync();
        // UAC/connection setup can take seconds. Recheck live foreground/peer
        // state before activating capture instead of using the pre-prompt state.
        if ((mbps.HasValue || qosEnabled || uploadMbps.HasValue) && protectionStillNeeded?.Invoke() == false)
        { mbps = null; qosEnabled = false; uploadMbps = null; }
        if (observeGameTraffic && observationStillNeeded?.Invoke() == false) observeGameTraffic = false;
        if (rules.Length != 0 && applicationRulesStillNeeded?.Invoke() == false) rules = [];
        if (writer == null || Volatile.Read(ref stopping) != 0) return;
        var protectedProcesses = (games ?? []).Where(g => g.IsRunning && g.ProcessId > 0).Take(128)
            .Select(g => new { processId = g.ProcessId, startedAtUtcTicks = g.ProcessStartedAt?.UtcTicks }).ToArray();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await writeGate.WaitAsync(timeout.Token);
            try
            {
                if (writer == null || Volatile.Read(ref stopping) != 0) return;
                long commandId;
                TaskCompletionSource<bool> acknowledgement;
                lock (sync)
                {
                    commandId = ++nextCommandId;
                    observationRequested = observeGameTraffic;
                    if (mbps.HasValue || qosEnabled || uploadMbps.HasValue || observeGameTraffic || rules.Length != 0)
                    { captureRequested = true; captureReleased = false; pendingReleaseId = 0; }
                    else if (captureRequested && !captureReleased)
                    {
                        // Reuse the pending release id until acknowledged. Later idle
                        // heartbeats keep the passive IPC helper alive without capture.
                        if (pendingReleaseId == 0) pendingReleaseId = commandId;
                        else commandId = pendingReleaseId;
                    }
                    pendingCommandId = commandId;
                    acknowledgement = pendingAcknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { mbps, uploadMbps, protectedProcesses, qosEnabled,
                    observeGameTraffic, applicationRules = rules.Select(r => new { processName = r.ProcessName,
                        downloadMbps = r.DownloadMbps, uploadMbps = r.UploadMbps }), commandId }).AsMemory(), timeout.Token);
                if (!await acknowledgement.Task.WaitAsync(timeout.Token))
                    throw new IOException("Der Priorisierungs-Helfer wurde vor der Befehlsbestätigung beendet.");
            }
            finally
            {
                lock (sync) { pendingAcknowledgement = null; pendingCommandId = 0; }
                writeGate.Release();
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { await StopAsync(resetStartAttempt: false); Set(new(false, null, "Paketpriorisierung wurde wegen einer Zeitüberschreitung beendet.")); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException)
        { await StopAsync(resetStartAttempt: false); Set(new(false, null, "Paketpriorisierung wurde beendet: " + ex.Message)); }
    }
    private async Task ReadStatusAsync(Stream stream, CancellationToken token)
    {
        using var input = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
        var lineBuffer = new char[MaximumStatusLineLength + 1];
        try
        {
            while (await ReadStatusLineAsync(input, lineBuffer, token) is string line)
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var active = root.TryGetProperty("active", out var activeValue) && activeValue.GetBoolean();
                var qosActive = root.TryGetProperty("qosActive", out var qosValue) && qosValue.GetBoolean();
                var appActive = root.TryGetProperty("applicationRulesActive", out var appValue) && appValue.GetBoolean();
                var observationSupported = root.TryGetProperty("observingGameTraffic", out var observingValue);
                var observing = observationSupported && observingValue.GetBoolean();
                var expirations = ReadActiveGames(root, observing);
                var gamePackets = ReadCounter(root, "gamePriorityPackets");
                var voicePackets = ReadCounter(root, "voicePriorityPackets");
                double? limitedDownloadMbps = null;
                if (root.TryGetProperty("limitedDownloadMbps", out var limitedDownload) && limitedDownload.ValueKind != JsonValueKind.Null)
                {
                    if (limitedDownload.ValueKind != JsonValueKind.Number || !limitedDownload.TryGetDouble(out var measured) ||
                        !double.IsFinite(measured) || measured is < 0 or > 100000)
                        throw new JsonException("Ungültige Downloadaktivität.");
                    limitedDownloadMbps = measured;
                }
                var limit = root.TryGetProperty("mbps", out var rate) && rate.ValueKind == JsonValueKind.Number ? rate.GetDouble() : (double?)null;
                var uploadActive = root.TryGetProperty("uploadActive", out var uploadValue) && uploadValue.GetBoolean();
                var uploadLimit = uploadActive && root.TryGetProperty("uploadMbps", out var uploadRate) && uploadRate.ValueKind == JsonValueKind.Number
                    ? uploadRate.GetDouble() : (double?)null;
                var error = root.TryGetProperty("error", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() : null;
                var programs = new List<ProgramTraffic>();
                if (root.TryGetProperty("topPrograms", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var item in list.EnumerateArray().Take(5))
                        if (item.TryGetProperty("name", out var programName) && programName.GetString() is { Length: > 0 and <= 64 } name && !name.Any(char.IsControl))
                            programs.Add(new(name, item.GetProperty("downMbps").GetDouble(), item.GetProperty("upMbps").GetDouble()));
                var next = new ShaperStatus(false, null, error ?? (active || appActive || uploadActive || limit is not null || uploadLimit is not null
                    ? "Der Helfer meldet veraltete Bandbreitenregeln. DYNEX bitte vollständig neu starten."
                    : qosActive ? "Priorisierung aktiv · Spiel- und Sprachpakete werden markiert."
                    : observing ? "Spielverkehr wird beobachtet."
                    : "Paketpriorisierung bereit."),
                    qosActive, gamePackets, voicePackets, null, programs, observing,
                    observing ? expirations.Keys.ToArray() : null, false, limitedDownloadMbps);
                lock (sync)
                {
                    if (root.TryGetProperty("releaseError", out var cleanup) && cleanup.ValueKind == JsonValueKind.String)
                        releaseFailure ??= cleanup.GetString();
                    if (pendingReleaseId != 0 && !active && !qosActive && !uploadActive && !observing && !appActive && limit is null &&
                        root.TryGetProperty("commandId", out var acknowledged) && acknowledged.TryGetInt64(out var acknowledgedId) &&
                        acknowledgedId == pendingReleaseId &&
                        root.TryGetProperty("captureReleased", out var capture) && capture.ValueKind == JsonValueKind.True &&
                        root.TryGetProperty("releaseError", out var release) && release.ValueKind == JsonValueKind.Null)
                    { captureReleased = true; pendingReleaseId = 0; }
                    // An old inactive status is not an acknowledgement of this
                    // shutdown. Require the helper's explicit final frame.
                    var terminating = root.TryGetProperty("stopped", out var stopped) && stopped.ValueKind == JsonValueKind.True;
                    if (!active && !qosActive && !uploadActive && !observing && !appActive && limit is null && terminating &&
                        root.TryGetProperty("releaseError", out var released) && released.ValueKind == JsonValueKind.Null)
                    { shutdownConfirmed = true; captureReleased = true; }
                    if (terminating)
                    {
                        connectionEnded = true;
                        observationRequested = false;
                        if (error is not null) terminationReason ??= error;
                        if (Volatile.Read(ref stopping) == 0) recoveryAvailable = shutdownConfirmed && releaseFailure is null;
                        pendingAcknowledgement?.TrySetResult(false);
                    }
                    else if (root.TryGetProperty("commandId", out var applied) && applied.TryGetInt64(out var appliedId) && appliedId == pendingCommandId)
                        pendingAcknowledgement?.TrySetResult(true);
                    // Publish the new status together with the release flags. Otherwise a snapshot taken
                    // in between sees "capture released" next to the previous "QoS active" status.
                    if (observationRequested && !observationSupported && error is null)
                        next = next with { Message = "Spielverkehr-Beobachtung wird vom Helfer nicht unterstützt. DYNEX bitte aktualisieren." };
                    status = releaseFailure is not null ? new(false, null, releaseFailure)
                        : terminationReason is not null ? new(false, null, terminationReason) : next;
                    activeGameExpirations = releaseFailure is null ? expirations : [];
                    lastStatus = Stopwatch.GetTimestamp();
                }
            }
            lock (sync)
            {
                connectionEnded = true;
                observationRequested = false;
                pendingAcknowledgement?.TrySetResult(false);
                if (captureRequested && !shutdownConfirmed)
                    releaseFailure ??= "Freigabe der Paketpriorisierung nicht bestätigt: Helferverbindung wurde ohne Abschaltbestätigung beendet.";
            }
            Set(new(false, null, "Paketpriorisierung beendet"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ObjectDisposedException)
        {
            lock (sync)
            {
                connectionEnded = true;
                observationRequested = false;
                terminationReason ??= "Helferstatus ungültig: " + ex.Message;
                pendingAcknowledgement?.TrySetResult(false);
            }
            Set(new(false, null, terminationReason));
            cancellation?.Cancel(); pipe?.Dispose();
        }
    }
    private static async Task<string?> ReadStatusLineAsync(StreamReader input, char[] buffer, CancellationToken token)
    {
        var count = 0;
        while (await input.ReadAsync(buffer.AsMemory(count, 1), token) != 0)
        {
            if (buffer[count] == '\n') return new string(buffer, 0, count).TrimEnd('\r');
            if (++count > MaximumStatusLineLength) throw new IOException("Ungültige Helferantwort: Größenlimit überschritten.");
        }
        if (count != 0) throw new EndOfStreamException("Unvollständige Helferantwort.");
        return null;
    }
    private static long ReadCounter(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return 0;
        if (!value.TryGetInt64(out var count) || count < 0) throw new JsonException("Ungültiger Priorisierungszähler.");
        return count;
    }
    private static Dictionary<GameTrafficIdentity, long> ReadActiveGames(JsonElement root, bool observing)
    {
        var result = new Dictionary<GameTrafficIdentity, long>();
        if (!observing) return result;
        if (!root.TryGetProperty("activeGameProcesses", out var games) || games.ValueKind != JsonValueKind.Array || games.GetArrayLength() > 128)
            throw new JsonException("Ungültige Spielverkehr-Identitäten.");
        var now = Stopwatch.GetTimestamp();
        foreach (var game in games.EnumerateArray())
        {
            if (!game.TryGetProperty("processId", out var pid) || !pid.TryGetInt32(out var processId) || processId <= 0 ||
                !game.TryGetProperty("startedAtUtcTicks", out var start) || !start.TryGetInt64(out var ticks) || ticks <= 0 || ticks > DateTime.MaxValue.Ticks ||
                !game.TryGetProperty("ageSeconds", out var ageValue) || !ageValue.TryGetDouble(out var age) || !double.IsFinite(age) || age < 0)
                throw new JsonException("Ungültige Spielverkehr-Identität.");
            if (age >= 10) continue;
            var identity = new GameTrafficIdentity(processId, ticks);
            if (!result.TryAdd(identity, now + (long)((10 - age) * Stopwatch.Frequency)))
                throw new JsonException("Doppelte Spielverkehr-Identität.");
        }
        return result;
    }
    private void Set(ShaperStatus value)
    {
        lock (sync)
        {
            status = releaseFailure is not null ? new(false, null, releaseFailure)
                : terminationReason is not null ? new(false, null, terminationReason) : value;
            activeGameExpirations.Clear();
            lastStatus = Stopwatch.GetTimestamp();
        }
    }
    public async Task StopAsync(bool requireExit = false, bool resetStartAttempt = true)
    {
        await stopGate.WaitAsync();
        try
        {
            Volatile.Write(ref stopping, 1);
            // Keep the authenticated read side alive until release and the final
            // acknowledgement. Closing it first would hide native cleanup errors.
            if (writer is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await writeGate.WaitAsync(timeout.Token);
                    try { await writer.WriteLineAsync("{\"mbps\":null,\"shutdown\":true}".AsMemory(), timeout.Token); }
                    finally { writeGate.Release(); }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
                { /* Keep reading: the final release frame may already be buffered. */ }
            }
            var exited = true;
            if (process != null)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(9)); }
                catch (TimeoutException) { exited = false; }
                catch (InvalidOperationException) { }
            }
            if (reader != null)
            {
                try { await reader.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException) { }
            }
            cancellation?.Cancel();
            pipe?.Dispose();
            if (reader != null) { try { await reader; } catch (OperationCanceledException) { } }
            try { writer?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            writer = null; pipe = null; reader = null;
            cancellation?.Dispose(); cancellation = null;
            if (exited) { process?.Dispose(); process = null; }
            lock (sync)
            {
                if (!exited || captureRequested && !shutdownConfirmed)
                    releaseFailure ??= "Freigabe der Paketpriorisierung nicht bestätigt: Der Helfer hat keine erfolgreiche Abschaltung bestätigt.";
                if (!resetStartAttempt && exited && shutdownConfirmed && releaseFailure is null)
                    recoveryAvailable = true;
                Set(new(false, null, "Paketpriorisierung ausgeschaltet"));
                if (shutdownConfirmed) captureReleased = true;
                observationRequested = false;
                if (resetStartAttempt)
                {
                    startAttempted = false; recoveryAttempts = 0; recoveryAvailable = false;
                }
                if (requireExit && releaseFailure is not null) throw new InvalidOperationException(releaseFailure);
            }
        }
        finally { stopGate.Release(); }
    }
    public async ValueTask DisposeAsync() => await StopAsync();
    internal sealed record ShaperStatus(bool Active, double? Mbps, string Message,
        bool QosActive = false, long GamePriorityPackets = 0, long VoicePriorityPackets = 0, double? UploadMbps = null,
        IReadOnlyList<ProgramTraffic>? TopPrograms = null, bool ObservingGameTraffic = false,
        IReadOnlyList<GameTrafficIdentity>? ActiveGameProcesses = null, bool ApplicationRulesActive = false,
        double? LimitedDownloadMbps = null);
}
