using System.Buffers.Binary;
using System.Net;

namespace Dynex.Mobile;

public readonly record struct InternetRoute(string Address, int PrefixLength);

/// <summary>Split routes keep LAN, discovery, loopback and reserved IPv4 outside a mobile TUN.</summary>
public static class PublicInternetRoutes
{
    public static IReadOnlyList<InternetRoute> All { get; } = Build();

    private static IReadOnlyList<InternetRoute> Build()
    {
        // Exclude special-purpose ranges as well as RFC1918. No default route
        // captures LAN traffic; IPv6 is limited to global-unicast 2000::/3.
        var excluded = new[] { "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8",
            "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24",
            "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4" }
            .Select(value =>
            {
                var parts = value.Split('/');
                var start = (ulong)BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(parts[0]).GetAddressBytes());
                return (Start: start, End: start + (1UL << (32 - int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture))) - 1);
            }).ToArray();
        var routes = new List<InternetRoute>();
        void Add(ulong start, int prefix)
        {
            var end = start + (1UL << (32 - prefix)) - 1;
            if (excluded.Any(range => range.Start <= start && range.End >= end)) return;
            if (excluded.Any(range => range.Start <= end && range.End >= start))
            {
                Add(start, prefix + 1);
                Add(start + (1UL << (31 - prefix)), prefix + 1);
                return;
            }
            var address = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(address, (uint)start);
            routes.Add(new(new IPAddress(address).ToString(), prefix));
        }
        Add(0, 0);
        routes.Add(new("2000::", 3));
        return routes.AsReadOnly();
    }
}
