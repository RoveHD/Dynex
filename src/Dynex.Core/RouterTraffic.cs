using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml;

namespace Dynex.Core;

public sealed record RouterTrafficSample(double DownloadMbps, double UploadMbps, string RouterName,
    string? Manufacturer = null, string? Model = null);

/// <summary>
/// Read-only whole-home throughput from the router's UPnP IGD counters (WANCommonInterfaceConfig).
/// Covers consoles, phones and TVs that do not run DYNEX. Only private LAN routers are contacted,
/// nothing is configured, and routers with UPnP status disabled simply report nothing.
/// </summary>
public sealed class RouterTrafficMonitor : IDisposable
{
    private const string Service = "urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1";
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly Func<RouterRoute, CancellationToken, Task<(Uri Location, IPAddress Source)?>> _discover;
    private readonly Func<RouterRoute?> _getRoute;
    private RouterRoute? _route;
    private (Uri Control, string Name, string? Manufacturer, string? Model)? _router;
    private long _nextDiscoveryTick;
    private (long Received, long Sent, long Tick)? _last;

    public RouterTrafficMonitor() : this(new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) }, true, DiscoverAsync, GetActiveRoute) { }

    internal RouterTrafficMonitor(HttpClient http, bool ownsClient,
        Func<RouterRoute, CancellationToken, Task<(Uri Location, IPAddress Source)?>> discover, Func<RouterRoute?> getRoute)
    {
        _http = http; _ownsClient = ownsClient; _discover = discover; _getRoute = getRoute;
    }

    /// <summary>Invalidate router identity and counter history. Call only after any pending sample has finished.</summary>
    public void Reset()
    {
        _router = null;
        _last = null;
        _route = null;
        _nextDiscoveryTick = 0;
    }

    /// <summary>Null until two counter readings exist, or when no supporting router answers.</summary>
    public async Task<RouterTrafficSample?> SampleAsync(CancellationToken token)
    {
        try
        {
            var route = _getRoute();
            if (route != _route)
            {
                Reset();
                _route = route;
            }
            // A UPnP responder is not evidence of the Internet route: switches/APs can advertise IGD too.
            if (route is null) return null;
            if (_router is null)
            {
                if (Stopwatch.GetTimestamp() < _nextDiscoveryTick) return null;
                _nextDiscoveryTick = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 300; // Retry discovery every 5 minutes.
                if (await _discover(route, token).ConfigureAwait(false) is not { } found ||
                    !found.Source.Equals(route.Gateway) || !IsRouterLocation(found.Location, found.Source)) return null;
                var description = await GetBoundedAsync(found.Location, token).ConfigureAwait(false);
                if (ParseControlUrl(description, found.Location) is not { } control) return null;
                _router = (control, ParseFriendlyName(description) ?? found.Location.Host,
                    ParseElement(description, "manufacturer"), ParseElement(description, "modelName"));
                _last = null;
            }
            var (url, name, manufacturer, model) = _router.Value;
            var received = await QueryAsync(url, "GetTotalBytesReceived", "NewTotalBytesReceived", token).ConfigureAwait(false);
            var sent = await QueryAsync(url, "GetTotalBytesSent", "NewTotalBytesSent", token).ConfigureAwait(false);
            var now = Stopwatch.GetTimestamp();
            if (received is null || sent is null) { _router = null; return null; }
            var previous = _last;
            _last = (received.Value, sent.Value, now);
            if (previous is not { } p) return null;
            var seconds = (now - p.Tick) / (double)Stopwatch.Frequency;
            if (seconds < 0.5) return null;
            return new(Rate(p.Received, received.Value, seconds), Rate(p.Sent, sent.Value, seconds), name, manufacturer, model);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or XmlException or SocketException or IOException)
        {
            if (token.IsCancellationRequested) throw;
            _router = null;
            return null;
        }
    }

    /// <summary>Many IGDs expose 32-bit counters; a smaller reading means a wrap (or a router restart).</summary>
    internal static double Rate(long before, long after, double seconds)
    {
        var delta = after >= before ? after - before : before <= uint.MaxValue ? (uint.MaxValue - before) + after + 1 : 0;
        return Math.Round(delta * 8 / seconds / 1_000_000, 2);
    }

    private async Task<long?> QueryAsync(Uri control, string action, string element, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, control)
        {
            Content = new StringContent(
                $"<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:{action} xmlns:u=\"{Service}\"/></s:Body></s:Envelope>",
                Encoding.UTF8, "text/xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{Service}#{action}\"");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return ParseCounter(await ReadBoundedAsync(response, token).ConfigureAwait(false), element);
    }

    private async Task<string> GetBoundedAsync(Uri uri, CancellationToken token)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadBoundedAsync(response, token).ConfigureAwait(false);
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
    {
        const int limit = 256 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new IOException("Routerantwort zu groß.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit) throw new IOException("Routerantwort zu groß.");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static Uri? ParseSsdpLocation(string response, IPAddress source, IPAddress? gateway)
    {
        if (gateway is null || !source.Equals(gateway)) return null;
        foreach (var line in response.Split("\r\n"))
        {
            if (!line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase)) continue;
            // Only plain HTTP on the private address that actually answered; never follow a redirect elsewhere.
            return Uri.TryCreate(line[9..].Trim(), UriKind.Absolute, out var uri) && IsRouterLocation(uri, source) ? uri : null;
        }
        return null;
    }

    private static bool IsRouterLocation(Uri uri, IPAddress source) => uri.Scheme == Uri.UriSchemeHttp &&
        IPAddress.TryParse(uri.Host, out var host) && host.Equals(source) && LanAddress.IsAllowed(host, false);

    internal sealed record RouterRoute(IPAddress Local, IPAddress Gateway);

    internal static IPAddress? SelectGateway(IEnumerable<IPAddress> addresses)
    {
        var gateways = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any)).Distinct().ToArray();
        return gateways.Length == 1 && LanAddress.IsAllowed(gateways[0], false) ? gateways[0] : null;
    }

    private static RouterRoute? GetActiveRoute()
    {
        try
        {
            // Let the OS choose the outgoing interface. UDP connect does not send a datagram.
            // Never fall back to the first adapter: that can be an unused LAN or VPN interface.
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 443));
            if (socket.LocalEndPoint is not IPEndPoint local) return null;
            var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(local.Address)));
            var gateway = adapter is null ? null : SelectGateway(adapter.GetIPProperties().GatewayAddresses.Select(g => g.Address));
            return gateway is null ? null : new(local.Address, gateway);
        }
        catch (Exception ex) when (ex is SocketException or NetworkInformationException) { return null; }
    }

    private static XmlDocument? Load(string xml)
    {
        var document = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000 });
        document.Load(reader);
        return document;
    }

    internal static Uri? ParseControlUrl(string descriptionXml, Uri location)
    {
        var document = Load(descriptionXml);
        foreach (XmlNode service in document!.GetElementsByTagName("service"))
        {
            string? Child(string name) => service.ChildNodes.Cast<XmlNode>().FirstOrDefault(n => n.LocalName == name)?.InnerText.Trim();
            if (Child("serviceType") != Service || Child("controlURL") is not { Length: > 0 and < 512 } path) continue;
            if (!Uri.TryCreate(location, path, out var control) || control.Scheme != Uri.UriSchemeHttp || control.Host != location.Host) return null;
            return control;
        }
        return null;
    }

    internal static string? ParseFriendlyName(string descriptionXml) => ParseElement(descriptionXml, "friendlyName");

    internal static string? ParseElement(string descriptionXml, string element)
    {
        var value = Load(descriptionXml)!.GetElementsByTagName(element).Cast<XmlNode>().FirstOrDefault()?.InnerText.Trim();
        return value is { Length: > 0 and <= 80 } && !value.Any(char.IsControl) ? value : null;
    }

    internal static long? ParseCounter(string soapXml, string element)
    {
        var value = Load(soapXml)!.GetElementsByTagName(element).Cast<XmlNode>().FirstOrDefault()?.InnerText.Trim();
        return long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var counter) ? counter : null;
    }

    /// <summary>Distinct LAN devices answering an SSDP search, excluding this PC and the given router.</summary>
    public static async Task<int?> CountLanDevicesAsync(IPAddress? router, CancellationToken token)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            var search = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: ssdp:all\r\n\r\n");
            await udp.SendAsync(search, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), token).ConfigureAwait(false);
            var local = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).ToHashSet();
            var devices = new HashSet<IPAddress>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                while (devices.Count < 256)
                {
                    var result = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    var source = result.RemoteEndPoint.Address;
                    if (LanAddress.IsAllowed(source, false) && !local.Contains(source) && !source.Equals(router)) devices.Add(source);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            return devices.Count;
        }
        catch (SocketException) { return null; }
    }

    private static async Task<(Uri Location, IPAddress Source)?> DiscoverAsync(RouterRoute route, CancellationToken token)
    {
        using var udp = new UdpClient(new IPEndPoint(route.Local, 0));
        var search = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n" +
            "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");
        await udp.SendAsync(search, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), token).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            while (true)
            {
                var result = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (result.Buffer.Length > 4096) continue;
                if (ParseSsdpLocation(Encoding.ASCII.GetString(result.Buffer), result.RemoteEndPoint.Address, route.Gateway) is { } location)
                    return (location, result.RemoteEndPoint.Address);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
