using Dynex.Core;

namespace Dynex.Desktop;

internal sealed record NetworkConnectionCheck(bool? FirewallConfigured, string FirewallMessage,
    IReadOnlyList<string> PrivateLocalAddresses)
{
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
                : "DYNEX-Firewallfreigabe fehlt oder konnte nicht bestätigt werden. DYNEX-Setup auf diesem PC erneut ausführen.", addresses);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new NetworkConnectionCheck(null, "Firewallprüfung: " + error.Message, addresses);
        }
    });
}
