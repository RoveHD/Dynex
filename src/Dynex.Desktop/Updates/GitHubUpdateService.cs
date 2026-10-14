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
    bool UpdateReady = false, string? AvailableVersion = null);

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
    private readonly Func<bool> gameActive;
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly TimeSpan checkInterval;
    private readonly TimeSpan deferredInterval;
    private readonly int bytesPerSecond;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? enabledLifetime;
    private Task operation = Task.CompletedTask;
    private Task loop = Task.CompletedTask;
    private readonly List<Task> tasks = [];
    private long operationEpoch = -1;
    private long backgroundEpoch = -1;
    private bool enabled;
    private bool disposed;
    private bool deferred;
    private long generation;
    private UpdateStatus status = new("Automatische Updates sind ausgeschaltet.");

    public GitHubUpdateService(string cacheDirectory, Version currentVersion, Func<bool> gameActive)
        : this(cacheDirectory, currentVersion, gameActive, null, TimeSpan.FromHours(6), TimeSpan.FromSeconds(30), 512 * 1024) { }

    internal GitHubUpdateService(string cacheDirectory, Version currentVersion, Func<bool> gameActive,
        HttpClient? httpClient, TimeSpan checkInterval, TimeSpan deferredInterval, int bytesPerSecond)
    {
        directory = Path.GetFullPath(cacheDirectory);
        this.currentVersion = Normalize(currentVersion);
        this.gameActive = gameActive;
        this.checkInterval = checkInterval;
        this.deferredInterval = deferredInterval;
        this.bytesPerSecond = bytesPerSecond > 0 ? bytesPerSecond : throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        ownsClient = httpClient is null;
        // Inspect every redirect ourselves before sending the next request.
        client = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public event Action? Changed;
    public UpdateStatus Status { get { lock (sync) return status; } }

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
                generation++;
                var previousLifetime = enabledLifetime;
                previousLifetime?.Cancel();
                if (previousLifetime is not null)
                    _ = Task.WhenAll(tasks.ToArray()).ContinueWith(_ => previousLifetime.Dispose(), TaskScheduler.Default);
                enabledLifetime = value ? CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token) : null;
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
        if (changed) Publish(new(value ? "Automatische Updates sind eingeschaltet." : "Automatische Updates sind ausgeschaltet."));
    }

    public Task CheckNowAsync()
    {
        lock (sync)
        {
            if (disposed) return Task.CompletedTask;
            if (!operation.IsCompleted && operationEpoch == generation) return operation;
            var epoch = generation;
            var download = enabled;
            var token = download ? enabledLifetime!.Token : lifetime.Token;
            operationEpoch = epoch;
            operation = Task.Run(() => CheckAsync(epoch, download, token));
            tasks.RemoveAll(task => task.IsCompleted);
            tasks.Add(operation);
            return operation;
        }
    }

    private async Task BackgroundAsync(long epoch, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await CheckNowAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                TimeSpan delay;
                lock (sync) delay = deferred ? deferredInterval : checkInterval;
                await Task.Delay(delay, token).ConfigureAwait(false);
                // Poll only local game state while deferred. Repeated API checks would exhaust
                // GitHub's anonymous per-IP quota when several LAN peers are playing.
                while (IsDeferred() && gameActive())
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
            lock (sync) { if (generation == epoch) deferred = false; }
            PublishFor(epoch, new("GitHub wird auf Updates geprüft …", true));
            using var response = await GetAsync(new($"https://api.github.com/repos/{Repository}/releases/latest"), true, cancellation).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                PublishFor(epoch, new("Noch kein Update auf GitHub verfügbar."));
                return;
            }
            response.EnsureSuccessStatusCode();
            var json = await ReadBoundedAsync(response.Content, 1024 * 1024, cancellation).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var release = ReadRelease(document.RootElement);
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
            if (gameActive()) { Defer(epoch, release.Version); return; }
            Directory.CreateDirectory(directory);
            var cached = await ReadVerifiedReadyAsync(cancellation).ConfigureAwait(false);
            if (cached is not null && cached.Version == release.Version.ToString(3))
            {
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
            if (gameActive()) { Defer(epoch, release.Version); return; }
            PublishFor(epoch, new($"Version {release.Version} wird im Hintergrund heruntergeladen …", true, AvailableVersion: release.Version.ToString(3)));
            partial = Path.Combine(directory, release.FileName + "." + Guid.NewGuid().ToString("N") + ".part");
            using var installer = await GetAsync(release.InstallerUri, false, cancellation).ConfigureAwait(false);
            installer.EnsureSuccessStatusCode();
            if (installer.Content.Headers.ContentLength is long contentLength && contentLength != release.Size)
                throw new InvalidDataException("Die Downloadgröße stimmt nicht mit GitHub überein.");
            await DownloadAsync(installer.Content, partial, release.Size, hash, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (disposed || !enabled || generation != epoch) return;
                var finalPath = Path.Combine(directory, release.FileName);
                File.Move(partial, finalPath, true);
                partial = null;
                var ready = new ReadyUpdate(release.Version.ToString(3), release.FileName, hash, release.Size);
                WriteAtomic(Path.Combine(directory, "ready.json"), JsonSerializer.Serialize(ready));
                deferred = false;
            }
            PublishFor(epoch, Ready(release.Version));
        }
        catch (DownloadDeferredException) { Defer(epoch, null); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or FormatException or InvalidOperationException or OperationCanceledException or OverflowException or UnauthorizedAccessException)
        {
            PublishFor(epoch, new("Update konnte gerade nicht geladen werden. Später erneut versuchen."));
        }
        finally
        {
            if (partial is not null) TryDelete(partial);
        }
    }

    /// <summary>Call only before starting network monitoring. The caller owns installer execution.</summary>
    public async Task<string?> TakeReadyInstallerAsync()
    {
        long epoch;
        CancellationToken token;
        lock (sync)
        {
            if (!enabled || disposed) return null;
            epoch = generation;
            token = enabledLifetime!.Token;
        }
        try
        {
            var ready = await ReadVerifiedReadyAsync(token).ConfigureAwait(false);
            if (ready is null) return null;
            lock (sync)
            {
                if (!enabled || disposed || epoch != generation) return null;
                var current = JsonSerializer.Deserialize<ReadyUpdate>(File.ReadAllText(Path.Combine(directory, "ready.json")));
                if (current != ready) return null;
                // Consuming the marker prevents an unsuccessful handoff being repeated every launch.
                File.Delete(Path.Combine(directory, "ready.json"));
                return Path.Combine(directory, ready.FileName);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException) { return null; }
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

    private async Task DownloadAsync(HttpContent content, string path, long size, string hash, CancellationToken token)
    {
        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[32 * 1024];
        long total = 0;
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (gameActive()) throw new DownloadDeferredException();
            var read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > size || total > MaximumInstallerBytes) throw new InvalidDataException("Download zu groß.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            digest.AppendData(buffer, 0, read);
            var wait = TimeSpan.FromSeconds((double)total / bytesPerSecond) - elapsed.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
        }
        if (total != size || !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(hash)))
            throw new InvalidDataException("Download ist unvollständig oder beschädigt.");
        await output.FlushAsync(token).ConfigureAwait(false);
        output.Flush(true);
    }

    private static Release? ReadRelease(JsonElement root)
    {
        if (!root.TryGetProperty("draft", out var draft) || draft.ValueKind is not (JsonValueKind.False or JsonValueKind.True) ||
            !root.TryGetProperty("prerelease", out var pre) || pre.ValueKind is not (JsonValueKind.False or JsonValueKind.True))
            throw new InvalidDataException("Ungültige GitHub-Veröffentlichung.");
        if (draft.ValueKind == JsonValueKind.True || pre.ValueKind == JsonValueKind.True) return null;
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
        return new(version, name, installerUri, hashUri, hash, size);
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
        PublishFor(epoch, new("Update wartet, bis dein Spiel beendet ist.", AvailableVersion: version?.ToString(3)));
    }
    private bool IsDeferred() { lock (sync) return deferred; }
    private void PublishFor(long epoch, UpdateStatus value)
    {
        lock (sync) { if (disposed || epoch != generation) return; status = value; }
        Notify();
    }
    private void Publish(UpdateStatus value)
    {
        lock (sync) { if (disposed) return; status = value; }
        Notify();
    }
    private void Notify()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch (Exception) { /* A UI subscriber cannot break download cleanup. */ }
    }
    private static UpdateStatus Ready(Version version) => new($"Version {version} bereit. Wird beim nächsten Start installiert.", UpdateReady: true, AvailableVersion: version.ToString(3));
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

    private sealed record Release(Version Version, string FileName, Uri InstallerUri, Uri? HashUri, string? Hash, long Size);
    private sealed record ReadyUpdate(string Version, string FileName, string Hash, long Size);
    private sealed class DownloadDeferredException : Exception;
}
