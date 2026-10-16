namespace Dynex.Core;

public interface IPeerService : IDisposable, IAsyncDisposable
{
    event Action? PeersChanged;
    string? NetworkStatus { get; }
    PeerNetworkDiagnostics GetNetworkDiagnostics() => new("Unbekannt", [], []);
    /// <summary>Request another bounded discovery pass without restarting authenticated sessions.</summary>
    void RequestDiscovery() { }
    /// <summary>Locally ignored/blocked identities cannot establish routes or participate in budgets.</summary>
    void SetExcludedDevices(IReadOnlyCollection<Guid> deviceIds) { }
    /// <summary>Fail closed when persistent exclusion policy cannot be read.</summary>
    void SetNetworkAccessEnabled(bool enabled) { }
    Task<PeerConnectionTestResult> TestConnectionAsync(Guid deviceId, System.Net.IPEndPoint lastAuthenticatedEndpoint, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult(PeerConnectionTestResult.Unavailable(deviceId, "Dieser Peer-Dienst unterstützt keine Offline-Zielprobe."));
    Task<PeerConnectionTestResult> TestConnectionAsync(Guid deviceId, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult(PeerConnectionTestResult.Unavailable(deviceId, "Dieser Peer-Dienst unterstützt keinen gezielten Verbindungstest."));
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
    void Publish(bool isGaming, double? downloadMbps = null, double? latencyMs = null,
        double? appliedLimitMbps = null, string? game = null, bool shaperActive = false,
        double? idleBaselineMs = null, string? latencyTargetId = null, DateTimeOffset? idleBaselineAtUtc = null,
        double? uploadMbps = null, string? topProgram = null);
    IReadOnlyList<PeerAdvertisement> Snapshot();
}
