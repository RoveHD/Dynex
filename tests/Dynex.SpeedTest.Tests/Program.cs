using System.Net;
using Dynex.Desktop;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("PASS: " + name); checks++;
}
async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T) { Check(true, name); return; }
    throw new InvalidOperationException("FAIL: " + name + " did not throw " + typeof(T).Name);
}

var successfulHandler = new TestHandler(async (request, _) =>
{
    Check(request.RequestUri!.AbsolutePath == "/__down" && request.RequestUri.Query.StartsWith("?bytes=65536&cacheBust=", StringComparison.Ordinal), "request uses a bounded, cache-busted Cloudflare download endpoint");
    Check(request.Headers.CacheControl?.NoCache == true && request.Headers.CacheControl.NoStore, "request disables caching");
    Check(request.Headers.AcceptEncoding.Contains(new System.Net.Http.Headers.StringWithQualityHeaderValue("identity")), "request disables content encoding");
    await Task.Delay(100);
    return Response(HttpStatusCode.OK, new byte[65_536], 65_536);
});
var successfulOptions = Options(streams: 1, bytesPerStream: 65_536, maximumBytes: 65_536, deadline: TimeSpan.FromSeconds(2));
var success = await new SpeedTestService(new HttpClient(successfulHandler), successfulOptions).MeasureAsync(CancellationToken.None);
var expectedRate = success.DownloadedBytes * 8d / success.Duration.TotalSeconds / 1_000_000d;
Check(success.DownloadedBytes == 65_536 && Math.Abs(success.DownloadMbps - expectedRate) < 0.000001, "download rate is calculated from aggregate bytes and shared elapsed time");

var probes = 0;
var loadHandler = new TestHandler(async (_, token) => { await Task.Delay(1500, token); return Response(HttpStatusCode.OK, new byte[1_048_576], 1_048_576); });
var loadedResult = await new SpeedTestService(new HttpClient(loadHandler), Options(1, 1_048_576, 1_048_576, TimeSpan.FromSeconds(3)),
    _ => Task.FromResult<double?>(Interlocked.Increment(ref probes) <= 3 ? 20 : 180)).MeasureAsync(CancellationToken.None);
Check(loadedResult.IdleLatencyMs == 20 && loadedResult.LoadedLatencyMs == 180, "idle and loaded latency are measured around the download load");
Check(Dynex.Core.Bufferbloat.Rate(loadedResult.IdleLatencyMs, loadedResult.LoadedLatencyMs)?.Grade == "C", "measured latency increase is graded");
var noProbe = await new SpeedTestService(new HttpClient(successfulHandler), successfulOptions).MeasureAsync(CancellationToken.None);
Check(noProbe.IdleLatencyMs is null && noProbe.LoadedLatencyMs is null, "without a latency probe the result stays unrated");

await ThrowsAsync<ArgumentOutOfRangeException>(() => Task.Run(() => new SpeedTestService(new HttpClient(), Options(2, 65_536, 65_536))), "configured stream budget cannot exceed maximum bytes");
await ThrowsAsync<HttpRequestException>(() => new SpeedTestService(new HttpClient(new TestHandler((_, _) => Task.FromResult(Response(HttpStatusCode.InternalServerError, [], 0)))),
    Options(1, 65_536, 65_536)).MeasureAsync(CancellationToken.None), "HTTP errors fail the measurement");
await ThrowsAsync<InvalidOperationException>(() => new SpeedTestService(new HttpClient(new TestHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, [], null)))),
    Options(1, 65_536, 65_536)).MeasureAsync(CancellationToken.None), "empty response is rejected");
await ThrowsAsync<InvalidOperationException>(() => new SpeedTestService(new HttpClient(new TestHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, new byte[12], 65_536)))),
    Options(1, 65_536, 65_536)).MeasureAsync(CancellationToken.None), "truncated response is rejected");
await ThrowsAsync<InvalidOperationException>(() => new SpeedTestService(new HttpClient(new TestHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes("<html>error</html>"), null, "text/html")))),
    Options(1, 65_536, 65_536)).MeasureAsync(CancellationToken.None), "HTML response is rejected");
await ThrowsAsync<InvalidOperationException>(() => new SpeedTestService(new HttpClient(new TestHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, new byte[65_536], 65_536, "application/json")))),
    Options(1, 65_536, 65_536)).MeasureAsync(CancellationToken.None), "non-binary response type is rejected");
await ThrowsAsync<InvalidOperationException>(() => new SpeedTestService(new HttpClient(new TestHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, new byte[65_536], 65_536, contentEncoding: "gzip")))),
    Options(1, 65_536, 65_536)).MeasureAsync(CancellationToken.None), "compressed response is rejected");

var parallelQueries = new System.Collections.Concurrent.ConcurrentBag<string>();
var parallelHandler = new TestHandler(async (request, _) =>
{
    parallelQueries.Add(request.RequestUri!.Query);
    await Task.Delay(100);
    return Response(HttpStatusCode.OK, new byte[1_000_000], 1_000_000);
});
var parallel = await new SpeedTestService(new HttpClient(parallelHandler), Options(2, 1_000_000, 2_000_000)).MeasureAsync(CancellationToken.None);
Check(parallel.DownloadedBytes == 2_000_000 && parallelQueries.Count == 2 && parallelQueries.Distinct().Count() == 2,
    "parallel streams aggregate bytes and receive distinct cache-busters");

