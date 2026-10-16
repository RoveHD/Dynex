using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Dynex.Core;
using global::Android.Content;
using global::Android.Net;

namespace Dynex.Android;

internal sealed record WifiConnection(Network Network, AutoLanNetworkSnapshot Scope, string Fingerprint,
    IReadOnlyList<string> DnsServers);
internal sealed record TransportConnection(Network Network, string Fingerprint, bool IsWifi);
internal sealed record MobileNetworkState(WifiConnection? Wifi, TransportConnection? Transport, bool OtherVpnActive);

/// <summary>Network callbacks only: no Wi-Fi scans, location requests or cellular polling.</summary>
internal sealed class WifiNetworkMonitor : IDisposable
{
    private readonly ConnectivityManager connectivity;
    private readonly Callback defaultCallback;
    private readonly Callback physicalCallback;
    private readonly object sync = new();
    private readonly object refreshGate = new();
    private WifiConnection? current;
    private TransportConnection? transport;
    private bool ownsVpn, allowExternalVpn, otherVpn, disposed;
    public event Action? Changed;
    public WifiConnection? Current { get { lock (sync) return current; } }
    public TransportConnection? Transport { get { lock (sync) return transport; } }
    public bool OtherVpnActive { get { lock (sync) return otherVpn && !ownsVpn; } }
    public MobileNetworkState Snapshot() { lock (sync) return new(current, transport, otherVpn && !ownsVpn); }

    public WifiNetworkMonitor(Context context, bool proxyMode)
    {
        allowExternalVpn = proxyMode;
        connectivity = (ConnectivityManager)context.GetSystemService(Context.ConnectivityService)!;
        defaultCallback = new Callback(this);
        physicalCallback = new Callback(this);
        connectivity.RegisterDefaultNetworkCallback(defaultCallback);
        using var request = new NetworkRequest.Builder().AddCapability(NetCapability.NotVpn)!.Build()!;
        connectivity.RegisterNetworkCallback(request, physicalCallback);
        Refresh();
    }

    public void SetOwnVpn(bool value)
    {
        lock (sync) { if (ownsVpn == value) return; ownsVpn = value; }
        Refresh();
    }
    public void SetProxyMode(bool value)
    {
        lock (sync) { if (allowExternalVpn == value) return; allowExternalVpn = value; }
        Refresh();
    }

    private void Refresh()
    {
        lock (refreshGate) RefreshCore();
    }

    private void RefreshCore()
    {
        WifiConnection? next = null;
        TransportConnection? nextTransport = null;
        var foundOtherVpn = false;
        lock (sync) if (disposed) return;
        try
        {
            bool owned, proxyMode;
            lock (sync) { owned = ownsVpn; proxyMode = allowExternalVpn; }
            var networks = connectivity.GetAllNetworks();
            foundOtherVpn = networks.Any(n => connectivity.GetNetworkCapabilities(n)?.HasTransport(TransportType.Vpn) == true);
            var active = connectivity.ActiveNetwork;
            var activeCaps = active is null ? null : connectivity.GetNetworkCapabilities(active);
            var activeIsVpn = activeCaps?.HasTransport(TransportType.Vpn) == true;
            Network? physical = null;
            if (activeCaps is not null && (!foundOtherVpn || owned || proxyMode))
            {
                if (!activeIsVpn) physical = active;
                else if (owned || proxyMode)
                {
                    // A VPN can advertise its underlying transports. Never prefer a merely
                    // connected Wi-Fi radio when cellular is in use or transport is ambiguous.
                    var cell = activeCaps.HasTransport(TransportType.Cellular);
                    var wifi = activeCaps.HasTransport(TransportType.Wifi);
                    if (cell != wifi)
                        physical = networks.FirstOrDefault(n =>
                        {
                            var caps = connectivity.GetNetworkCapabilities(n);
                            return caps is not null && !caps.HasTransport(TransportType.Vpn) &&
                                caps.HasTransport(cell ? TransportType.Cellular : TransportType.Wifi);
                        });
                }
            }
            if (physical is not null)
            {
                var caps = connectivity.GetNetworkCapabilities(physical);
                var links = connectivity.GetLinkProperties(physical);
                if (caps is not null && links?.InterfaceName is not null)
                {
                    var isWifi = caps.HasTransport(TransportType.Wifi) && !caps.HasTransport(TransportType.Cellular);
                    var fingerprint = physical.NetworkHandle + "|" + links.InterfaceName + "|" +
                        string.Join(";", links.LinkAddresses.Select(a => a.ToString())) + "|" +
                        string.Join(";", links.DnsServers.Select(a => a.HostAddress));
                    nextTransport = new(physical, fingerprint, isWifi);
                    if (isWifi)
                    {
                        var nic = Java.Net.NetworkInterface.GetByName(links.InterfaceName);
                        var bindings = links.LinkAddresses
                            .Select(a => (Address: IPAddress.Parse(a.Address!.HostAddress!.Split('%')[0]), Prefix: a.PrefixLength))
                            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                            .Select(a => new AutoLanNetworkBinding(a.Address, a.Prefix, nic?.Index ?? 0)).ToArray();
                        if (bindings.Length > 0)
                            next = new(physical, new AutoLanNetworkSnapshot(bindings), fingerprint,
                                links.DnsServers.Select(a => a.HostAddress!).ToArray());
                    }
                }
            }
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or ArgumentException or FormatException)
        { global::Android.Util.Log.Warn("DYNEX", "Network scope unavailable: " + ex.GetType().Name); }
        bool changed;
        lock (sync)
        {
            if (disposed) return;
            changed = current?.Fingerprint != next?.Fingerprint || transport?.Fingerprint != nextTransport?.Fingerprint || otherVpn != foundOtherVpn;
            current = next; transport = nextTransport; otherVpn = foundOtherVpn;
        }
        if (changed) Changed?.Invoke();
    }

    internal static void BindSocket(Network network, Socket socket)
    {
        if (android_setsocknetwork((ulong)network.NetworkHandle, socket.Handle.ToInt32()) != 0)
            throw new IOException("Die Verbindung kann nicht an das aktive Netz gebunden werden.");
    }

    internal static Task<IPAddress[]> ResolveAsync(Network network, string host, CancellationToken token) =>
        Task.Run(() => network.GetAllByName(host)!.Select(address =>
            IPAddress.Parse(address.HostAddress!.Split('%')[0])).ToArray(), token).WaitAsync(token);

    [DllImport("libandroid", SetLastError = true)]
    private static extern int android_setsocknetwork(ulong network, int socketFd);

    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; }
        connectivity.UnregisterNetworkCallback(defaultCallback);
        connectivity.UnregisterNetworkCallback(physicalCallback);
        defaultCallback.Dispose(); physicalCallback.Dispose();
    }

    private sealed class Callback(WifiNetworkMonitor owner) : ConnectivityManager.NetworkCallback
    {
        public override void OnAvailable(Network network) => owner.Refresh();
        public override void OnLost(Network network) => owner.Refresh();
        public override void OnCapabilitiesChanged(Network network, NetworkCapabilities capabilities) => owner.Refresh();
        public override void OnLinkPropertiesChanged(Network network, LinkProperties links) => owner.Refresh();
    }
}
