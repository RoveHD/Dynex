namespace Dynex.Core;

public interface IPeerService : IDisposable, IAsyncDisposable
{
    event Action? PeersChanged;
    string? NetworkStatus { get; }
    PeerNetworkDiagnostics GetNetworkDiagnostics() => new("Unbekannt", [], []);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
    void Publish(bool isGaming, double? downloadMbps = null, double? latencyMs = null,
        double? appliedLimitMbps = null, string? game = null, bool shaperActive = false,
        double? idleBaselineMs = null, string? latencyTargetId = null, DateTimeOffset? idleBaselineAtUtc = null,
        double? uploadMbps = null, string? topProgram = null);
    IReadOnlyList<PeerAdvertisement> Snapshot();
}