var fullBudgetChunk = new byte[64 * 1024 * 1024];
var fullBudgetHandler = new TestHandler(async (_, _) =>
{
    await Task.Delay(300);
    return Response(HttpStatusCode.OK, fullBudgetChunk, fullBudgetChunk.Length);
});
var fullBudgetTimer = System.Diagnostics.Stopwatch.StartNew();
var fullBudget = await new SpeedTestService(new HttpClient(fullBudgetHandler), Options(4, fullBudgetChunk.Length, 256L * 1024 * 1024,
    deadline: TimeSpan.FromSeconds(10), minDuration: TimeSpan.FromSeconds(1), minBytes: 1024 * 1024)).MeasureAsync(CancellationToken.None);
fullBudgetTimer.Stop();
Check(fullBudget.DownloadedBytes == 256L * 1024 * 1024 && fullBudget.Duration < TimeSpan.FromSeconds(1) &&
    fullBudgetTimer.Elapsed < TimeSpan.FromSeconds(1), "complete 256 MiB sample succeeds under one second on a fast connection");

var failFastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var failureCount = 0;
var failFastHandler = new TestHandler(async (_, _) =>
{
    var index = Interlocked.Increment(ref failureCount);
    if (index == 2) failFastStarted.TrySetResult();
    await failFastStarted.Task;
    if (index == 1)
    {
        await Task.Delay(40);
        return Response(HttpStatusCode.InternalServerError, [], 0);
    }
    return Response(HttpStatusCode.OK, new byte[65_536], 65_536, holdOpen: true);
});
var failFastTimer = System.Diagnostics.Stopwatch.StartNew();
await ThrowsAsync<HttpRequestException>(() => new SpeedTestService(new HttpClient(failFastHandler),
    Options(2, 65_536, 131_072, deadline: TimeSpan.FromSeconds(3))).MeasureAsync(CancellationToken.None), "one stream failure cancels sibling streams immediately");
Check(failFastTimer.Elapsed < TimeSpan.FromSeconds(1), "stream failure does not wait for the overall deadline");

await ThrowsAsync<InvalidOperationException>(() => new SpeedTestService(new HttpClient(new TestHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, new byte[128], 65_536)))),
    Options(1, 65_536, 65_536, minBytes: 1024)).MeasureAsync(CancellationToken.None), "measurement with too little data is rejected");
var shortSampleHandler = new TestHandler(async (_, _) =>
{
    await Task.Delay(100);
    return Response(HttpStatusCode.OK, new byte[4 * 1024 * 1024], 4 * 1024 * 1024);
});
await ThrowsAsync<InvalidOperationException>(() => new SpeedTestService(new HttpClient(shortSampleHandler),
    Options(1, 4 * 1024 * 1024, 4 * 1024 * 1024, deadline: TimeSpan.FromSeconds(9), minDuration: TimeSpan.FromSeconds(2), minBytes: 1024))
    .MeasureAsync(CancellationToken.None), "small sample shorter than the minimum duration is rejected");

var slowHandler = new TestHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, new byte[1_500_000], 2_000_000, holdOpen: true)));
var deadlineOptions = Options(1, 2_000_000, 2_000_000, deadline: TimeSpan.FromMilliseconds(180), minDuration: TimeSpan.FromMilliseconds(20), minBytes: 1_000_000);
var partial = await new SpeedTestService(new HttpClient(slowHandler), deadlineOptions).MeasureAsync(CancellationToken.None);
Check(partial.DownloadedBytes == 1_500_000 && partial.Duration >= deadlineOptions.MinimumDuration, "deadline returns a sufficiently long valid partial measurement");

var abortHandler = new TestHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, new byte[1_500_000], 2_000_000, holdOpen: true)));
using var callerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(70));
await ThrowsAsync<OperationCanceledException>(() => new SpeedTestService(new HttpClient(abortHandler), deadlineOptions with { Deadline = TimeSpan.FromSeconds(2) })
    .MeasureAsync(callerCancellation.Token), "caller cancellation is propagated instead of treated as a deadline result");

Console.WriteLine($"All {checks} speed test checks passed.");

static SpeedTestOptions Options(int streams, long bytesPerStream, long maximumBytes, TimeSpan? deadline = null,
    TimeSpan? minDuration = null, long minBytes = 1) => new(new Uri("https://speed.cloudflare.com/__down"),
    deadline ?? TimeSpan.FromSeconds(2), streams, maximumBytes, bytesPerStream, minDuration ?? TimeSpan.FromMilliseconds(20), minBytes);

static HttpResponseMessage Response(HttpStatusCode status, byte[] bytes, long? contentLength, string? mediaType = "application/octet-stream", bool holdOpen = false, string? contentEncoding = null)
{
    var content = new StreamContent(new TestStream(bytes, holdOpen));
    if (contentLength.HasValue) content.Headers.ContentLength = contentLength;
    if (mediaType is not null) content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
    if (contentEncoding is not null) content.Headers.ContentEncoding.Add(contentEncoding);
    return new HttpResponseMessage(status) { Content = content };
}

sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
}

sealed class TestStream(byte[] bytes, bool holdOpen) : MemoryStream(bytes, writable: false)
{
    private bool _finished;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await base.ReadAsync(buffer, cancellationToken);
        if (read == 0 && holdOpen && !_finished)
        {
            _finished = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        return read;
    }
}
