using System.Collections;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Dynex.Desktop;

internal static class NetworkIdentityProbe
{
    // Windows' network GUID survives address changes and does not confuse two networks with the same SSID/gateway IP.
    // https://learn.microsoft.com/windows/win32/api/netlistmgr/nf-netlistmgr-inetwork-getnetworkid
    internal static NetworkIdentity? Read()
    {
        object? manager = null, networks = null;
        try
        {
            using var route = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            route.Connect(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 443)); // Route selection, no packet sent.
            var address = ((IPEndPoint)route.LocalEndPoint!).Address;
            var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
                n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(address)));
            if (adapter is null || !Guid.TryParse(adapter.Id, out var adapterId)) return null;
            manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"), true)!);
            networks = ((dynamic)manager!).GetNetworks(1);
            var matches = new List<NetworkIdentity>();
            foreach (var network in (IEnumerable)networks)
            {
                object? connections = null;
                try
                {
                    connections = ((dynamic)network).GetNetworkConnections();
                    foreach (var connection in (IEnumerable)connections)
                    {
                        try
                        {
                            if (((INetworkConnection)connection).GetAdapterId() != adapterId) continue;
                            var typed = (INetwork)network;
                            var id = typed.GetNetworkId();
                            var name = new string(typed.GetName().Where(c => !char.IsControl(c)).Take(128).ToArray()).Trim();
                            if (id != Guid.Empty) matches.Add(new(id.ToString("D"), name.Length > 0 ? name : "Windows-Netzwerk"));
                        }
                        finally { Release(connection); }
                    }
                }
                finally { Release(connections); Release(network); }
            }
            var unique = matches.DistinctBy(n => n.Id).ToArray();
            return unique.Length == 1 ? unique[0] : null;
        }
        catch (Exception ex) when (ex is COMException or SocketException or NetworkInformationException or
            InvalidOperationException or InvalidCastException or ArgumentException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { return null; }
        finally { Release(networks); Release(manager); }
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
        void SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
    }
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
