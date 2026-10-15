using System.Net;
using System.Text;
using Dynex.Core;

internal static class RouterTrafficTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        var router = IPAddress.Parse("192.168.178.1");
        var ok = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nLOCATION: http://192.168.178.1:49000/igddesc.xml\r\nST: x\r\n\r\n";
        check(RouterTrafficMonitor.ParseSsdpLocation(ok, router)?.ToString() == "http://192.168.178.1:49000/igddesc.xml", "SSDP location from the answering router is accepted");
        check(RouterTrafficMonitor.ParseSsdpLocation(ok, IPAddress.Parse("192.168.178.9")) is null, "location pointing at another host is rejected");
        check(RouterTrafficMonitor.ParseSsdpLocation(ok.Replace("192.168.178.1:49000", "8.8.8.8"), IPAddress.Parse("8.8.8.8")) is null, "public router addresses are rejected");
        check(RouterTrafficMonitor.ParseSsdpLocation(ok.Replace("http://", "https://"), router) is null, "only plain LAN HTTP descriptions are read");
        var description = """<?xml version="1.0"?><root><device><friendlyName>FRITZ!Box 7590</friendlyName><serviceList><service><serviceType>urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1</serviceType><controlURL>/igdupnp/control/WANCommonIFC1</controlURL></service></serviceList></device></root>""";
        var location = new Uri("http://192.168.178.1:49000/igddesc.xml");
        check(RouterTrafficMonitor.ParseControlUrl(description, location)?.ToString() == "http://192.168.178.1:49000/igdupnp/control/WANCommonIFC1" &&
            RouterTrafficMonitor.ParseFriendlyName(description) == "FRITZ!Box 7590", "IGD description yields control URL and router name");
        check(RouterTrafficMonitor.ParseControlUrl(description.Replace("/igdupnp/control/WANCommonIFC1", "http://10.0.0.5/x"), location) is null, "control URL on another host is rejected");
        try { RouterTrafficMonitor.ParseControlUrl("<!DOCTYPE x [<!ENTITY e \"a\">]><root>&e;</root>", location); check(false, "DTD accepted"); }
        catch (System.Xml.XmlException) { check(true, "DTDs in router XML are rejected"); }
        check(RouterTrafficMonitor.ParseCounter("<s:Envelope xmlns:s=\"x\"><s:Body><u:R xmlns:u=\"y\"><NewTotalBytesReceived>123456</NewTotalBytesReceived></u:R></s:Body></s:Envelope>", "NewTotalBytesReceived") == 123456,
            "SOAP counter is parsed");
        check(RouterTrafficMonitor.Rate(1_000_000, 3_500_000, 2) == 10 && RouterTrafficMonitor.Rate(uint.MaxValue - 499_999, 2_000_000, 2) == 10,
            "rates handle 32-bit counter wrap");

        // End to end against a fake router.
        long received = 0, sent = 0;
        var handler = new FakeRouter(description, () => (received, sent));
        using var monitor = new RouterTrafficMonitor(new HttpClient(handler), true, _ => Task.FromResult<(Uri, IPAddress)?>((location, router)));
        check(await monitor.SampleAsync(CancellationToken.None) is null, "first reading establishes the counter baseline");
        await Task.Delay(600);
        received += 6_000_000; sent += 600_000;
        var sample = await monitor.SampleAsync(CancellationToken.None);
        check(sample is { RouterName: "FRITZ!Box 7590" } && sample.DownloadMbps > 40 && sample.DownloadMbps < 90 && sample.UploadMbps < sample.DownloadMbps,
            "second reading reports whole-home download/upload rates");
        check(handler.Actions.Contains("GetTotalBytesReceived") && handler.Actions.Contains("GetTotalBytesSent"), "only read-only counter actions are invoked");
    }

    private sealed class FakeRouter(string description, Func<(long Received, long Sent)> counters) : HttpMessageHandler
    {
        public readonly List<string> Actions = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(description, Encoding.UTF8, "text/xml") });
            var action = request.Headers.GetValues("SOAPAction").Single().Split('#')[1].TrimEnd('"');
            Actions.Add(action);
            var (rx, tx) = counters();
            var (element, value) = action == "GetTotalBytesReceived" ? ("NewTotalBytesReceived", rx) : ("NewTotalBytesSent", tx);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent($"<s:Envelope xmlns:s=\"x\"><s:Body><u:R xmlns:u=\"y\"><{element}>{value}</{element}></u:R></s:Body></s:Envelope>") });
        }
    }
}
