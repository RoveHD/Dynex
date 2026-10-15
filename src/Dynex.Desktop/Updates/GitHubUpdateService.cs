using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dynex.Desktop.Updates;

public sealed record UpdateStatus(string Message, bool IsBusy = false,
    bool UpdateReady = false, string? AvailableVersion = null,
    long? DownloadedBytes = null, long? TotalBytes = null);

/// <summary>Downloads releases only. The application owns the clean startup/install handoff.</summary>
public sealed class GitHubUpdateService : IDisposable
{
    private const string Repository = "RoveHD/Dynex";
    private const long MaximumInstallerBytes = 512L * 1024 * 1024;
    private static readonly Regex ReleaseTag = new(@"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256 = new("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant);
    private readonly object sync = new();
    private readonly string directory;
    private readonly Version currentVersion;
    private readonly Func<bool> speedTestPendingOrActive;
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly TimeSpan checkInterval;
    private readonly TimeSpan deferredInterval;
    private readonly int bytesPerSecond;
    private readonly TimeSpan downloadStallTimeout;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? enabledLifetime;
    private Task operation = Task.CompletedTask;
    private Task loop = Task.CompletedTask;
    private readonly List<Task> tasks = [];
    private long operationEpoch = -1;
    private long backgroundEpoch = -1;
    private bool enabled;
    private bool beta;
    private bool disposed;
    private bool deferred;
    private long generation;
    private ReadyUpdate? verifiedInstaller;
    private long verifiedInstallerEpoch = -1;
    private UpdateStatus status = new("Automatische Updates sind ausgeschaltet.");
    private readonly Func<string, SignatureCheck> inspectSignature;
    private readonly Lazy<SignatureCheck> currentSignature;
    /// <summary>Written by the installer after it restored the previous version.</summary>
    internal const string RolledBackFile = "rolled-back.txt";

    public GitHubUpdateService(string cacheDirectory, Version currentVersion, Func<bool> speedTestPendingOrActive)
        : this(cacheDirectory, currentVersion, speedTestPendingOrActive, null, TimeSpan.FromHours(6), TimeSpan.FromSeconds(30), 512 * 1024) { }

    internal GitHubUpdateService(string cacheDirectory, Version currentVersion, Func<bool> speedTestPendingOrActive,
        HttpClient? httpClient, TimeSpan checkInterval, TimeSpan deferredInterval, int bytesPerSecond,
        Func<string, SignatureCheck>? inspectSignature = null, SignatureCheck? currentSignature = null,
        TimeSpan? downloadStallTimeout = null)
    {
        this.inspectSignature = inspectSignature ?? AuthenticodeVerifier.Inspect;
        this.currentSignature = new(() => currentSignature ??
            (Environment.ProcessPath is { } self ? this.inspectSignature(self) : SignatureCheck.Unsigned));
        directory = Path.GetFullPath(cacheDirectory);
        this.currentVersion = Normalize(currentVersion);
        this.speedTestPendingOrActive = speedTestPendingOrActive;
        this.checkInterval = checkInterval;
        this.deferredInterval = deferredInterval;
        this.bytesPerSecond = bytesPerSecond > 0 ? bytesPerSecond : throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        this.downloadStallTimeout = downloadStallTimeout ?? TimeSpan.FromSeconds(30);
        if (this.downloadStallTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(downloadStallTimeout));
        ownsClient = httpClient is null;
        // Inspect every redirect ourselves before sending the next request.
        client = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        enabledLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
    }

    public event Action? Changed;
    public UpdateStatus Status { get { lock (sync) return status; } }

