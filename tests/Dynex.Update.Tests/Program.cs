using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dynex.Desktop.Updates;

if (args.Contains("--live-check", StringComparer.Ordinal))
{
    var cache = Path.Combine(Path.GetTempPath(), "dynex-readonly-update-check-" + Guid.NewGuid().ToString("N"));
    using var updater = new GitHubUpdateService(cache,
        typeof(GitHubUpdateService).Assembly.GetName().Version!, () => true);
    // Leave automatic updates disabled: this mode can only read release metadata.
    await updater.CheckNowAsync();
    Console.WriteLine(updater.Status.Message);
    return updater.Status.Message == "DYNEX ist auf dem neuesten Stand." ? 0 : 1;
}

var checks = 0;
var failures = new List<string>();
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++;
    Console.WriteLine("PASS: " + name);
}
async Task Scenario(string name, Func<Task> test)
{
    try { await test().WaitAsync(TimeSpan.FromSeconds(10)); }
    catch (Exception error) { failures.Add(name + ": " + error); Console.Error.WriteLine("FAIL: " + name + ": " + error.Message); }
}

await Scenario("opt in and consume", async () =>
{
    using var fixture = new Fixture();
    await fixture.Service.CheckNowAsync();
    Check(fixture.Handler.ApiCalls == 1 && fixture.Handler.DownloadCalls == 0, "manual check while disabled performs no download");
    Check(fixture.Service.Status.AvailableVersion == "0.5.0" && !fixture.Service.Status.IsBusy, "manual disabled check reports available version");
    Check(await fixture.Service.TakeReadyInstallerAsync() is null, "disabled updater cannot install");
    fixture.Service.SetEnabled(true);
    await fixture.Service.CheckNowAsync();
    Check(fixture.Service.Status.UpdateReady, "opt in downloads a verified release");
    var path = await fixture.Service.TakeReadyInstallerAsync();
    Check(path is not null && File.ReadAllBytes(path).SequenceEqual(fixture.Handler.Payload), "ready installer has the exact GitHub payload");
    Check(await fixture.Service.TakeReadyInstallerAsync() is null, "ready marker is consumed once");
    Check(!Directory.EnumerateFiles(fixture.Directory, "*.part").Any(), "completed download leaves no partial file");
});

await Scenario("checksum asset fallback", async () =>
{
    using var fixture = new Fixture();
    fixture.Handler.UseDigest = false;
    fixture.Service.SetEnabled(true);
    await fixture.Service.CheckNowAsync();
    Check(fixture.Service.Status.UpdateReady && fixture.Handler.ChecksumCalls == 1, "separate SHA-256 asset is verified when digest is absent");
});

await Scenario("startup handoff without network", async () =>
{
    using var fixture = new Fixture();
    fixture.Service.SetEnabled(true, startBackground: false);
    Check(fixture.Handler.ApiCalls == 0, "startup cache activation makes no HTTP request");
    Check(await fixture.Service.TakeReadyInstallerAsync() is null && fixture.Handler.ApiCalls == 0,
        "empty startup cache returns immediately without checking GitHub");
    fixture.Service.SetEnabled(true);
    await fixture.Service.CheckNowAsync();
    Check(fixture.Service.Status.UpdateReady, "background loop can start after cache-only activation");
    fixture.Service.Dispose();
    using var nextClient = new HttpClient(new FakeGitHub());
    using var nextStart = new GitHubUpdateService(fixture.Directory, new Version(0, 4, 1), () => false,
        nextClient, TimeSpan.FromHours(6), TimeSpan.FromHours(1), 1024 * 1024);
    nextStart.SetEnabled(true, startBackground: false);
    Check(await nextStart.TakeReadyInstallerAsync() is not null, "next startup can hand off the previously verified download offline");
});

await Scenario("installed version rejects stale cache", async () =>
{
    using var fixture = new Fixture();
    fixture.Service.SetEnabled(true);
    await fixture.Service.CheckNowAsync();
    fixture.Service.Dispose();
    using var client = new HttpClient(new FakeGitHub());
    using var updated = new GitHubUpdateService(fixture.Directory, new Version(0, 5, 0), () => false,
        client, TimeSpan.FromHours(6), TimeSpan.FromHours(1), 1024 * 1024);
    updated.SetEnabled(true, startBackground: false);
    Check(await updated.TakeReadyInstallerAsync() is null, "current installed version cannot reinstall cached same version");
});

