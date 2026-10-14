using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Dynex.Desktop;

internal sealed class NetworkMonitor
{
    private readonly Queue<double?> router = new();
    private readonly Queue<double?> external = new();
    private string? adapterId;
    private long previousReceived, previousSent;
    private long previousTime;
    public async Task<Measurement> SampleAsync(UserSettings settings, CancellationToken token)
    {
        // UDP connect chooses the route/local address without sending a datagram.
        // Select that exact interface, including VPN routes, rather than the first gateway.
        IPAddress? localAddress = null;
        try
        {
            var destination = IPAddress.Parse(settings.ExternalTarget);
            using var route = new Socket(destination.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            route.Connect(new IPEndPoint(destination, 443));
            localAddress = (route.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException) { }
        var adapter = localAddress == null ? null : NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(localAddress)));
        var gateway = adapter?.GetIPProperties().GatewayAddresses.Select(g => g.Address)
            .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !ip.Equals(IPAddress.Any));
        var routerTarget = string.IsNullOrWhiteSpace(settings.RouterTarget) ? gateway?.ToString() ?? "" : settings.RouterTarget;
        var routeId = $"{adapter?.Id}|{localAddress}|{gateway}|{routerTarget}";
        var sameRoute = adapterId == routeId;
        if (!sameRoute) { router.Clear(); external.Clear(); }
        var pingTasks = await Task.WhenAll(PingAsync(routerTarget, token), PingAsync(settings.ExternalTarget, token));
        token.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(routerTarget)) Push(router, pingTasks[0]);
        Push(external, pingTasks[1]);
        double? down = null, up = null;
        if (adapter != null)
        {
            var stats = adapter.GetIPStatistics();
            var now = Stopwatch.GetTimestamp();
            var seconds = Stopwatch.GetElapsedTime(previousTime, now).TotalSeconds;
            if (sameRoute && previousTime != 0 && seconds > 0 && stats.BytesReceived >= previousReceived && stats.BytesSent >= previousSent)
            { down = (stats.BytesReceived - previousReceived) * 8 / seconds / 1_000_000; up = (stats.BytesSent - previousSent) * 8 / seconds / 1_000_000; }
            adapterId = routeId; previousTime = now; previousReceived = stats.BytesReceived; previousSent = stats.BytesSent;
        }
        else { adapterId = null; previousTime = 0; }
        return new(View(router, routerTarget), View(external, settings.ExternalTarget), down, up, adapter?.Name ?? "Keine Route zum Messziel", routeId);
    }
    private static async Task<double?> PingAsync(string target, CancellationToken token)
    {
        if (!IPAddress.TryParse(target, out var ip)) return null;
        try
        {
            using var ping = new Ping();
            // Windows IcmpSendEcho2 can report premature timeouts below 1000 ms.
            // See OpenJDK jdk-25-ga Inet4AddressImpl.c, ping4 (minimum timeout).
            // Keep real failures as gaps; do not retry or substitute old values.
            var result = await ping.SendPingAsync(ip, 1000).WaitAsync(token);
            return result.Status == IPStatus.Success ? result.RoundtripTime : null;
        }
        catch (PingException) { return null; }
        catch (SocketException) { return null; }
    }
    private static void Push(Queue<double?> samples, double? value) { samples.Enqueue(value); while (samples.Count > 30) samples.Dequeue(); }
    internal static PingView View(IEnumerable<double?> samples, string target)
    {
        var array = samples.ToArray();
        var differences = array.Zip(array.Skip(1)).Where(p => p.First.HasValue && p.Second.HasValue).Select(p => Math.Abs(p.Second!.Value - p.First!.Value)).ToArray();
        return new(array.LastOrDefault(), differences.Length == 0 ? null : differences.Average(), array.Length == 0 ? null : 100.0 * array.Count(x => x == null) / array.Length, array.Length, target);
    }
    internal sealed record Measurement(PingView Router, PingView External, double? Down, double? Up, string Adapter, string RouteId);
}
