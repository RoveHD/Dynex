using System.Diagnostics;
using System.Net;

namespace Dynex.Shaper;

/// <summary>Packet evidence only: socket tables establish ownership, never activity.</summary>
public sealed class GameTrafficMonitor(Func<OwnershipSnapshot> snapshot, Func<double>? clock = null)
{
    public const double ActivitySeconds = 10;
    private readonly Func<double> _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    private readonly object _gate = new();
    private HashSet<(int ProcessId, long StartedAtUtcTicks)> _games = [];
    private readonly Dictionary<(int ProcessId, long StartedAtUtcTicks), double> _lastActivity = [];
    private bool _enabled;

    public sealed record Activity(int ProcessId, long StartedAtUtcTicks, double AgeSeconds);

    public void Configure(IEnumerable<ControlProtocol.ProtectedProcess> games, bool enabled)
    {
        var identities = games.Take(128).Where(game => game.ProcessId > 0 && game.StartedAtUtcTicks is > 0 &&
            game.StartedAtUtcTicks <= DateTime.MaxValue.Ticks)
            .Select(game => (game.ProcessId, StartedAtUtcTicks: game.StartedAtUtcTicks!.Value)).ToHashSet();
        lock (_gate)
        {
            _enabled = enabled;
            _games = enabled ? identities : [];
            foreach (var game in _lastActivity.Keys.Where(game => !_games.Contains(game)).ToArray()) _lastActivity.Remove(game);
        }
    }

    public void Observe(CapturedPacket packet)
    {
        lock (_gate)
        {
            if (!_enabled || _games.Count == 0 || packet.IpOffset < 0 || packet.IpOffset >= packet.Length) return;
            if (!InboundPacketParser.TryParse(packet.Buffer.AsSpan(packet.IpOffset, packet.Length - packet.IpOffset), out var flow)) return;
            if (packet.Direction == PacketDirection.Outbound)
                flow = new(flow.Protocol, flow.RemoteAddress, flow.RemotePort, flow.LocalAddress, flow.LocalPort);
            // LAN and every valid TCP/UDP packet (including TCP control packets)
            // count. Local loopback is not evidence of a network game session.
            if (Loopback(flow.LocalAddress) || Loopback(flow.RemoteAddress)) return;
            var owner = snapshot().Classify(flow);
            if (owner?.ProcessStartedAtUtc is not { } started) return;
            var game = (owner.ProcessId, started.UtcTicks);
            if (_games.Contains(game)) _lastActivity[game] = _clock();
        }
    }

    public IReadOnlyList<Activity> Snapshot()
    {
        lock (_gate)
        {
            var now = _clock();
            foreach (var game in _lastActivity.Where(pair => now - pair.Value >= ActivitySeconds || now < pair.Value)
                         .Select(pair => pair.Key).ToArray()) _lastActivity.Remove(game);
            return _lastActivity.OrderBy(pair => pair.Key.ProcessId).Select(pair =>
                new Activity(pair.Key.ProcessId, pair.Key.StartedAtUtcTicks, now - pair.Value)).ToArray();
        }
    }

    private static bool Loopback(IPAddress address) => IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}
