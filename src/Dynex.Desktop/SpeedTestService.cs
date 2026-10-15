using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;

namespace Dynex.Desktop;

internal interface ISpeedTestService
{
    Task<SpeedTestResult> MeasureAsync(CancellationToken token);
}

internal sealed record SpeedTestResult(double DownloadMbps, long DownloadedBytes, TimeSpan Duration,
    double? IdleLatencyMs = null, double? LoadedLatencyMs = null);

internal sealed record SpeedTestOptions(
    Uri Endpoint,
    TimeSpan Deadline,
    int StreamCount,
    long MaximumBytes,
    long BytesPerStream,
    TimeSpan MinimumDuration,
    long MinimumBytes)
{
    public static SpeedTestOptions Default { get; } = new(
        new Uri("https://speed.cloudflare.com/__down"), TimeSpan.FromSeconds(10), 4,
        256L * 1024 * 1024, 64L * 1024 * 1024, TimeSpan.FromSeconds(1), 1024 * 1024);
}

internal sealed class SpeedTestService : ISpeedTestService
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.None
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient _client;
    private readonly SpeedTestOptions _options;
    private readonly Func<CancellationToken, Task<double?>>? _probeLatency;

    public SpeedTestService() : this(SharedClient, SpeedTestOptions.Default, PingCloudflareAsync) { }

    internal SpeedTestService(HttpClient client, SpeedTestOptions? options = null, Func<CancellationToken, Task<double?>>? probeLatency = null)
    {
        _client = client;
        _probeLatency = probeLatency;
        _options = options ?? SpeedTestOptions.Default;
        if (_options.StreamCount < 1 || _options.BytesPerStream < 1 || _options.MaximumBytes < 1 ||
            _options.BytesPerStream > _options.MaximumBytes / _options.StreamCount || _options.MaximumBytes > 256L * 1024 * 1024 ||
            _options.Deadline <= TimeSpan.Zero || _options.Deadline > TimeSpan.FromSeconds(10) ||
            _options.MinimumDuration < TimeSpan.Zero || _options.MinimumBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Die Speedtest-Limits sind ungültig.");
    }

    private static async Task<double?> PingCloudflareAsync(CancellationToken token)
    {
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(IPAddress.Parse("1.1.1.1"), 1000).WaitAsync(token).ConfigureAwait(false);
            return reply.Status == System.Net.NetworkInformation.IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (System.Net.NetworkInformation.PingException) { return null; }
    }

    public async Task<SpeedTestResult> MeasureAsync(CancellationToken token)
    {
        // Bufferbloat: compare idle latency with latency while the download saturates the line.
        double? idle = null;
        if (_probeLatency is not null)
        {
            var samples = new List<double?>();
            for (var i = 0; i < 3; i++) { samples.Add(await _probeLatency(token).ConfigureAwait(false)); }
            idle = Dynex.Core.Bufferbloat.Median(samples);
        }
        using var deadline = new CancellationTokenSource(_options.Deadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var stopwatch = Stopwatch.StartNew();
        long downloaded = 0;
        async Task DownloadWorkerAsync()
        {
            try { await DownloadStreamAsync(linked.Token, bytes => Interlocked.Add(ref downloaded, bytes)).ConfigureAwait(false); }
            catch { linked.Cancel(); throw; }
        }
        var workers = Enumerable.Range(0, _options.StreamCount)
            .Select(_ => DownloadWorkerAsync())
            .ToArray();
        var loadedSamples = new List<double?>();
        using var probeStop = new CancellationTokenSource();
        var probing = _probeLatency is null ? Task.CompletedTask : Task.Run(async () =>
        {
            try
            {
                // Skip the TCP ramp-up so the samples reflect a saturated line.
                await Task.Delay(500, probeStop.Token).ConfigureAwait(false);
                while (!probeStop.IsCancellationRequested)
                {
                    var sample = await _probeLatency(probeStop.Token).ConfigureAwait(false);
                    lock (loadedSamples) loadedSamples.Add(sample);
                    await Task.Delay(250, probeStop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        });

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        {
            // The time budget is a valid measurement boundary; external cancellation remains an error.
        }
        catch
        {
            linked.Cancel();
            try { await Task.WhenAll(workers).ConfigureAwait(false); } catch { }
            throw;
        }
        finally
        {
            stopwatch.Stop();
            probeStop.Cancel();
            await probing.ConfigureAwait(false);
        }

        token.ThrowIfCancellationRequested();
        var elapsed = stopwatch.Elapsed;
        // A complete 256 MiB transfer is also useful on multi-gigabit connections taking under a second.
        var fullBudgetSample = downloaded == 256L * 1024 * 1024;
        if (downloaded < _options.MinimumBytes || elapsed < _options.MinimumDuration && !fullBudgetSample)
            throw new InvalidOperationException("Der Speedtest hat zu wenige Daten oder zu kurz gemessen.");

        var mbps = downloaded * 8d / elapsed.TotalSeconds / 1_000_000d;
        if (!double.IsFinite(mbps) || mbps < 1 || mbps > 10_000)
            throw new InvalidOperationException("Die gemessene Downloadrate liegt außerhalb des gültigen Bereichs (1 bis 10.000 Mbit/s).");

        double? loaded;
        lock (loadedSamples) loaded = Dynex.Core.Bufferbloat.Median(loadedSamples);
        return new SpeedTestResult(mbps, downloaded, elapsed, idle, loaded);
    }

    private async Task DownloadStreamAsync(CancellationToken token, Action<int> addBytes)
    {
        var requestUri = new UriBuilder(_options.Endpoint)
        {
            Query = "bytes=" + _options.BytesPerStream.ToString(CultureInfo.InvariantCulture) + "&cacheBust=" + Guid.NewGuid().ToString("N")
        }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.Pragma.ParseAdd("no-cache");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long expected && expected != _options.BytesPerStream)
            throw new InvalidOperationException("Der Speedtest-Server lieferte eine unerwartete Datenmenge.");
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Der Speedtest-Server lieferte keinen application/octet-stream-Datenstrom.");
        if (response.Content.Headers.ContentEncoding.Count > 0)
            throw new InvalidOperationException("Der Speedtest-Server komprimierte die Antwort trotz deaktivierter Kompression.");

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        long received = 0;
        while (true)
        {
            var remaining = _options.BytesPerStream - received;
            if (remaining == 0) break;
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
            if (read == 0) break;
            received += read;
            addBytes(read);
        }
        if (received != _options.BytesPerStream)
            throw new InvalidOperationException("Die Speedtest-Antwort wurde vorzeitig beendet.");
    }
}
