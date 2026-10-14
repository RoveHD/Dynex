using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Dynex.Shaper;

public static class InternetFilter
{
    public static string Build(IEnumerable<(IPAddress Address, int PrefixLength)> localPrefixes)
    {
        var excludes = new List<string>
        {
            // RFC1918, loopback, shared carrier space, link-local, multicast,
            // reserved/broadcast. ICMP (including IPv6 discovery) is never captured.
            Range("ip.SrcAddr", IPAddress.Parse("0.0.0.0"), 8),
            Range("ip.SrcAddr", IPAddress.Parse("10.0.0.0"), 8),
            Range("ip.SrcAddr", IPAddress.Parse("100.64.0.0"), 10),
            Range("ip.SrcAddr", IPAddress.Parse("127.0.0.0"), 8),
            Range("ip.SrcAddr", IPAddress.Parse("169.254.0.0"), 16),
            Range("ip.SrcAddr", IPAddress.Parse("172.16.0.0"), 12),
            Range("ip.SrcAddr", IPAddress.Parse("192.168.0.0"), 16),
            Range("ip.SrcAddr", IPAddress.Parse("224.0.0.0"), 3),
            Range("ipv6.SrcAddr", IPAddress.Parse("::"), 128),
            Range("ipv6.SrcAddr", IPAddress.Parse("::1"), 128),
            Range("ipv6.SrcAddr", IPAddress.Parse("fc00::"), 7),
            Range("ipv6.SrcAddr", IPAddress.Parse("fe80::"), 10),
            Range("ipv6.SrcAddr", IPAddress.Parse("ff00::"), 8),
            "ip.DstAddr >= 224.0.0.0", "ipv6.DstAddr >= ff00::"
        };
        foreach (var (address, prefix) in localPrefixes)
        {
            if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) continue;
            // A /0 on a tunnel is a default route, never an on-link LAN exemption.
            if (prefix <= 0) continue;
            excludes.Add(Range(address.AddressFamily == AddressFamily.InterNetwork ? "ip.SrcAddr" : "ipv6.SrcAddr", address, prefix));
        }
        var local = string.Join(" or ", excludes.Distinct(StringComparer.Ordinal));
        const string exemptPorts = "53,67,68,123,137,138,1900,3702,5353,5355,546,547";
        var ports = exemptPorts.Split(',').Select(p => $"udp.SrcPort == {p} or udp.DstPort == {p}");
        // TCP ACK/control packets have no download payload and must not be held.
        return $"inbound and !loopback and !impostor and ((tcp and tcp.PayloadLength > 0 and tcp.SrcPort != 53 and tcp.DstPort != 53) or (udp and !({string.Join(" or ", ports)}))) and !({local})";
    }

    public static string ForCurrentNetwork() => Build(NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => (a.Address, a.PrefixLength)));

    public static string Range(string field, IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        if (prefix < 0 || prefix > bytes.Length * 8) throw new ArgumentOutOfRangeException(nameof(prefix));
        var low = (byte[])bytes.Clone();
        var high = (byte[])bytes.Clone();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            var mask = (byte)(255 << (8 - bits));
            low[i] &= mask;
            high[i] |= (byte)~mask;
        }
        return $"({field} >= {new IPAddress(low)} and {field} <= {new IPAddress(high)})";
    }
}
