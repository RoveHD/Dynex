using System.Buffers.Binary;
using System.Net;
using Dynex.Mobile;

var checks = 0;
void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + reason);
    checks++; Console.WriteLine("PASS: " + reason);
}
bool Captured(string value)
{
    var address = IPAddress.Parse(value).GetAddressBytes();
    return PublicInternetRoutes.All.Any(route =>
    {
        var network = IPAddress.Parse(route.Address).GetAddressBytes();
        if (network.Length != address.Length) return false;
        for (var bit = 0; bit < route.PrefixLength; bit++)
            if (((network[bit / 8] ^ address[bit / 8]) & (1 << (7 - bit % 8))) != 0) return false;
        return true;
    });
}
foreach (var address in new[] { "1.1.1.1", "8.8.8.8", "9.9.9.9", "93.184.216.34", "223.255.255.254", "2001:4860:4860::8888", "2606:4700:4700::1111" })
    Check(Captured(address), "public internet route is captured: " + address);
foreach (var address in new[] { "0.0.0.0", "0.255.255.255", "10.0.0.0", "10.255.255.255", "100.64.0.0", "100.127.255.255",
    "127.0.0.1", "127.255.255.255", "169.254.0.0", "169.254.255.255", "172.16.0.0", "172.31.255.255", "192.168.0.0", "192.168.255.255",
    "192.0.0.1", "192.0.2.1", "198.18.0.1", "198.19.255.255", "198.51.100.1", "203.0.113.1", "224.0.0.0", "239.255.39.74",
    "240.0.0.0", "255.255.255.255", "::", "::1", "::ffff:192.168.1.1", "fe80::1", "fd00::1", "fc00::1", "ff02::1" })
    Check(!Captured(address), "LAN, multicast or special address stays outside TUN: " + address);
foreach (var address in new[] { "9.255.255.255", "11.0.0.0", "100.63.255.255", "100.128.0.0", "126.255.255.255", "128.0.0.0",
    "169.253.255.255", "169.255.0.0", "172.15.255.255", "172.32.0.0", "192.167.255.255", "192.169.0.0" })
    Check(Captured(address), "exclusion prefix does not swallow adjacent public networks: " + address);
var ranges = PublicInternetRoutes.All.Where(route => IPAddress.Parse(route.Address).GetAddressBytes().Length == 4).Select(route =>
{
    var start = (ulong)BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(route.Address).GetAddressBytes());
    return (Start: start, End: start + (1UL << (32 - route.PrefixLength)) - 1);
}).OrderBy(range => range.Start).ToArray();
Check(ranges.Zip(ranges.Skip(1)).All(pair => pair.First.End < pair.Second.Start), "generated IPv4 routes are canonical and do not overlap");
Check(PublicInternetRoutes.All.Count is > 1 and < 256 && PublicInternetRoutes.All.All(route => route.PrefixLength != 0),
    "split routes are bounded and contain no default route");
Check(!LocalSocksProxy.IsInternetDownload(new(IPAddress.Parse("8.8.8.8"), 53)) &&
    !LocalSocksProxy.IsInternetDownload(new(IPAddress.Parse("192.168.1.1"), 443)), "DNS and private LAN remain exempt from proxy download pacing");
Console.WriteLine($"{checks} public-route checks passed; no Android VPN or sockets started.");