await Scenario("beta channel", async () =>
{
    using var stable = new Fixture();
    stable.Handler.Variant = "prerelease";
    stable.Service.SetEnabled(true);
    await stable.Service.CheckNowAsync();
    Check(!stable.Service.Status.UpdateReady, "stable channel ignores pre-releases");
    using var fixture = new Fixture();
    fixture.Service.SetBetaChannel(true);
    fixture.Service.SetEnabled(true);
    await fixture.Service.CheckNowAsync();
    Check(fixture.Service.Status.UpdateReady && fixture.Service.Status.AvailableVersion == "0.5.0",
        "beta channel installs the newest verified pre-release and skips malformed entries");
    using var drafts = new Fixture();
    drafts.Handler.Variant = "draft";
    drafts.Service.SetBetaChannel(true);
    drafts.Service.SetEnabled(true);
    await drafts.Service.CheckNowAsync();
    Check(!drafts.Service.Status.UpdateReady, "beta channel never installs drafts");
});

var publisher = new SignatureCheck(true, true, "CN=DYNEX Publisher");
foreach (var (name, candidate, current, accepted) in new[]
{
    ("signed install requires signed update", SignatureCheck.Unsigned, publisher, false),
    ("same publisher is accepted", publisher, publisher, true),
    ("other publisher is rejected", new SignatureCheck(true, true, "CN=Someone Else"), publisher, false),
    ("broken signature is rejected even on unsigned install", new SignatureCheck(true, false, "CN=DYNEX Publisher"), SignatureCheck.Unsigned, false),
    ("unsigned legacy install accepts hash-verified unsigned update", SignatureCheck.Unsigned, SignatureCheck.Unsigned, true),
})
{
    await Scenario("signature: " + name, async () =>
    {
        using var fixture = new Fixture(current: current) { Candidate = candidate };
        fixture.Service.SetEnabled(true);
        await fixture.Service.CheckNowAsync();
        Check(fixture.Service.Status.UpdateReady == accepted, name);
        if (!accepted)
        {
            Check(fixture.Service.Status.Message.StartsWith("Update verworfen:"), name + " explains the rejection");
            Check(!System.IO.Directory.EnumerateFiles(fixture.Directory).Any(f => f.EndsWith(".exe") || f.EndsWith(".part")), name + " keeps no installer");
        }
    });
}

await Scenario("signature re-checked before handoff", async () =>
{
    using var fixture = new Fixture(current: publisher) { Candidate = publisher };
    fixture.Service.SetEnabled(true);
    await fixture.Service.CheckNowAsync();
    Check(fixture.Service.Status.UpdateReady, "signed update becomes ready");
    fixture.Candidate = SignatureCheck.Unsigned; // File swapped on disk after download.
    Check(await fixture.Service.TakeReadyInstallerAsync() is null, "installer whose signature changed after download is not executed");
    Check(!File.Exists(Path.Combine(fixture.Directory, "ready.json")) && !File.Exists(Path.Combine(fixture.Directory, "DYNEX-Setup-0.5.0.exe")),
        "rejected ready installer is removed");
});

await Scenario("rolled back version is not downloaded again", async () =>
{
    using var fixture = new Fixture();
    System.IO.Directory.CreateDirectory(fixture.Directory);
    File.WriteAllText(Path.Combine(fixture.Directory, "rolled-back.txt"), "0.5.0\r\n");
    Check(fixture.Service.RolledBackVersion == "0.5.0", "installer rollback marker is read");
    fixture.Service.SetEnabled(true);
    await fixture.Service.CheckNowAsync();
    Check(!fixture.Service.Status.UpdateReady && fixture.Handler.DownloadCalls == 0 && fixture.Service.Status.Message.Contains("wiederhergestellt"),
        "a version that failed to install is reported instead of re-downloaded");
    File.WriteAllText(Path.Combine(fixture.Directory, "rolled-back.txt"), "0.4.0");
    Check(fixture.Service.RolledBackVersion is null, "marker for an older version is ignored");
});

