using global::Android.Content;
using global::Android.Net;

namespace Dynex.Android;

public static class VpnCompatibility
{
    private static int ownVpn;
    public static bool OwnVpnActive => Volatile.Read(ref ownVpn) != 0;
    internal static void SetOwnVpn(bool value) => Volatile.Write(ref ownVpn, value ? 1 : 0);

    public static bool HasOtherActiveVpn(Context context)
    {
        if (OwnVpnActive) return false;
        var manager = (ConnectivityManager)context.GetSystemService(Context.ConnectivityService)!;
        return manager.GetAllNetworks().Any(network =>
            manager.GetNetworkCapabilities(network)?.HasTransport(TransportType.Vpn) == true);
    }
}
