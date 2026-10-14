using System.Net;
using System.Security.Cryptography;

namespace Dynex.Core;

public sealed record DynexSettings
{
    public Guid DeviceId { get; init; } = Guid.NewGuid();
    public string DeviceName { get; init; } = Environment.MachineName;
    public string GroupKeyBase64 { get; init; } = "";
    public int Port { get; init; } = 39274;
    public bool LoopbackOnly { get; init; }
    public IReadOnlyList<IPEndPoint> DirectPeers { get; init; } = Array.Empty<IPEndPoint>();
    public double LineMbps { get; init; } = 200;
    public double HeadroomPercent { get; init; } = 15;
    public double GamingReserveMbps { get; init; } = 12;
    public double? ManualDownloadLimitMbps { get; init; }
    public bool AdaptiveEnabled { get; init; } = true;
    public TimeSpan PeerTtl { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(2);

    internal void Validate(bool requireKey)
    {
        if (DeviceId == Guid.Empty || string.IsNullOrWhiteSpace(DeviceName) || DeviceName.Length > 80 || DeviceName.Any(char.IsControl))
            throw new ArgumentException("Gerätename und Geräte-ID sind ungültig.");
        if (Port is < 1 or > 65535 || DirectPeers is null || DirectPeers.Count > 64)
            throw new ArgumentException("UDP-Port oder direkte Peers sind ungültig.");
        if (!double.IsFinite(LineMbps) || LineMbps is < 1 or > 100000 ||
            !double.IsFinite(HeadroomPercent) || HeadroomPercent is < 0 or > 90 ||
            !double.IsFinite(GamingReserveMbps) || GamingReserveMbps is < 0 or > 100000 ||
            ManualDownloadLimitMbps is { } m && (!double.IsFinite(m) || m is < 0.1 or > 100000))
            throw new ArgumentException("Bandbreiteneinstellungen sind ungültig.");
        if (PeerTtl < TimeSpan.FromSeconds(1) || PeerTtl > TimeSpan.FromSeconds(30) ||
            HeartbeatInterval < TimeSpan.FromMilliseconds(100) || HeartbeatInterval >= PeerTtl)
            throw new ArgumentException("Heartbeat muss kürzer als die Peer-Ablaufzeit sein.");
        foreach (var endpoint in DirectPeers)
            if (endpoint is null || endpoint.Port is < 1 or > 65535 || !LanAddress.IsAllowed(endpoint.Address, LoopbackOnly))
                throw new ArgumentException("Direkte Peers müssen IPv4-Adressen im privaten LAN verwenden.");
        if (requireKey)
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(GroupKeyBase64); }
            catch (FormatException) { throw new ArgumentException("Gruppenschlüssel ist ungültig."); }
            try { if (bytes.Length != 32) throw new ArgumentException("Gruppenschlüssel muss 32 zufällige Bytes enthalten."); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
}

public static class GroupKey
{
    public static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

public sealed record PeerAdvertisement(Guid DeviceId, string DeviceName, bool IsGaming,
    double? DownloadMbps, double? LatencyMs, DateTimeOffset LastSeenUtc,
    string? Address = null, double? AppliedLimitMbps = null, string? Game = null, bool ShaperActive = false,
    double? IdleBaselineMs = null, string? LatencyTargetId = null, long? LatencySampleSequence = null,
    DateTimeOffset? IdleBaselineAtUtc = null, Guid? SessionId = null, double? UploadMbps = null,
    DownloadBudgetProposal? BudgetProposal = null);

/// <summary>
/// A participant's currently configured/measured capacity and reserve policy, not its measured traffic.
/// Fresh status publications advertise this directly; never re-advertise the negotiated minimum.
/// Otherwise a departed slow participant's value would circulate indefinitely.
/// </summary>
public sealed record DownloadBudgetProposal(double LineMbps, double HeadroomPercent, double GamingReserveMbps)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => double.IsFinite(LineMbps) && LineMbps is >= 1 and <= 100000 &&
        double.IsFinite(HeadroomPercent) && HeadroomPercent is >= 0 and <= 90 &&
        double.IsFinite(GamingReserveMbps) && GamingReserveMbps is >= 0 and <= 100000;

    internal static DownloadBudgetProposal FromSettings(DynexSettings settings) =>
        new(settings.LineMbps, settings.HeadroomPercent, settings.GamingReserveMbps);

    internal double Remaining(int gamingCount) => Math.Max(0, LineMbps * (1 - HeadroomPercent / 100) - gamingCount * GamingReserveMbps);
}

public sealed record ProbeStats(DateTimeOffset TimestampUtc, double? ExternalLatencyMs,
    double? DownloadMbps, bool IsGamingRelevant = true, string TargetId = "default");

/// <summary>
/// Shared metadata describes the conservative planning pool, not proof that a driver applied it.
/// Complete means all currently observed participants supplied valid proposals and the per-PC share
/// can be represented. It does not establish a globally converged membership view or detect partitions.
/// AppliedLimitMbps/ShaperActive remain the separate confirmations; missing/disabled filters cannot
/// be controlled by this policy. Zero gamers means there is no automatic shared pool.
/// </summary>
public sealed record LimitDecision(double? DownloadLimitMbps, string Reason, bool GamingActive,
    double? IdleBaselineMs, bool AdaptiveReduced, DateTimeOffset? IdleBaselineAtUtc = null,
    double? SharedDownloadBudgetMbps = null, int ParticipantCount = 1, int GamingCount = 0,
    double? SharedCapacityMbps = null, bool SharedBudgetComplete = false);

internal static class LanAddress
{
    internal static bool IsAllowed(IPAddress address, bool loopbackOnly)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        if (IPAddress.IsLoopback(address)) return loopbackOnly;
        if (loopbackOnly) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 ||
               b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254;
    }
}