foreach (var variant in new[] { "current", "older", "prerelease", "draft", "version-suffix", "version-leading-zero", "foreign-url", "wrong-name", "missing-hash", "duplicate", "oversized", "bad-digest", "bad-hash", "bad-size", "bad-json", "stream-short", "stream-long" })
{
    await Scenario("reject " + variant, async () =>
    {
        using var fixture = new Fixture();
        fixture.Handler.Variant = variant;
        fixture.Service.SetEnabled(true);
        await fixture.Service.CheckNowAsync();
        Check(!fixture.Service.Status.UpdateReady, variant + " cannot become ready");
        if (variant is "wrong-name" or "oversized" or "version-suffix" or "version-leading-zero")
            Check(fixture.Service.Status.Message.Contains("nicht geladen"), variant + " reports invalid release instead of claiming current version");
        Check(await fixture.Service.TakeReadyInstallerAsync() is null, variant + " cannot provide an installer");
        Check(!System.IO.Directory.Exists(fixture.Directory) || !System.IO.Directory.EnumerateFiles(fixture.Directory, "*.part").Any(), variant + " leaves no partial installer");
    });
}

await Scenario("tampered persisted cache", async () =>
{
    using var fixture = new Fixture();
    fixture.Service.SetEnabled(true);
    await fixture.Service.CheckNowAsync();
    var path = Directory.GetFiles(fixture.Directory, "*.exe").Single();
    await File.WriteAllBytesAsync(path, [1, 2, 3]);
    Check(await fixture.Service.TakeReadyInstallerAsync() is null, "cached installer is hashed again before handoff");
    var escapedPath = Path.Combine(fixture.Directory, "..", "escape.exe");
    await File.WriteAllBytesAsync(escapedPath, fixture.Handler.Payload);
    await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "ready.json"), JsonSerializer.Serialize(new
    {
        Version = "0.5.0", FileName = "../escape.exe", Hash = fixture.Handler.Hash, Size = fixture.Handler.Payload.Length
    }));
    Check(await fixture.Service.TakeReadyInstallerAsync() is null, "persisted filename traversal is rejected");
    File.Delete(escapedPath);
    await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "ready.json"), "{\"Version\":\"0.5.0\",\"FileName\":\"DYNEX-Setup-0.5.0.exe\",\"Hash\":null,\"Size\":3}");
    Check(await fixture.Service.TakeReadyInstallerAsync() is null, "null persisted checksum is rejected without crashing startup");
});

await Scenario("disable ready update", async () =>
{
    using var fixture = new Fixture();
    fixture.Service.SetEnabled(true);
    await fixture.Service.CheckNowAsync();
    fixture.Service.SetEnabled(false);
    Check(await fixture.Service.TakeReadyInstallerAsync() is null, "unchecking blocks an already downloaded update");
    Check(!fixture.Service.Status.IsBusy && !fixture.Service.Status.UpdateReady, "unchecked status does not claim an installation is pending");
});

await Scenario("defer gaming and resume", async () =>
{
    using var fixture = new Fixture(deferInterval: TimeSpan.FromMilliseconds(20));
    fixture.Gaming = true;
    fixture.Service.SetEnabled(true);
    await fixture.Service.CheckNowAsync();
    Check(fixture.Handler.DownloadCalls == 0 && fixture.Service.Status.Message.Contains("Spiel"), "gaming defers installer network traffic");
    await Task.Delay(100);
    Check(fixture.Handler.ApiCalls == 1, "gaming polls local state without repeated GitHub requests");
    fixture.Gaming = false;
    await fixture.Ready.Task;
    Check(fixture.Service.Status.UpdateReady && fixture.Handler.DownloadCalls == 1, "background retry downloads after gaming stops");
});

await Scenario("mid transfer game start", async () =>
{
    using var fixture = new Fixture();
    fixture.Handler.OnPayloadRead = () => fixture.Gaming = true;
    fixture.Service.SetEnabled(true);
    await fixture.Service.CheckNowAsync();
    Check(!fixture.Service.Status.UpdateReady && fixture.Service.Status.Message.Contains("Spiel"), "starting a game interrupts an update download");
    Check(!Directory.GetFiles(fixture.Directory, "*.part").Any(), "deferred partial download is removed");
});

