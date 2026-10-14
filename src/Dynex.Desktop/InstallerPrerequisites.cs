using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace Dynex.Desktop;

/// <summary>Installer-only entry points; never start a runtime or read user settings.</summary>
internal static class InstallerPrerequisites
{
    internal sealed record Result(int ExitCode, string Message);

    internal static async Task<Result> RunAsync(Func<bool>? isInstalled = null,
        Func<bool>? firewallConfigured = null, Func<Task<int>>? install = null)
    {
        isInstalled ??= () => PacketFilterSetup.IsInstalled;
        firewallConfigured ??= InstallerFirewall.IsConfigured;
        install ??= InstallMachineAsync;
        try
        {
            // Check conflicting Windows block rules before any filter download
            // or elevation, including on a PC without the filter yet installed.
            var firewallReady = firewallConfigured();
            if (isInstalled() && firewallReady) return new(0, "Downloadbegrenzung und private Heimnetzverbindung sind bereits eingerichtet.");
            var code = await install();
            if (code == 1602) return Failure(1602, "Die Windows-Rückfrage oder Einrichtung wurde abgebrochen.");
            if (code is not (0 or 3010))
            {
                // Preserve a concrete block-rule diagnosis if policy changed
                // while the elevated phase ran, rather than reporting only code 1.
                if (code == 1) _ = firewallConfigured();
                return Failure(code, $"Windows meldet bei der Einrichtung Fehler {code}.");
            }
            if (!isInstalled() || !firewallConfigured()) return Failure(1, "Windows hat die benötigten Komponenten noch nicht bestätigt.");
            return code == 3010
                ? new(3010, "Die Einrichtung wurde abgeschlossen. Windows muss zum Aktivieren der Downloadbegrenzung neu gestartet werden; DYNEX startet den PC nicht automatisch neu.")
                : new(0, "Downloadbegrenzung und private Heimnetzverbindung wurden eingerichtet.");
        }
        catch (Exception error) when (error is OperationCanceledException || error is Win32Exception { NativeErrorCode: 1223 })
        { return Failure(1602, "Die Windows-Rückfrage oder Einrichtung wurde abgebrochen."); }
        catch (Exception error)
        { return Failure(1, error.Message); }
    }

    private static Result Failure(int code, string reason) => new(code,
        "Die Netzwerkeinrichtung ist noch nicht vollständig: " + reason +
        " DYNEX ist installiert. Starte das DYNEX-Setup erneut oder verwende die Einrichtung in den Einstellungen.");

    private static async Task<int> InstallMachineAsync()
    {
        // Download into the initiating user's cache before elevation. Credentials
        // from another administrator must not switch the DYNEX user profile.
        var package = PacketFilterSetup.IsInstalled ? null : await PacketFilterSetup.PreparePackageAsync(_ => { });
        return await RunElevatedAsync("--setup-machine", package);
    }

    private static async Task<int> RunElevatedAsync(string command, string? package = null)
    {
        // Fixed executable and command only; never accepts an arbitrary command,
        // app destination, firewall port, profile or remote address from callers.
        var start = new ProcessStartInfo(InstallerFirewall.ApplicationPath) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add(command);
        if (package is not null) start.ArgumentList.Add(package);
        using var process = Process.Start(start) ?? throw new IOException("Die Windows-Einrichtung konnte nicht gestartet werden.");
        await process.WaitForExitAsync();
        return process.ExitCode == 1223 ? 1602 : process.ExitCode;
    }

    internal static async Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length == 0) return 2;
        if (args[0] is "--setup-machine" or "--remove-machine-firewall")
        {
            if (args.Length > (args[0] == "--setup-machine" ? 2 : 1)) return 2;
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
            if (!string.Equals(Environment.ProcessPath, InstallerFirewall.ApplicationPath, StringComparison.OrdinalIgnoreCase)) return 13;
            try
            {
                if (args[0] == "--remove-machine-firewall") { InstallerFirewall.RemoveConfigured(); return 0; }
                var restart = false;
                if (!PacketFilterSetup.IsInstalled)
                {
                    if (args.Length != 2) return 2;
                    restart = await PacketFilterSetup.InstallPackageAsync(args[1], _ => { });
                }
                InstallerFirewall.EnsureConfigured();
                return restart ? 3010 : 0;
            }
            catch (OperationCanceledException) { return 1602; }
            catch (Win32Exception error) { return error.NativeErrorCode == 1223 ? 1602 : error.NativeErrorCode; }
            catch (InvalidDataException) { return 13; }
            catch { return 1; }
        }
        if (args[0] == "--remove-setup-firewall")
        {
            if (args.Length != 1) return 2;
            try { return InstallerFirewall.IsConfigured() ? await RunElevatedAsync("--remove-machine-firewall") : 0; }
            catch (Win32Exception error) { return error.NativeErrorCode == 1223 ? 1602 : error.NativeErrorCode; }
            catch { return 1; }
        }
        // The result destination is fixed to this installation, rather than an
        // arbitrary path supplied to a potentially elevated process.
        if (args.Length != 3 || args[0] != "--setup-prerequisites" || args[1] != "--setup-result" ||
            !Path.IsPathFullyQualified(args[2]) || !string.Equals(Path.GetFullPath(args[2]),
                Path.Combine(AppContext.BaseDirectory, "setup-result.txt"), StringComparison.OrdinalIgnoreCase)) return 2;
        var result = await RunAsync();
        try { await File.WriteAllTextAsync(args[2], result.Message, System.Text.Encoding.Unicode); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 2; }
        return result.ExitCode;
    }
}
