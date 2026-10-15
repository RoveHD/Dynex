using Dynex.Core;

namespace Dynex.Desktop;

internal sealed record NetworkConnectionCheck(bool? FirewallConfigured, string FirewallMessage,
    IReadOnlyList<string> PrivateLocalAddresses, LanEnvironment? Lan = null)
{
    /// <summary>Wi-Fi name (for guest networks) and other UPnP devices answering (client isolation). Read-only.</summary>
    private static LanEnvironment ReadLan()
    {
        string? wifi = null;
        try
        {
            using var netsh = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("netsh", "wlan show interfaces")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (netsh is not null)
            {
                var output = netsh.StandardOutput.ReadToEnd();
                netsh.WaitForExit(3000);
                wifi = output.Split('\n').Select(line => line.Trim())
                    .Where(line => line.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("SSID-", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
                    .Select(line => line[(line.IndexOf(':') + 1)..].Trim()).FirstOrDefault(name => name.Length is > 0 and <= 64);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        var gateway = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses).Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        int? devices = null;
        try { devices = RouterTrafficMonitor.CountLanDevicesAsync(gateway, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException) { }
        return new(wifi, devices);
    }

    // This check is read-only. Opening network help must never install a filter,
    // relax firewall policy or change a Windows network profile.
    internal static Task<NetworkConnectionCheck> ReadAsync(bool isolated) => Task.Run(() =>
    {
        if (isolated) return new NetworkConnectionCheck(null, "Isolierter Test: Windows-Firewall wird nicht geprüft.", []);
        var addresses = LanDiscovery.GetPrivateLocalAddresses();
        try
        {
            var configured = InstallerFirewall.IsConfigured();
            return new NetworkConnectionCheck(configured, configured
                ? "DYNEX-Firewallfreigabe für das private Heimnetz vorhanden."
                : "DYNEX-Firewallfreigabe fehlt oder konnte nicht bestätigt werden. DYNEX-Setup auf diesem PC erneut ausführen.", addresses, ReadLan());
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new NetworkConnectionCheck(null, "Firewallprüfung: " + error.Message, addresses, ReadLan());
        }
    });
}