await Scenario("cached deferred update returns to normal cadence", async () =>
{
    using var fixture = new Fixture(deferInterval: TimeSpan.FromMilliseconds(20));
    Directory.CreateDirectory(fixture.Directory);
    await File.WriteAllBytesAsync(Path.Combine(fixture.Directory, "DYNEX-Setup-0.5.0.exe"), fixture.Handler.Payload);
    await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "ready.json"), JsonSerializer.Serialize(new
    {
        Version = "0.5.0", FileName = "DYNEX-Setup-0.5.0.exe", Hash = fixture.Handler.Hash, Size = fixture.Handler.Payload.Length
    }));
    fixture.Gaming = true;
    fixture.Service.SetEnabled(true);
    await fixture.Service.CheckNowAsync();
    fixture.Gaming = false;
    await fixture.Ready.Task;
    var calls = fixture.Handler.ApiCalls;
    await Task.Delay(100);
    Check(fixture.Handler.ApiCalls == calls && calls == 2, "verified cache clears deferred polling and resumes six-hour cadence");
    Check(fixture.Handler.DownloadCalls == 0, "resumed cached update is not downloaded twice");
});

await Scenario("turn off during download and concurrency", async () =>
{
    using var fixture = new Fixture();
    fixture.Handler.BlockPayload = true;
    fixture.Service.SetEnabled(true);
    var first = fixture.Service.CheckNowAsync();
    await fixture.Handler.PayloadStarted.Task;
    var checksTogether = Enumerable.Range(0, 12).Select(_ => fixture.Service.CheckNowAsync()).ToArray();
    Check(checksTogether.All(task => ReferenceEquals(first, task)), "concurrent checks share one operation");
    fixture.Service.SetEnabled(false);
    await first;
    Check(fixture.Handler.DownloadCalls == 1 && !fixture.Service.Status.IsBusy, "unchecking cancels the active transfer");
    Check(!Directory.GetFiles(fixture.Directory, "*.part").Any(), "cancelled download leaves no partial file");
    Check(await fixture.Service.TakeReadyInstallerAsync() is null, "cancelled transfer cannot be installed");
    fixture.Handler.BlockPayload = false;
    fixture.Service.SetEnabled(true);
    await fixture.Service.CheckNowAsync();
    Check(fixture.Service.Status.UpdateReady && fixture.Handler.DownloadCalls == 2, "re-enabling starts a fresh generation after cancellation");
});

foreach (var redirect in new[] { "http://github.com/RoveHD/Dynex/evil", "https://example.com/evil", "https://github.com.evil.test/evil", "https://127.0.0.1/evil", "https://github.com:444/evil", "https://user@github.com/evil" })
{
    await Scenario("redirect " + redirect, async () =>
    {
        using var fixture = new Fixture();
        fixture.Handler.Redirect = redirect;
        fixture.Service.SetEnabled(true);
        await fixture.Service.CheckNowAsync();
        Check(!fixture.Service.Status.UpdateReady && fixture.Handler.ForeignCalls == 0, "unsafe redirect is rejected before sending: " + redirect);
    });
}

await Scenario("allowed asset redirect", async () =>
{
    using var fixture = new Fixture();
    fixture.Handler.Redirect = "https://release-assets.githubusercontent.com/github-production-release-asset/known";
    fixture.Service.SetEnabled(true);
    await fixture.Service.CheckNowAsync();
    Check(fixture.Service.Status.UpdateReady, "GitHub release asset host is supported");
});

await Scenario("subscriber fault isolation and disposal", async () =>
{
    using var fixture = new Fixture();
    fixture.Service.Changed += () => throw new InvalidOperationException("UI fault");
    fixture.Service.SetEnabled(true);
    await fixture.Service.CheckNowAsync();
    Check(fixture.Service.Status.UpdateReady, "subscriber failure cannot break verification or cleanup");
    fixture.Service.Dispose();
    Check(await fixture.Service.TakeReadyInstallerAsync() is null, "disposed updater refuses handoff");
    await fixture.Service.CheckNowAsync();
});

Console.WriteLine($"{checks} checks, {failures.Count} failures");
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

