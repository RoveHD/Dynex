using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Dynex.Shaper;

/// <summary>Admits only identified, sustained bulk flows to the download queue.</summary>
public sealed class BulkDownloadPolicy
{
    private static readonly HashSet<string> VoiceApplications = new(StringComparer.OrdinalIgnoreCase)
    {
        "Discord", "DiscordCanary", "DiscordPTB", "Teams", "ms-teams", "Zoom", "ts3client_win64", "ts3client_win32", "mumble"
    };
    internal static bool IsVoiceApplication(string name) => VoiceApplications.Contains(name);
    private readonly Func<OwnershipSnapshot> _snapshot;
    private readonly Func<double> _clock;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly (IPAddress Address, int Prefix)[] _localNetworks;
    private readonly Dictionary<FlowIdentity, FlowActivity> _flows = new();
    private readonly Dictionary<(int Pid, long StartedAt), FlowActivity> _programs = new();
    private ControlProtocol.ProtectedProcess[] _protected = [];
    private double _lastCleanup;

    public BulkDownloadPolicy(Func<OwnershipSnapshot> snapshot,
        IEnumerable<(IPAddress Address, int Prefix)>? localNetworks = null,
        Func<double>? clock = null, Func<DateTimeOffset>? utcNow = null)
    {
        _snapshot = snapshot;
        _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _localNetworks = (localNetworks ?? GetLocalNetworks()).ToArray();
    }

    public void Protect(IEnumerable<ControlProtocol.ProtectedProcess> processes) =>
        Volatile.Write(ref _protected, processes.Take(128).ToArray());

    // Called by one receiver thread. Updating protected identities is atomic.
    public bool ShouldLimit(CapturedPacket packet)
    {
        if (packet.Direction == PacketDirection.Outbound) return false;
        if (packet.IpOffset < 0 || packet.IpOffset >= packet.Length) return false;
        var ip = packet.Buffer.AsSpan(packet.IpOffset, packet.Length - packet.IpOffset);
        if (!InboundPacketParser.TryParse(ip, out var flow, out var payloadLength) || payloadLength <= 0) return false;
        if (IsLocal(flow.RemoteAddress) || flow.LocalPort is 53 or 5353 or 5355 or 39274 || flow.RemotePort is 53 or 5353 or 5355 or 39274)
            return false;
        var snapshot = _snapshot();
        var age = _utcNow() - snapshot.CapturedAtUtc;
        if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(1)) return false;
        var owner = snapshot.Classify(flow);
        // Incomplete process information must never turn an unknown game into
        // background traffic. Voice applications also bypass high-volume flows.
        if (owner is null || owner.ProcessStartedAtUtc is null || string.IsNullOrEmpty(owner.ProcessName)) return false;
        if (VoiceApplications.Contains(owner.ProcessName)) return false;
        if (Volatile.Read(ref _protected).Any(p => p.ProcessId == owner.ProcessId &&
            (p.StartedAtUtcTicks is null || p.StartedAtUtcTicks == owner.ProcessStartedAtUtc.Value.UtcTicks))) return false;

        var now = _clock();
        if (now - _lastCleanup > 5)
        {
            foreach (var key in _flows.Where(p => now - p.Value.LastAt > 5).Select(p => p.Key).ToArray()) _flows.Remove(key);
            foreach (var key in _programs.Where(p => now - p.Value.LastAt > 5).Select(p => p.Key).ToArray()) _programs.Remove(key);
            _lastCleanup = now;
        }
        var identity = new FlowIdentity(flow, owner.ProcessId, owner.ProcessStartedAtUtc.Value.UtcTicks);
        var programId = (owner.ProcessId, owner.ProcessStartedAtUtc.Value.UtcTicks);
        if (!_programs.TryGetValue(programId, out var program) || now - program.StartedAt > 1)
            _programs[programId] = program = new FlowActivity { StartedAt = now };
        program.Bytes += payloadLength;
        program.LastAt = now;
        if (!_flows.TryGetValue(identity, out var activity) || now - activity.LastAt > 5)
        {
            if (_flows.Count >= 8192) return false;
            _flows[identity] = activity = new FlowActivity { StartedAt = now, LastAt = now };
        }
        activity.LastAt = now;
        if (activity.IsBulk) return true;
        activity.Bytes += payloadLength;
        var elapsed = now - activity.StartedAt;
        // Allow short requests and modest flows immediately. Once a connection
        // transfers 256 KiB at >= 2 Mbit/s over >= 250 ms it is a bulk candidate.
        // Keep that decision until idle, otherwise TCP's response to our own
        // limit would repeatedly disable the limiter.
        var busyProgram = program.Bytes >= 1024 * 1024 && now - program.StartedAt >= .25;
        if (elapsed >= .25 &&
            (activity.Bytes >= 256 * 1024 && activity.Bytes / elapsed >= 256 * 1024 ||
             busyProgram && activity.Bytes >= 64 * 1024 && activity.Bytes / elapsed >= 32 * 1024))
            activity.IsBulk = true;
        else if (elapsed > 1)
        {
            activity.StartedAt = now;
            activity.Bytes = payloadLength;
        }
        return activity.IsBulk;
    }

    private bool IsLocal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4 && (bytes[0] is 0 or 10 or 127 || bytes[0] >= 224 ||
            bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 ||
            bytes[0] == 169 && bytes[1] == 254)) return true;
        if (bytes.Length == 16 && (address.IsIPv6LinkLocal || address.IsIPv6Multicast || (bytes[0] & 0xfe) == 0xfc)) return true;
        foreach (var (local, prefix) in _localNetworks)
        {
            var other = local.GetAddressBytes();
            if (other.Length != bytes.Length || prefix <= 0 || prefix > bytes.Length * 8) continue;
            var whole = prefix / 8;
            var rest = prefix % 8;
            if (bytes.AsSpan(0, whole).SequenceEqual(other.AsSpan(0, whole)) &&
                (rest == 0 || (bytes[whole] & (255 << (8 - rest))) == (other[whole] & (255 << (8 - rest))))) return true;
        }
        return false;
    }

    private static IEnumerable<(IPAddress Address, int Prefix)> GetLocalNetworks() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
        .Select(a => (a.Address, a.PrefixLength));

    private readonly record struct FlowIdentity(SocketFlow Flow, int Pid, long StartedAt);
    private sealed class FlowActivity { internal double StartedAt, LastAt; internal long Bytes; internal bool IsBulk; }
}
