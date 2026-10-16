using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Dynex.Core;

namespace Dynex.Desktop;

internal sealed record NetworkConnectionCheck(bool? FirewallConfigured, string FirewallMessage,
    IReadOnlyList<string> PrivateLocalAddresses, LanEnvironment? Lan = null,
    IReadOnlyList<PeerReachability>? DirectPeerPings = null)
{
    internal string Summary => FirewallMessage +
        (PrivateLocalAddresses.Count > 0 ? " Dieser PC: " + string.Join(", ", PrivateLocalAddresses) + "." : "") +
        (DirectPeerPings is { Count: > 0 } pings ? " " + string.Join(" ", pings.Select(p => p.ToString())) : "");

    internal static async Task<IReadOnlyList<PeerReachability>> ProbePeersAsync(IEnumerable<string> targets,
        IReadOnlyList<string> localAddresses, CancellationToken token,
        Func<string, CancellationToken, Task<long?>>? probe = null)
    {
        probe ??= async (address, cancellation) =>
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(IPAddress.Parse(address), TimeSpan.FromSeconds(1),
                new byte[32], cancellationToken: cancellation).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        };
        var addresses = targets.Where(a => IPAddress.TryParse(a, out var ip) && SettingsStore.IsPrivate(ip))
            .Select(a => IPAddress.Parse(a).ToString()).Distinct(StringComparer.Ordinal)
            .Where(a => !localAddresses.Contains(a, StringComparer.Ordinal)).Take(32).ToArray();
        return await Task.WhenAll(addresses.Select(async address =>
        {
            token.ThrowIfCancellationRequested();
            try { return new PeerReachability(address, await probe(address, token).ConfigureAwait(false)); }
            catch (Exception error) when (error is PingException or System.Net.Sockets.SocketException)
            { return new PeerReachability(address, null); }
        })).ConfigureAwait(false);
    }
    // Drain stdout while waiting, and kill a stalled child instead of blocking on ReadToEnd.
    internal static async Task<string?> ReadProcessOutputAsync(ProcessStartInfo command, TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(command);
        if (process is null) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return await output.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            token.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>Wi-Fi name and other UPnP devices answering. Read-only and bounded.</summary>
    private static async Task<LanEnvironment> ReadLanAsync(CancellationToken token)
    {
        string? wifi = null;
        try
        {
            var output = await ReadProcessOutputAsync(new("netsh", "wlan show interfaces")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true }, TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            wifi = output?.Split('\n').Select(line => line.Trim())
                    .Where(line => line.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("SSID-", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                    .Select(line => line[(line.IndexOf(':') + 1)..].Trim()).FirstOrDefault(name => name.Length is > 0 and <= 64);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        token.ThrowIfCancellationRequested();
        var gateway = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses).Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        int? devices = null;
        try { devices = await RouterTrafficMonitor.CountLanDevicesAsync(gateway, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException) { }
        return new(wifi, devices);
    }

    // Opening network help never installs a filter, relaxes firewall policy or changes a profile.
    internal static async Task<NetworkConnectionCheck> ReadAsync(bool isolated, CancellationToken cancellationToken = default,
        IReadOnlyList<string>? directPeers = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isolated) return new(null, "Isolierter Test: Windows-Firewall wird nicht geprüft.", []);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        try
        {
            return await Task.Run(async () =>
            {
                var addresses = LanDiscovery.GetPrivateLocalAddresses();
                bool? configured = null;
                string message;
                try
                {
                    // COM has no cancellation API. Bound the caller's wait; late results are never published.
                    configured = await Task.Run(InstallerFirewall.IsConfigured, token)
                        .WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                    message = configured.Value
                        ? "DYNEX-Firewallfreigabe für das private Heimnetz vorhanden."
                        : "DYNEX-Firewallfreigabe fehlt oder konnte nicht bestätigt werden. DYNEX-Setup auf diesem PC erneut ausführen.";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    message = error is TimeoutException ? "Firewallprüfung: Zeitlimit erreicht." : "Firewallprüfung: " + error.Message;
                }
                var lan = await ReadLanAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var probes = await ProbePeersAsync(directPeers ?? [], addresses, token).ConfigureAwait(false);
                return new NetworkConnectionCheck(configured, message, addresses, lan, probes);
            }, token).WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Die Netzwerkprüfung hat das Zeitlimit von 10 Sekunden erreicht. Bitte erneut versuchen."); }
    }
}

internal sealed record PeerReachability(string Address, long? RoundtripMs)
{
    public override string ToString() => RoundtripMs is { } ms
        ? $"{Address}: Ping {ms} ms (erreichbar; DYNEX-Verbindung wird separat bestätigt)."
        : $"{Address}: keine Ping-Antwort (ICMP kann gesperrt sein; DYNEX-Suche läuft weiter).";
}