sealed class Fixture : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "dynex-updates-" + Guid.NewGuid().ToString("N"));
    public FakeGitHub Handler { get; } = new();
    public GitHubUpdateService Service { get; }
    public bool Gaming;
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HttpClient client;
    public SignatureCheck Candidate = SignatureCheck.Unsigned;
    public Fixture(TimeSpan? deferInterval = null, SignatureCheck? current = null)
    {
        client = new(Handler);
        Service = new(Directory, new Version(0, 4, 1), () => Gaming, client,
            TimeSpan.FromHours(6), deferInterval ?? TimeSpan.FromHours(1), 1024 * 1024 * 1024,
            _ => Candidate, current ?? SignatureCheck.Unsigned);
        Service.Changed += () => { if (Service.Status.UpdateReady) Ready.TrySetResult(); };
    }
    public void Dispose()
    {
        Service.Dispose();
        client.Dispose();
        try { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); } catch (IOException) { }
    }
}

sealed class FakeGitHub : HttpMessageHandler
{
    public byte[] Payload { get; } = Enumerable.Range(0, 100000).Select(i => (byte)i).ToArray();
    public string Hash => Convert.ToHexString(SHA256.HashData(Payload));
    public string Variant = "";
    public bool UseDigest = true;
    public bool BlockPayload;
    public string? Redirect;
    public Action? OnPayloadRead;
    public int ApiCalls, DownloadCalls, ChecksumCalls, ForeignCalls;
    public TaskCompletionSource PayloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!;
        if (uri.Host == "api.github.com")
        {
            Interlocked.Increment(ref ApiCalls);
            var version = Variant == "current" ? "0.4.1" : Variant == "older" ? "0.4.0" : "0.5.0";
            var tag = Variant == "version-suffix" ? "v0.5.0-evil" : Variant == "version-leading-zero" ? "v00.5.0" : "v" + version;
            var name = $"DYNEX-Setup-{version}.exe";
            var url = $"https://github.com/RoveHD/Dynex/releases/download/v{version}/";
            var assets = new List<object>();
            var installer = new { name = Variant == "wrong-name" ? "other.exe" : name,
                browser_download_url = Variant == "foreign-url" ? "https://example.com/evil.exe" : url + name,
                size = Variant == "oversized" ? 999999999L : Variant == "bad-size" ? Payload.Length + 1 : Payload.Length,
                digest = Variant == "missing-hash" || !UseDigest ? null : Variant == "bad-digest" ? "md5:abc" : "sha256:" + (Variant == "bad-hash" ? new string('0', 64) : Hash) };
            assets.Add(installer);
            if (Variant == "duplicate") assets.Add(installer);
            if (Variant != "missing-hash") assets.Add(new { name = name + ".sha256", browser_download_url = url + name + ".sha256", size = 100 });
            var json = Variant == "bad-json" ? "{bad" : JsonSerializer.Serialize(new { tag_name = tag, draft = Variant == "draft", prerelease = Variant == "prerelease", assets });
            if (uri.AbsolutePath.EndsWith("/releases"))
            {
                // Beta list: a malformed entry, an older stable release and the requested entry as pre-release.
                var list = new object[]
                {
                    new { tag_name = "nightly", draft = false, prerelease = true, assets = Array.Empty<object>() },
                    new { tag_name = "v0.4.0", draft = false, prerelease = false, assets = Array.Empty<object>() },
                    new { tag_name = tag, draft = Variant == "draft", prerelease = true, assets }
                };
                json = JsonSerializer.Serialize(list);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
        if (uri.Host != "github.com" && uri.Host != "release-assets.githubusercontent.com")
        {
            ForeignCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        }
        if (uri.AbsolutePath.EndsWith(".sha256"))
        {
            ChecksumCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Hash + "  DYNEX-Setup-0.5.0.exe") });
        }
        if (Redirect is not null && uri.Host == "github.com")
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new(Redirect);
            return Task.FromResult(response);
        }
        Interlocked.Increment(ref DownloadCalls);
        PayloadStarted.TrySetResult();
        var bytes = Variant == "stream-short" ? Payload[..^1] : Variant == "stream-long" ? [.. Payload, (byte)1] : Payload;
        var content = new StreamContent(new PayloadStream(bytes, OnPayloadRead, BlockPayload));
        content.Headers.ContentLength = Payload.Length;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}

sealed class PayloadStream(byte[] data, Action? onRead, bool block) : MemoryStream(data, false)
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var read = await base.ReadAsync(buffer, cancellationToken);
        onRead?.Invoke();
        return read;
    }
}
