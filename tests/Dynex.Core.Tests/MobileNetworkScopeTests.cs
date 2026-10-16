using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Dynex.Core;

internal static class MobileNetworkScopeTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        AutoLanNetworkBinding Binding(string address = "192.168.4.12", int prefix = 24, int index = 4) =>
            new(IPAddress.Parse(address), prefix, index);
        foreach (var binding in new[] { Binding(), Binding("10.0.2.1", 8), Binding("172.16.1.2", 12),
            Binding("192.168.0.1", 16), Binding("192.168.4.1", 30) })
        {
            var snapshot = new AutoLanNetworkSnapshot([binding]);
            var read = AutoLanNetworkScope.ReadProvided(() => snapshot);
            check(snapshot.BindingCount == 1 && read is { Error: null, Bindings.Length: 1 } &&
                read.Bindings[0].Address.Equals(binding.Address) && read.Bindings[0].InterfaceIndex == binding.InterfaceIndex,
                "explicit platform scope preserves a valid private IPv4 prefix and interface: " + binding.Address);
            var discovery = new AutoLanDiscovery();
            check(discovery.Next(read.Bindings, 8).All(ip => read.Bindings[0].Contains(ip) &&
                !ip.Equals(read.Bindings[0].Address) && !ip.Equals(read.Bindings[0].Broadcast)),
                "mobile scope uses existing bounded on-link discovery without requiring Windows ARP");
        }
        foreach (var invalid in new[]
        {
            Binding("8.8.8.8"), Binding("127.0.0.1"), Binding("169.254.1.2", 16), Binding("100.64.0.1"),
            Binding("0.0.0.0"), Binding("255.255.255.255"), Binding("224.0.0.1"), Binding("::1"), Binding("::ffff:192.168.4.12"),
            Binding("192.168.4.0"), Binding("192.168.4.255"), Binding("192.168.4.12", 15), Binding("172.16.1.2", 11),
            Binding("172.15.1.2", 16), Binding("172.32.1.2", 16), Binding("10.0.2.1", 0), Binding("10.0.2.1", 7),
            Binding("10.0.2.1", 31), Binding("10.0.2.1", 32), Binding("10.0.2.1", 33), Binding(index: 0), Binding(index: -1),
            new AutoLanNetworkBinding(null!, 24, 4), null!
        })
        {
            var rejected = false;
            try { _ = new AutoLanNetworkSnapshot([invalid]); } catch (ArgumentException) { rejected = true; }
            check(rejected, "invalid platform adapter data never opens a scope: " + invalid);
            var read = AutoLanNetworkScope.ReadProvided(() => new([invalid]));
            check(read is { Bindings.Length: 0, Error: not null }, "invalid provider data fails closed without terminating the heartbeat");
        }
        foreach (var bindings in new[]
        {
            new[] { Binding(), Binding(index: 5) },
            new[] { Binding(), Binding(prefix: 25) },
            new[] { Binding(), Binding("192.168.4.13", index: 5) },
            Enumerable.Range(1, 17).Select(i => Binding($"10.{i}.0.1", 16, i)).ToArray()
        })
        {
            var rejected = false;
            try { _ = new AutoLanNetworkSnapshot(bindings); } catch (ArgumentException) { rejected = true; }
            check(rejected, "ambiguous or excessive adapter snapshots are rejected atomically");
        }
        check(new AutoLanNetworkSnapshot([Binding(), Binding()]).BindingCount == 1, "identical adapter data is deduplicated");
        check(new AutoLanNetworkSnapshot([Binding(), Binding("10.2.0.1", 16, 5)]).BindingCount == 2,
            "non-overlapping trusted interfaces remain independently scoped");
        check(new AutoLanNetworkSnapshot([Binding(), Binding("10.2.0.1", 16, 5)]).CopyBindings().SequenceEqual(
            new AutoLanNetworkSnapshot([Binding("10.2.0.1", 16, 5), Binding()]).CopyBindings()),
            "adapter enumeration order does not create false scope changes or new discovery bursts");
        var source = new[] { Binding() };
        var immutable = new AutoLanNetworkSnapshot(source);
        source[0] = Binding("10.0.0.1", 8);
        var firstCopy = immutable.CopyBindings();
        firstCopy[0] = new(IPAddress.Parse("8.8.8.8"), IPAddress.Any, 0);
        check(immutable.CopyBindings() is [{ InterfaceIndex: 4 }] && immutable.CopyBindings()[0].Address.Equals(IPAddress.Parse("192.168.4.12")),
            "snapshot copies input collections and does not expose its retained bindings");
        var recovered = false;
        AutoLanNetworkSnapshot Provider() => recovered ? immutable : throw new InvalidOperationException("network permission unavailable");
        check(AutoLanNetworkScope.ReadProvided(Provider) is { Bindings.Length: 0, Error: not null }, "provider permission failure closes scope");
        recovered = true;
        check(AutoLanNetworkScope.ReadProvided(Provider) is { Bindings.Length: 1, Error: null }, "provider recovery is read without a Windows event");
        check(AutoLanNetworkScope.ReadProvided(() => AutoLanNetworkSnapshot.Empty) is { Bindings.Length: 0, Error: not null } &&
            AutoLanNetworkScope.ReadProvided(() => null!) is { Bindings.Length: 0, Error: not null }, "disconnected and null provider snapshots never fall back to all interfaces");
        var broad = new AutoLanNetworkSnapshot([Binding("10.5.7.12", 8)]).CopyBindings();
        var burst = AutoLanDiscovery.LocalBurst(broad);
        check(burst.Count == 253 && burst.All(ip => ip.GetAddressBytes()[0] == 10 && ip.GetAddressBytes()[1] == 5 && ip.GetAddressBytes()[2] == 7),
            "low-power discovery narrows a broad private /8 to one finite local-/24 burst");
        check(AutoLanDiscovery.LocalBurst(new AutoLanNetworkSnapshot([Binding("192.168.4.1", 30)]).CopyBindings()) is [var neighbor] &&
            neighbor.Equals(IPAddress.Parse("192.168.4.2")), "low-power burst preserves narrower on-link boundaries");
        var multiple = new AutoLanNetworkSnapshot(Enumerable.Range(1, 16).Select(i => Binding($"10.{i}.2.1", 16, i))).CopyBindings();
        check(AutoLanDiscovery.LocalBurst(multiple).Count <= 256 && AutoLanDiscovery.LocalBurst([]).Count == 0,
            "low-power discovery caps all interfaces together to 256 targets and sends nothing for empty scope");

        var keyA = AutoLanIdentity.GenerateSigningKey(); var keyB = AutoLanIdentity.GenerateSigningKey();
        var portA = FreePort(); var portB = FreePort(); while (portB == portA) portB = FreePort();
        var settingsA = new DynexSettings { DeviceId = AutoLanIdentity.GetDeviceId(keyA), DeviceName = "Mobile scope A", Port = portA,
            LoopbackOnly = true, DirectPeers = [new(IPAddress.Loopback, portB)], HeartbeatInterval = TimeSpan.FromMilliseconds(100), PeerTtl = TimeSpan.FromSeconds(1) };
        var settingsB = settingsA with { DeviceId = AutoLanIdentity.GetDeviceId(keyB), DeviceName = "PC scope B", Port = portB, DirectPeers = [] };
        using (var scoped = new AutoLanPeerService(settingsA with { LoopbackOnly = false, DirectPeers = [] }, keyA, () => immutable))
        {
            typeof(AutoLanPeerService).GetField("_bindings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(scoped, immutable.CopyBindings());
            var allowed = typeof(AutoLanPeerService).GetMethod("SourceAllowed", BindingFlags.Instance | BindingFlags.NonPublic)!;
            bool Source(string address, int index) => (bool)allowed.Invoke(scoped, [IPAddress.Parse(address), index])!;
            check(Source("192.168.4.8", 4) && !Source("192.168.4.8", 5) && !Source("192.168.5.8", 4) &&
                !Source("8.8.8.8", 4) && !Source("127.0.0.1", 4),
                "mobile peer source acceptance requires both on-link address and exact supplied interface; no socket opened");
        }
        var providerReads = 0;
        AutoLanNetworkSnapshot TestProvider() { providerReads++; return immutable; }
        await using var a = new AutoLanPeerService(settingsA, keyA, TestProvider, lowPowerDiscovery: true);
        await using var b = new AutoLanPeerService(settingsB, keyB, () => AutoLanNetworkSnapshot.Empty);
        check(!(bool)typeof(AutoLanPeerService).GetField("_subscribeNetworkChanges", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(a)!,
            "public platform provider requires no global NetworkChange event support");
        var nullRejected = false;
        try { using var invalid = new AutoLanPeerService(settingsA, keyA, (Func<AutoLanNetworkSnapshot>)null!); }
        catch (ArgumentNullException) { nullRejected = true; }
        check(nullRejected, "missing explicit provider is rejected instead of selecting an automatic platform fallback");
        await using (var passive = new AutoLanPeerService(settingsA with { LoopbackOnly = false, DirectPeers = [] }, keyA,
            () => AutoLanNetworkSnapshot.Empty, lowPowerDiscovery: true))
        using (var unusedSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            var run = (Task)typeof(AutoLanPeerService).GetMethod("DiscoveryLoopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(passive, [unusedSocket, stop.Token])!;
            check(!run.IsCompleted && unusedSocket.LocalEndPoint is null, "low-power discovery begins awaiting a signal without starting any socket or timer");
            passive.RequestDiscovery();
            while ((int)typeof(AutoLanPeerService).GetField("_discoveryRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(passive)! != 0)
            { stop.Token.ThrowIfCancellationRequested(); await Task.Delay(5, stop.Token); }
            check(!run.IsCompleted && unusedSocket.LocalEndPoint is null && passive.GetNetworkDiagnostics().DiscoveryProbeDatagrams == 0,
                "an empty-scope request returns to signal waiting without packets or permanent polling");
            stop.Cancel(); await run;
            check(run.IsCompletedSuccessfully, "low-power discovery cancellation interrupts its idle signal wait cleanly");
        }
        b.Publish(true, game: "Synthetic PC game"); a.Publish(false);
        await a.StartAsync(); await b.StartAsync();
        var until = DateTimeOffset.UtcNow.AddSeconds(5);
        while ((a.Snapshot().Count != 1 || b.Snapshot().Count != 1) && DateTimeOffset.UtcNow < until) await Task.Delay(20);
        check(a.Snapshot() is [{ IsGaming: true }] && b.Snapshot() is [{ IsGaming: false }],
            "public mobile constructor shares existing authenticated AutoLAN gaming status on isolated loopback");
        check(providerReads == 0, "explicit loopback test mode never reads or probes the supplied real network scope");
        await a.StopAsync(); await b.StopAsync();
        check(a.Snapshot().Count == 0 && b.Snapshot().Count == 0, "mobile AutoLAN lifecycle closes isolated sockets and clears peers");
    }

    private static int FreePort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }
}
