namespace Dynex.Shaper;

public sealed record ApplicationLimit(string ProcessName, double? DownloadMbps, double? UploadMbps);

/// <summary>Fresh, unambiguous socket identity is required; a remembered PID alone never selects a rule.</summary>
public sealed class ApplicationRulePolicy(Func<OwnershipSnapshot> snapshot,
    IEnumerable<(System.Net.IPAddress Address, int Prefix)>? localNetworks = null)
{
    private readonly (System.Net.IPAddress Address, int Prefix)[] _localNetworks = (localNetworks ?? BulkDownloadPolicy.GetLocalNetworks()).ToArray();
    private IReadOnlyDictionary<string, ApplicationLimit> _rules = new Dictionary<string, ApplicationLimit>();
    public void Configure(IEnumerable<ApplicationLimit> rules) => Volatile.Write(ref _rules,
        rules.ToDictionary(r => r.ProcessName, StringComparer.OrdinalIgnoreCase));

    public ApplicationLimit? Classify(CapturedPacket packet)
    {
        if (packet.IpOffset < 0 || packet.IpOffset >= packet.Length) return null;
        if (!InboundPacketParser.TryParse(packet.Buffer.AsSpan(packet.IpOffset, packet.Length - packet.IpOffset), out var flow, out var payload) || payload <= 0)
            return null; // TCP ACK/control packets keep every other flow responsive.
        if (packet.Direction == PacketDirection.Outbound)
            flow = new(flow.Protocol, flow.RemoteAddress, flow.RemotePort, flow.LocalAddress, flow.LocalPort);
        if (BulkDownloadPolicy.IsLocal(flow.RemoteAddress, _localNetworks)) return null;
        if (flow.LocalPort is 53 or 5353 or 5355 or 39274 || flow.RemotePort is 53 or 5353 or 5355 or 39274) return null;
        var owner = snapshot().Classify(flow);
        if (owner is not { ProcessId: > 0, ProcessStartedAtUtc: not null, ProcessName: not null }) return null;
        if (!Volatile.Read(ref _rules).TryGetValue(owner.ProcessName, out var rule)) return null;
        return (packet.Direction == PacketDirection.Inbound ? rule.DownloadMbps : rule.UploadMbps).HasValue ? rule : null;
    }
}
