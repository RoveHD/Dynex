using System.Collections;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Dynex.Core;

internal sealed record AutoLanBinding(IPAddress Address, IPAddress Mask, int InterfaceIndex)
{
    internal bool Contains(IPAddress remote)
    {
        if (remote.AddressFamily != AddressFamily.InterNetwork) return false;
        var a = Address.GetAddressBytes(); var m = Mask.GetAddressBytes(); var r = remote.GetAddressBytes();
        for (var i = 0; i < 4; i++) if ((a[i] & m[i]) != (r[i] & m[i])) return false;
        return true;
    }
    internal IPAddress Broadcast
    {
        get
        {
            var a = Address.GetAddressBytes(); var m = Mask.GetAddressBytes();
            return new IPAddress(a.Select((b, i) => (byte)(b | ~m[i])).ToArray());
        }
    }
}

internal static class AutoLanNetworkScope
{
    internal static (AutoLanBinding[] Bindings, string? Error) Read()
    {
        if (!OperatingSystem.IsWindows()) return ([], "AutoLAN benötigt Windows-Privatnetzprofile; außerhalb Windows nur expliziter Loopback-Testmodus.");
        try
        {
            var trusted = GetPrivateAdapters();
            var bindings = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Tunnel or NetworkInterfaceType.Loopback) &&
                    Guid.TryParse(n.Id, out var id) && trusted.Contains(id))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.PrefixLength is >= 8 and <= 30 && LanAddress.IsAllowed(a.Address, false))
                    .Select(a => new AutoLanBinding(a.Address, a.IPv4Mask, n.GetIPProperties().GetIPv4Properties().Index)))
                .Distinct().Take(16).ToArray();
            return (bindings, bindings.Length == 0 ? "AutoLAN wartet auf ein aktives privates Windows-IPv4-LAN. Öffentliche/ unbekannte Profile werden ausgeschlossen." : null);
        }
        catch (Exception e) when (e is COMException or NetworkInformationException or InvalidOperationException or InvalidCastException or ArgumentException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { return ([], "AutoLAN kann private Windows-Netzprofile nicht prüfen: " + e.Message); }
    }

    [SupportedOSPlatform("windows")]
    private static HashSet<Guid> GetPrivateAdapters()
    {
        var adapters = new HashSet<Guid>();
        var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"), true)!;
        dynamic manager = Activator.CreateInstance(type)!;
        object? networks = null;
        try
        {
            networks = manager.GetNetworks(1); // NLM_ENUM_NETWORK_CONNECTED
            foreach (dynamic network in (IEnumerable)networks)
            {
                object? connections = null;
                try
                {
                    if ((int)network.GetCategory() != 1) continue; // NLM_NETWORK_CATEGORY_PRIVATE only
                    connections = network.GetNetworkConnections();
                    foreach (dynamic connection in (IEnumerable)connections)
                        try { adapters.Add(((INetworkConnection)connection).GetAdapterId()); }
                        finally { Release(connection); }
                }
                finally { Release(connections); Release(network); }
            }
        }
        finally { Release(networks); Release(manager); }
        return adapters;
    }

    [SupportedOSPlatform("windows")]
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    // GUID return values are not reliably marshalled through dynamic IDispatch on .NET.
    // Vtable order follows Microsoft's netlistmgr.h INetworkConnection declaration.
    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetwork();
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
        int GetDomainType();
    }
}
