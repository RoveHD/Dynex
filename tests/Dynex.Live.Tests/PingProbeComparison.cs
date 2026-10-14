using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

internal static class PingProbeComparison
{
    private sealed record Probe(int TimeoutMs, int Index, string Target, DateTimeOffset At, string Status, long? Rtt, double ElapsedMs);
    public static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        var targets = new[] { "192.168.1.1", "1.1.1.1" };
        var rows = new List<Probe>();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        for (var i = 0; i < 50; i++)
        {
            var cycle = Stopwatch.StartNew();
            var timeouts = i % 2 == 0 ? new[] { 900, 1000 } : new[] { 1000, 900 };
            rows.AddRange(await Task.WhenAll(timeouts.SelectMany(timeout => targets.Select(target => Send(timeout, i, target)))));
            var remaining = TimeSpan.FromMilliseconds(700) - cycle.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, lifetime.Token);
        }
        var summary = rows.GroupBy(r => (r.TimeoutMs, r.Target)).Select(g => new { g.Key.TimeoutMs, g.Key.Target, Sent=g.Count(), Success=g.Count(r=>r.Status=="Success"), Failures=g.Where(r=>r.Status!="Success").ToArray(), AverageMs=g.Where(r=>r.Rtt.HasValue).Select(r=>r.Rtt).Average() }).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(summary));
        await File.WriteAllTextAsync(Path.Combine(output, "observations.json"), JsonSerializer.Serialize(new { Summary=summary, Observations=rows }, new JsonSerializerOptions { WriteIndented=true }));
        return 0;

        async Task<Probe> Send(int timeout, int index, string target)
        {
            using var ping = new Ping(); var at = DateTimeOffset.UtcNow; var clock = Stopwatch.StartNew();
            try
            {
                var reply = await ping.SendPingAsync(IPAddress.Parse(target), TimeSpan.FromMilliseconds(timeout), cancellationToken:lifetime.Token);
                return new(timeout,index,target,at,reply.Status.ToString(),reply.Status==IPStatus.Success?reply.RoundtripTime:null,clock.Elapsed.TotalMilliseconds);
            }
            catch (PingException error) { return new(timeout,index,target,at,error.GetType().Name,null,clock.Elapsed.TotalMilliseconds); }
        }
    }
}
