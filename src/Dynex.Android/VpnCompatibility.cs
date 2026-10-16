using global::Android.Content;
using global::Android.Net;

namespace Dynex.Android;

public static class VpnCompatibility
{
    private static int ownVpn;
    public static bool OwnVpnActive => Volatile.Read(ref ownVpn) != 0;
    internal static void SetOwnVpn(bool value) => Volatile.Write(ref ownVpn, value ? 1 : 0);

    // ActiveNetwork is the calling application's default network, not just any
    // VPN on the device. Physical means only current bypass, never setup success.
    public static AppVpnRoute ReadAppRoute(Context context)
    {
        if (OwnVpnActive) return AppVpnRoute.Unknown;
        try
        {
            var manager = (ConnectivityManager)context.GetSystemService(Context.ConnectivityService)!;
            var active = manager.ActiveNetwork;
            var caps = active is null ? null : manager.GetNetworkCapabilities(active);
            if (caps is null) return AppVpnRoute.Unknown;
            if (caps.HasTransport(TransportType.Vpn)) return AppVpnRoute.Vpn;
            return caps.HasTransport(TransportType.Wifi) || caps.HasTransport(TransportType.Cellular) || caps.HasTransport(TransportType.Ethernet)
                ? AppVpnRoute.Physical : AppVpnRoute.Unknown;
        }
        catch (Java.Lang.Exception) { return AppVpnRoute.Unknown; }
    }

    public static bool HasOtherActiveVpn(Context context)
    {
        if (OwnVpnActive) return false;
        var manager = (ConnectivityManager)context.GetSystemService(Context.ConnectivityService)!;
        return manager.GetAllNetworks().Any(network =>
            manager.GetNetworkCapabilities(network)?.HasTransport(TransportType.Vpn) == true);
    }
}