    /// <summary>A newer version whose installation failed and was rolled back; it is not downloaded again automatically.</summary>
    public string? RolledBackVersion
    {
        get
        {
            try
            {
                var path = Path.Combine(directory, RolledBackFile);
                if (!File.Exists(path) || new FileInfo(path).Length > 64) return null;
                var text = File.ReadAllText(path).Trim();
                return TryVersion("v" + text, out var version) && version > currentVersion ? version.ToString(3) : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    public void SetEnabled(bool value, bool startBackground = true)
    {
        var changed = false;
        lock (sync)
        {
            if (disposed) return;
            if (enabled != value)
            {
                changed = true;
                enabled = value;
                RenewGenerationLocked();
                // Store this before starting a worker; an asynchronous "checking" or progress
                // update must not be overwritten by the caller's later enabled notification.
                status = new(value ? "Automatische Updates sind eingeschaltet." : "Automatische Updates sind ausgeschaltet.");
            }
            if (value && startBackground && backgroundEpoch != generation)
            {
                var token = enabledLifetime!.Token;
                var epoch = generation;
                backgroundEpoch = epoch;
                loop = Task.Run(() => BackgroundAsync(epoch, token));
                tasks.Add(loop);
            }
        }
        if (changed) Notify();
    }

    /// <summary>Opt-in: also consider GitHub releases marked as pre-release (same vX.Y.Z tags).</summary>
    public void SetBetaChannel(bool value)
    {
        lock (sync)
        {
            if (disposed || beta == value) return;
            var restartBackground = enabled && backgroundEpoch == generation;
            beta = value;
            RenewGenerationLocked();
            status = new(enabled ? "Update-Kanal geändert. GitHub wird erneut geprüft." : "Automatische Updates sind ausgeschaltet.");
            if (restartBackground)
            {
                var token = enabledLifetime!.Token;
                var epoch = generation;
                backgroundEpoch = epoch;
                loop = Task.Run(() => BackgroundAsync(epoch, token));
                tasks.Add(loop);
            }
        }
        Notify();
    }

    private void RenewGenerationLocked()
    {
        generation++;
        deferred = false;
        var previousLifetime = enabledLifetime!;
        previousLifetime.Cancel();
        _ = Task.WhenAll(tasks.ToArray()).ContinueWith(_ => previousLifetime.Dispose(), TaskScheduler.Default);
        enabledLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
    }

    public Task CheckNowAsync() => CheckCoreAsync(background: false);

    private long operationStartedTick;

    private Task CheckCoreAsync(bool background)
    {
        lock (sync)
        {
            if (disposed) return Task.CompletedTask;
            if (!operation.IsCompleted && operationEpoch == generation) return operation;
            // The background loop's first check right after a manual one would only repeat the same
            // GitHub request (and briefly show "checking" again).
            if (background && operationEpoch == generation && operationStartedTick != 0 &&
                Stopwatch.GetElapsedTime(operationStartedTick) < TimeSpan.FromMinutes(1)) return operation;
            operationStartedTick = Stopwatch.GetTimestamp();
            var epoch = generation;
            var download = enabled;
            var token = enabledLifetime!.Token;
            operationEpoch = epoch;
            operation = Task.Run(() => CheckAsync(epoch, download, token));
            tasks.RemoveAll(task => task.IsCompleted);
            tasks.Add(operation);
            return operation;
        }
    }

    private async Task BackgroundAsync(long epoch, CancellationToken token)
    {
        var first = true;
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Only the loop's first check may reuse a manual check that just ran.
                await CheckCoreAsync(background: first).ConfigureAwait(false);
                first = false;
                token.ThrowIfCancellationRequested();
                TimeSpan delay;
                lock (sync) delay = deferred ? deferredInterval : checkInterval;
                await Task.Delay(delay, token).ConfigureAwait(false);
                // Poll only local speed-test state while deferred. Repeated API checks would exhaust
                // GitHub's anonymous per-IP quota while a speed test is pending.
                while (IsDeferred() && speedTestPendingOrActive())
                    await Task.Delay(deferredInterval, token).ConfigureAwait(false);
                lock (sync) if (generation != epoch) return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task CheckAsync(long epoch, bool download, CancellationToken token)
    {
        string? partial = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        var cancellation = timeout.Token;
        try
        {
            lock (sync)
            {
                if (generation != epoch || disposed) return;
                deferred = false;
            }
            PublishFor(epoch, new("GitHub wird auf Updates geprüft …", true));
            bool useBeta;
            lock (sync) useBeta = beta;
            using var response = await GetAsync(new(useBeta
                ? $"https://api.github.com/repos/{Repository}/releases?per_page=10"
                : $"https://api.github.com/repos/{Repository}/releases/latest"), true, cancellation).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                PublishFor(epoch, new("Noch kein Update auf GitHub verfügbar."));
                return;
            }
            response.EnsureSuccessStatusCode();
            var json = await ReadBoundedAsync(response.Content, 4 * 1024 * 1024, cancellation).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var release = useBeta ? ReadNewest(document.RootElement) : ReadRelease(document.RootElement);
            if (release is null || release.Version <= currentVersion)
            {
                PublishFor(epoch, new("DYNEX ist auf dem neuesten Stand."));
                return;
            }
            if (!download)
            {
                PublishFor(epoch, new($"Version {release.Version} verfügbar. Automatische Updates zum Herunterladen einschalten.", AvailableVersion: release.Version.ToString(3)));
                return;
            }
            if (RolledBackVersion == release.Version.ToString(3))
            {
                PublishFor(epoch, new($"Version {release.Version} ließ sich nicht installieren; DYNEX hat die vorherige Version wiederhergestellt. Eine manuelle Installation über GitHub ist möglich.",
                    AvailableVersion: release.Version.ToString(3)));
                return;
            }
            if (speedTestPendingOrActive()) { Defer(epoch, release.Version); return; }
            Directory.CreateDirectory(directory);
            var cached = await ReadVerifiedReadyAsync(cancellation).ConfigureAwait(false);
            if (cached is not null && cached.Version == release.Version.ToString(3) && cached.Size == release.Size &&
                (release.Hash is null || string.Equals(cached.Hash, release.Hash, StringComparison.OrdinalIgnoreCase)))
            {
                // Older versions did not persist the release channel. An authenticated GitHub
                // response must establish it before the cached installer can be handed off.
                lock (sync)
                {
                    if (disposed || !enabled || generation != epoch) return;
                    if (cached.Prerelease != release.Prerelease)
                        WriteAtomic(Path.Combine(directory, "ready.json"), JsonSerializer.Serialize(cached with { Prerelease = release.Prerelease }));
                }
                PublishFor(epoch, Ready(release.Version));
                return;
            }
            var hash = release.Hash;
            if (hash is null)
            {
                using var hashResponse = await GetAsync(release.HashUri!, false, cancellation).ConfigureAwait(false);
                hashResponse.EnsureSuccessStatusCode();
                var hashText = Encoding.UTF8.GetString(await ReadBoundedAsync(hashResponse.Content, 4096, cancellation).ConfigureAwait(false)).Trim();
                var fields = hashText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length is < 1 or > 2 || !Sha256.IsMatch(fields[0]) ||
                    (fields.Length == 2 && fields[1].TrimStart('*') != release.FileName))
                    throw new InvalidDataException("Ungültige SHA-256-Prüfsumme.");
                hash = fields[0].ToUpperInvariant();
            }
            if (speedTestPendingOrActive()) { Defer(epoch, release.Version); return; }
            PublishFor(epoch, new($"Download von Version {release.Version} wird verbunden …", true,
                AvailableVersion: release.Version.ToString(3), DownloadedBytes: 0, TotalBytes: release.Size));
            partial = Path.Combine(directory, release.FileName + "." + Guid.NewGuid().ToString("N") + ".part");
            using (var transfer = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                // A speed test can start while HTTP headers, body bytes or the bandwidth limiter are
                // waiting. Cancel all three instead of checking only between completed reads.
                var measurement = MonitorSpeedTestAsync(transfer);
                try
                {
                    using var installer = await GetAsync(release.InstallerUri, false, transfer.Token).ConfigureAwait(false);
                    installer.EnsureSuccessStatusCode();
                    if (installer.Content.Headers.ContentLength is long contentLength && contentLength != release.Size)
                        throw new InvalidDataException("Die Downloadgröße stimmt nicht mit GitHub überein.");
                    await DownloadAsync(installer.Content, partial, release.Size, hash, transfer.Token,
                        total => PublishProgress(epoch, release.Version, total, release.Size)).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && transfer.IsCancellationRequested)
                {
                    throw new DownloadDeferredException();
                }
                finally
                {
                    transfer.Cancel();
                    await measurement.ConfigureAwait(false);
                }
            }
            cancellation.ThrowIfCancellationRequested();
            PublishFor(epoch, new($"Version {release.Version} vollständig heruntergeladen. Update wird geprüft …", true,
                AvailableVersion: release.Version.ToString(3), DownloadedBytes: release.Size, TotalBytes: release.Size));
            if (SignaturePolicy.Reject(currentSignature.Value, inspectSignature(partial)) is { } rejected)
                throw new UpdateRejectedException(rejected);
            lock (sync)
            {
                if (disposed || !enabled || generation != epoch) return;
                var finalPath = Path.Combine(directory, release.FileName);
                File.Move(partial, finalPath, true);
                partial = null;
                var ready = new ReadyUpdate(release.Version.ToString(3), release.FileName, hash, release.Size, release.Prerelease);
                WriteAtomic(Path.Combine(directory, "ready.json"), JsonSerializer.Serialize(ready));
                deferred = false;
            }
            PublishFor(epoch, Ready(release.Version));
        }
        catch (DownloadDeferredException) { Defer(epoch, null); }
        catch (DownloadStalledException)
        {
            PublishFor(epoch, new("Update-Download unterbrochen: GitHub hat keine weiteren Daten geliefert. Mit „Jetzt prüfen“ erneut starten."));
        }
        catch (UpdateRejectedException error) { PublishFor(epoch, new($"Update verworfen: {error.Message} Es wird nichts installiert.")); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            PublishFor(epoch, new("Update-Anfrage hat zu lange gedauert. Mit „Jetzt prüfen“ erneut starten."));
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or FormatException or InvalidOperationException or OperationCanceledException or OverflowException or UnauthorizedAccessException)
        {
            PublishFor(epoch, new("Update konnte gerade nicht geladen werden. Mit „Jetzt prüfen“ erneut versuchen."));
        }
        finally
        {
            if (partial is not null) TryDelete(partial);
        }
    }

    /// <summary>Validates a ready installer without consuming it. The caller owns the clean shutdown and execution.</summary>
    public async Task<string?> TakeReadyInstallerAsync()
    {
        long epoch;
        bool useBeta;
        CancellationToken token;
        lock (sync)
        {
            if (!enabled || disposed) return null;
            epoch = generation;
            useBeta = beta;
            token = enabledLifetime!.Token;
        }
        try
        {
            var ready = await ReadVerifiedReadyAsync(token).ConfigureAwait(false);
            if (ready is null || ready.Version == RolledBackVersion || ready.Prerelease is null ||
                ready.Prerelease == true && !useBeta) return null;
            // Check the publisher again immediately before execution, not only after download.
            if (SignaturePolicy.Reject(currentSignature.Value, inspectSignature(Path.Combine(directory, ready.FileName))) is { } rejected)
            {
                lock (sync)
                {
                    TryDelete(Path.Combine(directory, "ready.json"));
                    TryDelete(Path.Combine(directory, ready.FileName));
                }
                PublishFor(epoch, new($"Update verworfen: {rejected} Es wird nichts installiert."));
                return null;
            }
            lock (sync)
            {
                if (!enabled || disposed || epoch != generation) return null;
                var current = JsonSerializer.Deserialize<ReadyUpdate>(File.ReadAllText(Path.Combine(directory, "ready.json")));
                if (current != ready) return null;
                verifiedInstaller = ready;
                verifiedInstallerEpoch = epoch;
                return Path.Combine(directory, ready.FileName);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException) { return null; }
    }

    /// <summary>Recheck immediately before Process.Start; a preference change invalidates an earlier verified handoff.</summary>
    public bool CanStartInstaller(string installerPath)
    {
        try
        {
            lock (sync)
            {
                if (!enabled || disposed || verifiedInstallerEpoch != generation || verifiedInstaller is not { } ready ||
                    ready.Prerelease is null || ready.Prerelease == true && !beta || ready.Version == RolledBackVersion ||
                    !string.Equals(Path.GetFullPath(installerPath), Path.Combine(directory, ready.FileName), StringComparison.OrdinalIgnoreCase))
                    return false;
                var metadata = Path.Combine(directory, "ready.json");
                if (!File.Exists(metadata) || new FileInfo(metadata).Length > 4096) return false;
                return JsonSerializer.Deserialize<ReadyUpdate>(File.ReadAllText(metadata)) == ready;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Consume only after Process.Start succeeded. A failed launch must leave the verified cache retryable.</summary>
    public bool ConfirmInstallerStarted(string installerPath)
    {
        try
        {
            lock (sync)
            {
                var metadata = Path.Combine(directory, "ready.json");
                if (!File.Exists(metadata) || new FileInfo(metadata).Length > 4096) return false;
                var ready = JsonSerializer.Deserialize<ReadyUpdate>(File.ReadAllText(metadata));
                if (ready is null || !TryVersion("v" + ready.Version, out var version) || version <= currentVersion ||
                    ready.FileName != $"DYNEX-Setup-{version.ToString(3)}.exe" ||
                    !string.Equals(Path.GetFullPath(installerPath), Path.Combine(directory, ready.FileName), StringComparison.OrdinalIgnoreCase))
                    return false;
                File.Delete(metadata);
                return true;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            // The child installer is already running: bookkeeping failure must never launch it twice.
            return false;
        }
    }

    public void ReportInstallerLaunchFailure(string message)
    {
        lock (sync)
        {
            if (disposed) return;
            status = new($"Update konnte nicht gestartet werden: {message} Mit „Jetzt prüfen“ erneut versuchen.",
                AvailableVersion: status.AvailableVersion);
        }
        Notify();
    }

    private async Task<ReadyUpdate?> ReadVerifiedReadyAsync(CancellationToken token)
    {
        try
        {
            var metadata = Path.Combine(directory, "ready.json");
            if (!File.Exists(metadata) || new FileInfo(metadata).Length > 4096) return null;
            var ready = JsonSerializer.Deserialize<ReadyUpdate>(await File.ReadAllTextAsync(metadata, token).ConfigureAwait(false));
            if (ready is null || !TryVersion("v" + ready.Version, out var version) || version <= currentVersion ||
                ready.FileName != $"DYNEX-Setup-{version.ToString(3)}.exe" || ready.Hash is null || !Sha256.IsMatch(ready.Hash) ||
                ready.Size is <= 0 or > MaximumInstallerBytes) return null;
            var path = Path.Combine(directory, ready.FileName);
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (file.Length != ready.Size) return null;
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(ready.Hash)) ? ready : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or FormatException or ArgumentException) { return null; }
    }

    private async Task DownloadAsync(HttpContent content, string path, long size, string hash, CancellationToken token,
        Action<long> reportProgress)
    {
        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[32 * 1024];
        long total = 0;
        var elapsed = Stopwatch.StartNew();
        var lastProgress = TimeSpan.MinValue;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (speedTestPendingOrActive()) throw new DownloadDeferredException();
            int read;
            using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                readTimeout.CancelAfter(downloadStallTimeout);
                var pendingRead = input.ReadAsync(buffer, readTimeout.Token).AsTask();
                try
                {
                    // WaitAsync also bounds streams that fail to honour their read token.
                    read = await pendingRead.WaitAsync(readTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new DownloadStalledException();
                }
                finally
                {
                    if (!pendingRead.IsCompleted)
                    {
                        readTimeout.Cancel();
                        _ = pendingRead.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            if (speedTestPendingOrActive()) throw new DownloadDeferredException();
            if (read == 0) break;
            total += read;
            if (total > size || total > MaximumInstallerBytes) throw new InvalidDataException("Download zu groß.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            digest.AppendData(buffer, 0, read);
            if (total == size || lastProgress == TimeSpan.MinValue || elapsed.Elapsed - lastProgress >= TimeSpan.FromMilliseconds(250))
            {
                reportProgress(total);
                lastProgress = elapsed.Elapsed;
            }
            var wait = TimeSpan.FromSeconds((double)total / bytesPerSecond) - elapsed.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
        }
        if (total != size || !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(hash)))
            throw new InvalidDataException("Download ist unvollständig oder beschädigt.");
        await output.FlushAsync(token).ConfigureAwait(false);
        output.Flush(true);
    }

    private async Task MonitorSpeedTestAsync(CancellationTokenSource transfer)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), transfer.Token).ConfigureAwait(false);
                if (!speedTestPendingOrActive()) continue;
                transfer.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (transfer.IsCancellationRequested) { }
    }

    private void PublishProgress(long epoch, Version version, long downloaded, long total)
    {
        var percentage = (int)(100 * downloaded / total);
        PublishFor(epoch, new($"Version {version}: {downloaded / 1_000_000d:N1} / {total / 1_000_000d:N1} MB ({percentage} %) heruntergeladen.",
            true, AvailableVersion: version.ToString(3), DownloadedBytes: downloaded, TotalBytes: total));
    }

    private static Release? ReadNewest(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Ungültige GitHub-Release-Liste.");
        Release? newest = null;
        foreach (var item in list.EnumerateArray())
        {
            Release? candidate;
            // One malformed or unrelated entry must not hide a valid newer release.
            try { candidate = ReadRelease(item, allowPrerelease: true); }
            catch (InvalidDataException) { continue; }
            if (candidate is not null && (newest is null || candidate.Version > newest.Version)) newest = candidate;
        }
        return newest;
    }

    private static Release? ReadRelease(JsonElement root, bool allowPrerelease = false)
    {
        if (!root.TryGetProperty("draft", out var draft) || draft.ValueKind is not (JsonValueKind.False or JsonValueKind.True) ||
            !root.TryGetProperty("prerelease", out var pre) || pre.ValueKind is not (JsonValueKind.False or JsonValueKind.True))
            throw new InvalidDataException("Ungültige GitHub-Veröffentlichung.");
        if (draft.ValueKind == JsonValueKind.True || pre.ValueKind == JsonValueKind.True && !allowPrerelease) return null;
        if (!root.TryGetProperty("tag_name", out var tag) || !TryVersion(tag.GetString(), out var version) ||
            !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Ungültige GitHub-Veröffentlichung.");
        var name = $"DYNEX-Setup-{version.ToString(3)}.exe";
        JsonElement? installer = null, checksum = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var assetName)) continue;
            if (assetName.GetString() == name) { if (installer is not null) throw new InvalidDataException("Mehrdeutiger Installer."); installer = asset; }
            if (assetName.GetString() == name + ".sha256") { if (checksum is not null) throw new InvalidDataException("Mehrdeutige Prüfsumme."); checksum = asset; }
        }
        if (installer is not JsonElement exe || !exe.TryGetProperty("size", out var sizeElement) ||
            !sizeElement.TryGetInt64(out var size) || size is <= 0 or > MaximumInstallerBytes)
            throw new InvalidDataException("Kein gültiger DYNEX-Installer veröffentlicht.");
        var baseUrl = $"https://github.com/{Repository}/releases/download/v{version.ToString(3)}/";
        var installerUri = ExactAssetUri(exe, baseUrl + name);
        string? hash = null;
        if (exe.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String)
        {
            var value = digest.GetString()!;
            if (!value.StartsWith("sha256:", StringComparison.Ordinal) || !Sha256.IsMatch(value[7..]))
                throw new InvalidDataException("Ungültiger GitHub-Hash.");
            hash = value[7..].ToUpperInvariant();
        }
        Uri? hashUri = checksum is JsonElement sum ? ExactAssetUri(sum, baseUrl + name + ".sha256") : null;
        if (hash is null && hashUri is null) throw new InvalidDataException("Keine SHA-256-Prüfsumme vorhanden.");
        return new(version, name, installerUri, hashUri, hash, size, pre.ValueKind == JsonValueKind.True);
    }

    private static Uri ExactAssetUri(JsonElement asset, string expected)
    {
        if (!asset.TryGetProperty("browser_download_url", out var url) || url.GetString() != expected)
            throw new InvalidDataException("Update stammt nicht aus dem DYNEX-Repository.");
        return new(expected);
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, bool api, CancellationToken token)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort ||
                !(api ? uri.Host == "api.github.com" : uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("Unsicheres Update-Ziel.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("DYNEX/" + currentVersion.ToString(3));
            request.Headers.Accept.ParseAdd(api ? "application/vnd.github+json" : "application/octet-stream");
            if (api) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("Update-Weiterleitung fehlt.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            return response;
        }
        throw new InvalidDataException("Zu viele Update-Weiterleitungen.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken token)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("Update-Antwort zu groß.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var stream = await content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (result.Length + read > limit) throw new InvalidDataException("Update-Antwort zu groß.");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }

    private void Defer(long epoch, Version? version)
    {
        lock (sync) { if (epoch != generation) return; deferred = true; }
        PublishFor(epoch, new("Update wird nach dem Speedtest fortgesetzt.", AvailableVersion: version?.ToString(3)));
    }
    private bool IsDeferred() { lock (sync) return deferred; }
    private void PublishFor(long epoch, UpdateStatus value)
    {
        lock (sync) { if (disposed || epoch != generation) return; status = value; }
        Notify();
    }
    private void Notify()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch (Exception) { /* A UI subscriber cannot break download cleanup. */ }
    }
    private static UpdateStatus Ready(Version version) => new($"Version {version} geprüft. DYNEX wird geschlossen, aktualisiert und neu gestartet.", UpdateReady: true, AvailableVersion: version.ToString(3));
    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));
    private static bool TryVersion(string? tag, out Version version)
    {
        version = new(0, 0, 0);
        return tag is not null && ReleaseTag.IsMatch(tag) && Version.TryParse(tag[1..], out version!);
    }
    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { TryDelete(temporary); }
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            enabled = false;
            generation++;
            enabledLifetime?.Cancel();
            lifetime.Cancel();
        }
        // Do not dispose cancellation sources while background continuations still use them.
        _ = Task.WhenAll(tasks.ToArray()).ContinueWith(_ =>
        {
            enabledLifetime?.Dispose();
            lifetime.Dispose();
            if (ownsClient) client.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private sealed record Release(Version Version, string FileName, Uri InstallerUri, Uri? HashUri, string? Hash, long Size, bool Prerelease);
    private sealed record ReadyUpdate(string Version, string FileName, string Hash, long Size, bool? Prerelease = null);
    private sealed class DownloadDeferredException : Exception;
    private sealed class DownloadStalledException : Exception;
    private sealed class UpdateRejectedException(string message) : Exception(message);
}
