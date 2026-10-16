using System.Collections;
using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Dynex.Core;

/// <summary>One explicitly trusted active IPv4 Wi-Fi/LAN address, its on-link prefix and OS interface index.</summary>
public sealed record AutoLanNetworkBinding(IPAddress Address, int PrefixLength, int InterfaceIndex);

/// <summary>
/// Immutable, bounded scope supplied by a platform adapter. Supply only the app's
/// explicitly approved active network; this does not infer trust from an address.
/// An empty snapshot immediately closes discovery and acceptance on the next heartbeat.
/// </summary>
public sealed class AutoLanNetworkSnapshot
{
    private readonly AutoLanBinding[] _bindings;
    public static AutoLanNetworkSnapshot Empty { get; } = new([]);
    public int BindingCount => _bindings.Length;

    public AutoLanNetworkSnapshot(IEnumerable<AutoLanNetworkBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var supplied = bindings.Take(17).ToArray();
        if (supplied.Length > 16) throw new ArgumentException("Höchstens 16 AutoLAN-Netzwerkadressen sind erlaubt.", nameof(bindings));
        var validated = new List<AutoLanBinding>();
        foreach (var binding in supplied)
        {
            if (binding is null || binding.Address is null || binding.Address.AddressFamily != AddressFamily.InterNetwork ||
                binding.PrefixLength is < 8 or > 30 || binding.InterfaceIndex <= 0)
                throw new ArgumentException("AutoLAN benötigt eine private IPv4-Hostadresse, Präfix /8 bis /30 und einen positiven Interfaceindex.", nameof(bindings));
            var address = new IPAddress(binding.Address.GetAddressBytes());
            var value = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
            var mask = uint.MaxValue << (32 - binding.PrefixLength);
            var network = value & mask;
            var broadcast = network | ~mask;
            // The entire prefix must stay inside one RFC1918 block. A private
            // host with an overly broad mask must never grant public IP scope.
            var privatePrefix = value >> 24 == 10 && binding.PrefixLength >= 8 ||
                value >> 20 == 0xac1 && binding.PrefixLength >= 12 ||
                value >> 16 == 0xc0a8 && binding.PrefixLength >= 16;
            if (!privatePrefix || value == network || value == broadcast)
                throw new ArgumentException("AutoLAN-Netzwerkadresse oder Präfix liegt außerhalb eines privaten Hostnetzes.", nameof(bindings));
            var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, mask);
            var current = new AutoLanBinding(address, new(bytes), binding.InterfaceIndex);
            if (validated.Contains(current)) continue;
            if (validated.Any(previous => previous.Address.Equals(current.Address) ||
                previous.InterfaceIndex != current.InterfaceIndex && (previous.Contains(current.Address) || current.Contains(previous.Address))))
                throw new ArgumentException("Widersprüchliche AutoLAN-Adressen oder überlappende Schnittstellen.", nameof(bindings));
            validated.Add(current);
        }
        // OS adapters may enumerate identical addresses in a different order.
        // This must not masquerade as a scope change and restart mobile bursts.
        _bindings = validated.OrderBy(binding => binding.InterfaceIndex)
            .ThenBy(binding => BinaryPrimitives.ReadUInt32BigEndian(binding.Address.GetAddressBytes())).ToArray();
    }

    internal AutoLanBinding[] CopyBindings() => _bindings.Select(binding => new AutoLanBinding(
        new(binding.Address.GetAddressBytes()), new(binding.Mask.GetAddressBytes()), binding.InterfaceIndex)).ToArray();
}

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

    internal static (AutoLanBinding[] Bindings, string? Error) ReadProvided(Func<AutoLanNetworkSnapshot> provider)
    {
        try
        {
            var snapshot = provider();
            if (snapshot is null) return ([], "AutoLAN-Netzwerkfreigabe fehlt.");
            var bindings = snapshot.CopyBindings();
            return bindings.Length == 0 ? ([], "Keine aktive, explizit freigegebene AutoLAN-Netzwerkverbindung.") : (bindings, null);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return ([], "AutoLAN-Netzwerkfreigabe nicht verfügbar (" + error.GetType().Name + ")."); }
    }

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
