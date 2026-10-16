using System.Net;
using System.Net.Sockets;

namespace Dynex.Core;

public sealed partial class PeerService
{
    private ConnectionProbe? _connectionProbe;
    private HashSet<Guid> _excludedDevices = [];
    private volatile bool _deviceNetworkEnabled = true;
    private bool IsExcluded(Guid id) => !_deviceNetworkEnabled || Volatile.Read(ref _excludedDevices).Contains(id);
    public void SetNetworkAccessEnabled(bool enabled)
    {
        lock (_gate) { _deviceNetworkEnabled = enabled; if (!enabled) _peers.Clear(); }
        Notify();
    }
    public void SetExcludedDevices(IReadOnlyCollection<Guid> deviceIds)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        lock (_gate)
        {
            Volatile.Write(ref _excludedDevices, deviceIds.ToHashSet());
            foreach (var id in deviceIds) _peers.Remove(id);
        }
        Notify();
    }
    public Task<PeerConnectionTestResult> TestConnectionAsync(Guid deviceId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        IPEndPoint? endpoint;
        lock (_gate)
        {
            ExpireLocked(System.Diagnostics.Stopwatch.GetTimestamp());
            endpoint = _peers.GetValueOrDefault(deviceId)?.Endpoint;
        }
        return endpoint is null ? Task.FromResult(PeerConnectionTestResult.Unavailable(deviceId, "Kein authentifizierter Ziel-Endpunkt bekannt.")) :
            TestConnectionAsync(deviceId, endpoint, timeout, cancellationToken);
    }
    public Task<PeerConnectionTestResult> TestConnectionAsync(Guid deviceId, IPEndPoint lastAuthenticatedEndpoint, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var socket = _socket;
        if (socket is null || IsExcluded(deviceId) || deviceId == Guid.Empty || deviceId == _settings.DeviceId ||
            lastAuthenticatedEndpoint.Port is < 1 or > 65535 || !LanAddress.IsAllowed(lastAuthenticatedEndpoint.Address, _settings.LoopbackOnly))
            return Task.FromResult(PeerConnectionTestResult.Unavailable(deviceId, "Lokaler Dienst gestoppt, Gerät ausgeschlossen oder Ziel außerhalb des erlaubten LAN-Bereichs."));
        var endpoint = new IPEndPoint(lastAuthenticatedEndpoint.Address, lastAuthenticatedEndpoint.Port);
        return _connectionProbe!.TestAsync(deviceId, endpoint, timeout,
            async (packet, target, token) => { if (IsExcluded(deviceId)) throw new InvalidOperationException("Gerät inzwischen ausgeschlossen."); await socket.SendToAsync(packet.AsMemory(), SocketFlags.None, target, token).ConfigureAwait(false); }, cancellationToken);
    }
}

public sealed partial class AutoLanPeerService
{
    private ConnectionProbe? _connectionProbe;
    private HashSet<Guid> _excludedDevices = [];
    private volatile bool _deviceNetworkEnabled = true;
    private bool IsExcluded(Guid id) => !_deviceNetworkEnabled || Volatile.Read(ref _excludedDevices).Contains(id);
    public void SetNetworkAccessEnabled(bool enabled)
    {
        lock (_gate) { _deviceNetworkEnabled = enabled; if (!enabled) _peers.Clear(); }
        Notify();
    }
    public void SetExcludedDevices(IReadOnlyCollection<Guid> deviceIds)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        lock (_gate)
        {
            Volatile.Write(ref _excludedDevices, deviceIds.ToHashSet());
            foreach (var id in deviceIds) { _peers.Remove(id); _versions.Remove(id); _names.Remove(id); }
            // Preserve dormant sequence state until normal expiry; unblocking cannot revive captured ciphertext.
            // Excluded channels never send, receive, count as routes, or become test targets.
        }
        Notify();
    }
    public Task<PeerConnectionTestResult> TestConnectionAsync(Guid deviceId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        IPEndPoint? endpoint;
        lock (_gate)
        {
            ExpireLocked(System.Diagnostics.Stopwatch.GetTimestamp());
            endpoint = _channels.GetValueOrDefault(deviceId)?.Endpoint;
        }
        return endpoint is null ? Task.FromResult(PeerConnectionTestResult.Unavailable(deviceId, "Kein authentifizierter Ziel-Endpunkt bekannt.")) :
            TestConnectionAsync(deviceId, endpoint, timeout, cancellationToken);
    }
    public Task<PeerConnectionTestResult> TestConnectionAsync(Guid deviceId, IPEndPoint lastAuthenticatedEndpoint, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var socket = _socket;
        if (socket is null || IsExcluded(deviceId) || deviceId == Guid.Empty || deviceId == _settings.DeviceId ||
            lastAuthenticatedEndpoint.Port is < 1 or > 65535 || !EndpointAllowed(lastAuthenticatedEndpoint.Address))
            return Task.FromResult(PeerConnectionTestResult.Unavailable(deviceId, "Lokaler Dienst gestoppt, Gerät ausgeschlossen oder Ziel außerhalb des privaten Netzbereichs."));
        var endpoint = new IPEndPoint(lastAuthenticatedEndpoint.Address, lastAuthenticatedEndpoint.Port);
        return _connectionProbe!.TestAsync(deviceId, endpoint, timeout,
            async (packet, target, token) => { if (IsExcluded(deviceId) || !EndpointAllowed(target.Address)) throw new InvalidOperationException("Gerät oder Netzwerk inzwischen ausgeschlossen."); await socket.SendToAsync(packet.AsMemory(), SocketFlags.None, target, token).ConfigureAwait(false); }, cancellationToken);
    }
}
