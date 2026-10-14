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
    internal sealed record Candidate(Guid? AdapterId, string Name, AutoLanBinding Binding);

    internal static (AutoLanBinding[] Bindings, string? Error) Read()
    {
        if (!OperatingSystem.IsWindows()) return ([], "AutoLAN benötigt Windows-Privatnetzprofile; außerhalb Windows nur expliziter Loopback-Testmodus.");
        try
        {
            var profiles = GetAdapterProfiles();
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Tunnel or NetworkInterfaceType.Loopback))
                .SelectMany(n =>
                {
                    var properties = n.GetIPProperties();
                    var id = Guid.TryParse(n.Id, out var parsed) ? parsed : (Guid?)null;
                    return properties.UnicastAddresses
                        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.PrefixLength is >= 8 and <= 30 && LanAddress.IsAllowed(a.Address, false))
                        .Select(a => new Candidate(id, n.Name, new(a.Address, a.IPv4Mask, properties.GetIPv4Properties().Index)));
                }).ToArray();
            return Select(candidates, profiles);
        }
        catch (Exception e) when (e is COMException or NetworkInformationException or InvalidOperationException or InvalidCastException or ArgumentException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { return ([], "AutoLAN kann private Windows-Netzprofile nicht prüfen: " + e.Message); }
    }

    internal static (AutoLanBinding[] Bindings, string? Error) Select(Candidate[] candidates, IReadOnlyDictionary<Guid, int> profiles)
    {
        int Category(Candidate candidate) => candidate.AdapterId is { } id && profiles.TryGetValue(id, out var category) ? category : -1;
        var bindings = candidates.Where(c => Category(c) == 1).Select(c => c.Binding).Distinct().Take(16).ToArray();
        if (bindings.Length != 0) return (bindings, null);
        if (candidates.Length == 0) return ([], "Keine aktive LAN-Verbindung mit privater IPv4-Adresse gefunden. Ethernet oder WLAN verbinden.");
        var details = candidates.DistinctBy(c => c.AdapterId).Take(4).Select(c => $"{c.Name}: {Category(c) switch
        {
            0 => "öffentliches Windows-Netzwerk",
            2 => "Windows-Domänennetzwerk",
            _ => "Windows-Netzwerkprofil unbekannt"
        }}");
        return ([], "Automatische PC-Verbindung nicht verfügbar. " + string.Join("; ", details) + ". DYNEX verbindet PCs nur in einem privaten Windows-Netzwerk.");
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<Guid, int> GetAdapterProfiles()
    {
        var adapters = new Dictionary<Guid, int>();
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
                    var category = (int)network.GetCategory();
                    connections = network.GetNetworkConnections();
                    foreach (dynamic connection in (IEnumerable)connections)
                        try
                        {
                            var id = ((INetworkConnection)connection).GetAdapterId();
                            // Ambiguous profiles must not grant private-network trust.
                            adapters[id] = adapters.TryGetValue(id, out var previous) && previous != category ? -1 : category;
                        }
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
