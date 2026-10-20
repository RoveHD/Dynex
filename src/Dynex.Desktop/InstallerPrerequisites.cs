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
        Func<bool>? firewallConfigured = null, Func<Task<int>>? install = null, Func<bool>? installationRegistered = null, bool legacyInstallation = false)
    {
        isInstalled ??= () => PacketFilterSetup.IsInstalled;
        firewallConfigured ??= InstallerFirewall.IsConfigured;
        install ??= () => InstallMachineAsync(legacyInstallation);
        installationRegistered ??= () => new WindowsInstallerSystem().ReadOwnership()?.Applications.Contains(InstallerFirewall.ApplicationPath, StringComparer.OrdinalIgnoreCase) == true;
        try
        {
            // Check conflicting Windows block rules before any filter download
            // or elevation, including on a PC without the filter yet installed.
            var firewallReady = firewallConfigured();
            if (isInstalled() && firewallReady && installationRegistered()) return new(0, "Paketpriorisierung und private Heimnetzverbindung sind bereits eingerichtet.");
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
                ? new(3010, "Die Einrichtung wurde abgeschlossen. Windows muss zum Aktivieren der Paketpriorisierung neu gestartet werden; DYNEX startet den PC nicht automatisch neu.")
                : new(0, "Paketpriorisierung und private Heimnetzverbindung wurden eingerichtet.");
        }
        catch (Exception error) when (error is OperationCanceledException || error is Win32Exception { NativeErrorCode: 1223 })
        { return Failure(1602, "Die Windows-Rückfrage oder Einrichtung wurde abgebrochen."); }
        catch (Exception error)
        { return Failure(1, error.Message); }
    }

    private static Result Failure(int code, string reason) => new(code,
        "Die Netzwerkeinrichtung ist noch nicht vollständig: " + reason +
        " DYNEX ist installiert. Starte das DYNEX-Setup erneut oder verwende die Einrichtung in den Einstellungen.");

    internal static async Task<int> InstallMachineAsync(bool legacyInstallation,
        Func<Task<string?>>? preparePackage = null, Func<string?, Task<int>>? installMachine = null,
        Func<Task<int>>? installFirewall = null)
    {
        // Download into the initiating user's cache before elevation. Credentials
        // from another administrator must not switch the DYNEX user profile.
        preparePackage ??= async () => PacketFilterSetup.IsInstalled ? null : await PacketFilterSetup.PreparePackageAsync(_ => { });
        string? package;
        try { package = await preparePackage(); }
        catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException))
        {
            // A failed filter download must not leave PC discovery blocked.
            var firewallCode = await (installFirewall?.Invoke() ?? RunElevatedAsync("--setup-machine-firewall"));
            if (firewallCode is 1602 or 1223) throw new OperationCanceledException("Die Windows-Einrichtung wurde abgebrochen.");
            throw new IOException((firewallCode == 0
                ? "Die Firewallfreigabe für DYNEX-PCs wurde eingerichtet. Die Paketpriorisierung konnte nicht vorbereitet werden: "
                : $"Die Paketpriorisierung konnte nicht vorbereitet werden; auch die Firewallfreigabe wurde nicht bestätigt (Windows-Code {firewallCode}): ") + error.Message, error);
        }
        if (installMachine is not null) return await installMachine(package);
        var arguments = package is null ? new List<string>() : [package];
        if (legacyInstallation) arguments.Add("--upgrade-legacy-installation");
        return await RunElevatedAsync("--setup-machine", arguments.ToArray());
    }

    private static async Task<int> RunElevatedAsync(string command, params string[] arguments)
    {
        // Fixed executable and command only; never accepts an arbitrary command,
        // app destination, firewall port, profile or remote address from callers.
        var start = new ProcessStartInfo(InstallerFirewall.ApplicationPath) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add(command);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Die Windows-Einrichtung konnte nicht gestartet werden.");
        await process.WaitForExitAsync();
        return process.ExitCode == 1223 ? 1602 : process.ExitCode;
    }

    internal static async Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length == 0) return 2;
        if (args[0] == "--remove-user-data")
        {
            if (args.Length != 1) return 2;
            // NSIS invokes this before deleting files, in the initiating user's
            // unelevated context. Never erase another administrator's profile.
            if (IsAdministrator()) return await WriteRemovalResultAsync("user-cleanup-result.txt", new(5, "Die Benutzerbereinigung muss ohne Administratorrechte im ursprünglichen Windows-Benutzerkonto gestartet werden."));
            var user = InstallerUserCleanup.Run();
            return await WriteRemovalResultAsync("user-cleanup-result.txt", new(user.ExitCode, user.Message));
        }
        if (args[0] is "--uninstall-system" or "--cleanup-removed-installation")
        {
            if (args.Length > 2 || args.Length == 2 && args[1] != "--confirm-legacy-filter-removal") return 2;
            var cleanup = args[0] == "--cleanup-removed-installation";
            var resultName = cleanup ? "cleanup-result.txt" : "uninstall-result.txt";
            var resultPath = Path.Combine(AppContext.BaseDirectory, resultName);
            try
            {
                if (cleanup && IsAdministrator())
                    return await WriteRemovalResultAsync(resultName, new(5, "Die Bereinigung muss ohne Administratorrechte im ursprünglichen Windows-Benutzerkonto gestartet werden. Die Windows-Rückfrage erfolgt erst danach für die Systembereinigung."));
                var target = cleanup ? CleanupApplication(WindowsIdentity.GetCurrent().User!.Value) : InstallerFirewall.ApplicationPath;
                if (cleanup && !MatchesCleanupApplication(target, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
                    return await WriteRemovalResultAsync(resultName, new(5204, "Der lokale Windows-Benutzerordner wurde umgeleitet. Diese Bereinigung unterstützt nur den ursprünglichen Standardpfad und hat keine System- oder Benutzereinstellungen entfernt. Bitte die alte DYNEX-Installation gezielt prüfen lassen."));
                if (File.Exists(resultPath)) File.Delete(resultPath);
                if (cleanup && RemovedInstallationStillExists(target))
                    return await WriteRemovalResultAsync(resultName, new(5203, "DYNEX ist noch installiert. Bitte zuerst die normale DYNEX-Deinstallation in den Windows-Einstellungen ausführen."));
                if (!cleanup && RunningInstallationStillExists(target))
                    return await WriteRemovalResultAsync(resultName, new(5203, "DYNEX oder seine Paketpriorisierung läuft noch. Bitte DYNEX vollständig beenden und die Deinstallation erneut starten. Es wurde kein Filter entfernt."));
                var extra = args.Length == 2 ? new[] { "--confirm-legacy-filter-removal" } : [];
                var code = cleanup
                    ? await RunElevatedAsync("--cleanup-machine", new[] { WindowsIdentity.GetCurrent().User!.Value }.Concat(extra).ToArray())
                    : await RunElevatedAsync("--uninstall-machine", extra);
                if (!File.Exists(resultPath))
                    return await WriteRemovalResultAsync(resultName, new(code is 0 or 3010 ? 1 : code,
                        code is 1602 or 1223 ? "Die Windows-Rückfrage wurde abgebrochen. Die Bereinigung wurde nicht bestätigt." : $"Die Systembereinigung konnte nicht bestätigt werden (Windows-Code {code})."));
                return code;
            }
            catch (Exception error) when (error is OperationCanceledException || error is Win32Exception { NativeErrorCode: 1223 })
            { return await WriteRemovalResultAsync(resultName, new(1602, "Die Windows-Rückfrage wurde abgebrochen. DYNEX wurde nicht vollständig entfernt.")); }
            catch (Exception error) { return await WriteRemovalResultAsync(resultName, new(1, "Die Systembereinigung ist fehlgeschlagen: " + error.Message)); }
        }
        if (args[0] is "--uninstall-machine" or "--cleanup-machine")
        {
            var cleanup = args[0] == "--cleanup-machine";
            var minimum = cleanup ? 2 : 1;
            if (args.Length < minimum || args.Length > minimum + 1 || args.Length == minimum + 1 && args[^1] != "--confirm-legacy-filter-removal") return 2;
            if (!IsAdministrator()) return 5;
            if (!SameExecutable()) return 13;
            var resultName = cleanup ? "cleanup-result.txt" : "uninstall-result.txt";
            try
            {
                var target = cleanup ? CleanupApplication(args[1]) : InstallerFirewall.ApplicationPath;
                if (cleanup && RemovedInstallationStillExists(target)) return await WriteRemovalResultAsync(resultName, new(5203, "DYNEX ist noch installiert. Bitte die normale Deinstallation verwenden."));
                // The initiating uninstall wrapper is itself DYNEX.exe. The
                // wrapper already checked other main processes; recheck the
                // elevated packet helper immediately before native removal.
                if (!cleanup && RunningInstallationStillExists(target, includeApplication: false))
                    return await WriteRemovalResultAsync(resultName, new(5203, "Die DYNEX-Paketpriorisierung läuft noch. Bitte DYNEX vollständig beenden und die Deinstallation wiederholen. Es wurde kein Filter entfernt."));
                return await WithSetupLockAsync(async () => await WriteRemovalResultAsync(resultName,
                    await InstallerSystem.RemoveAsync(new WindowsInstallerSystem(), target, args.Length == minimum + 1)));
            }
            catch (Exception error) { return await WriteRemovalResultAsync(resultName, new(1, "Die Systembereinigung ist nicht vollständig: " + error.Message)); }
        }
        if (args[0] is "--setup-machine" or "--setup-machine-firewall" or "--remove-machine-firewall")
        {
            var legacyUpgrade = args[0] == "--setup-machine" && args[^1] == "--upgrade-legacy-installation";
            var effectiveLength = args.Length - (legacyUpgrade ? 1 : 0);
            if (effectiveLength > (args[0] == "--setup-machine" ? 2 : 1)) return 2;
            if (!IsAdministrator()) return 5;
            if (!SameExecutable()) return 13;
            try
            {
                if (args[0] == "--remove-machine-firewall") { InstallerFirewall.RemoveConfigured(); return 0; }
                if (args[0] == "--setup-machine-firewall")
                    return await WithSetupLockAsync(() => { InstallerFirewall.EnsureConfigured(); return Task.FromResult(0); });
                return await WithSetupLockAsync(() => InstallerSystem.InstallAsync(new WindowsInstallerSystem(), InstallerFirewall.ApplicationPath,
                    effectiveLength == 2 ? args[1] : null, legacyUpgrade));
            }
            catch (OperationCanceledException) { return 1602; }
            catch (Win32Exception error) { return error.NativeErrorCode == 1223 ? 1602 : error.NativeErrorCode; }
            catch (InvalidDataException) { return 13; }
            catch { return 1; }
        }
        if (args[0] == "--remove-setup-firewall")
        {
            if (args.Length != 1) return 2;
            try { return InstallerFirewall.HasRules() ? await RunElevatedAsync("--remove-machine-firewall") : 0; }
            catch (Win32Exception error) { return error.NativeErrorCode == 1223 ? 1602 : error.NativeErrorCode; }
            catch { return 1; }
        }
        // The result destination is fixed to this installation, rather than an
        // arbitrary path supplied to a potentially elevated process.
        if (args.Length is not (3 or 4) || args.Length == 4 && args[3] != "--upgrade-legacy-installation" || args[0] != "--setup-prerequisites" || args[1] != "--setup-result" ||
            !Path.IsPathFullyQualified(args[2]) || !string.Equals(Path.GetFullPath(args[2]),
                Path.Combine(AppContext.BaseDirectory, "setup-result.txt"), StringComparison.OrdinalIgnoreCase)) return 2;
        var result = await RunAsync(legacyInstallation: args.Length == 4);
        try { await File.WriteAllTextAsync(args[2], result.Message, System.Text.Encoding.Unicode); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 2; }
        return result.ExitCode;
    }

    private static bool IsAdministrator() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private static bool SameExecutable() => string.Equals(Environment.ProcessPath, InstallerFirewall.ApplicationPath, StringComparison.OrdinalIgnoreCase);

    private static bool RemovedInstallationStillExists(string application)
    {
        var directory = Path.GetDirectoryName(application)!;
        if (File.Exists(application) || File.Exists(Path.Combine(directory, "Uninstall.exe"))) return true;
        return RunningInstallationStillExists(application);
    }

    private static bool RunningInstallationStillExists(string application, bool includeApplication = true)
    {
        foreach (var name in includeApplication ? new[] { "DYNEX", "Dynex.Shaper" } : ["Dynex.Shaper"])
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    var running = process.MainModule?.FileName;
                    if (running is null) throw new IOException("Der Pfad eines laufenden DYNEX-Prozesses konnte nicht geprüft werden.");
                    if (MatchesInstallationProcess(application, running, includeApplication)) return true;
                }
                catch (InvalidOperationException) { /* Process exited during inventory. */ }
            }
        }
        return false;
    }

    internal static bool MatchesInstallationProcess(string application, string running, bool includeApplication = true) =>
        includeApplication && string.Equals(running, application, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(running, Path.Combine(Path.GetDirectoryName(application)!, "shaper", "Dynex.Shaper.exe"), StringComparison.OrdinalIgnoreCase);

    internal static bool MatchesCleanupApplication(string protectedProfileApplication, string localApplicationData) =>
        !string.IsNullOrWhiteSpace(localApplicationData) && Path.IsPathFullyQualified(localApplicationData) &&
        string.Equals(Path.GetFullPath(Path.Combine(localApplicationData, "Programs", "DYNEX", "DYNEX.exe")), protectedProfileApplication, StringComparison.OrdinalIgnoreCase);

    internal static string CleanupApplication(string sid)
    {
        // The elevated process may use another administrator's credentials.
        // Resolve the initiating SID through protected ProfileList, never a path
        // argument or the administrator's LocalApplicationData environment.
        var identity = new SecurityIdentifier(sid);
        if (!identity.IsAccountSid() || !string.Equals(identity.Value, sid, StringComparison.Ordinal)) throw new InvalidDataException("Ungültige ursprüngliche Benutzeridentität.");
        using var machine = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var profile = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid);
        if (profile?.GetValue("ProfileImagePath") is not string root) throw new InvalidDataException("Das ursprüngliche Windows-Benutzerprofil konnte nicht bestätigt werden.");
        root = Environment.ExpandEnvironmentVariables(root);
        if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidDataException("Das Windows-Benutzerprofil ist kein lokaler absoluter Pfad.");
        return Path.GetFullPath(Path.Combine(root, "AppData", "Local", "Programs", "DYNEX", "DYNEX.exe"));
    }

    private static async Task<int> WithSetupLockAsync(Func<Task<int>> action)
    {
        // Mutex ownership is thread-affine. Keep it on one dedicated thread
        // while the asynchronous MSI operation runs to completion.
        return await Task.Run(() =>
        {
            using var mutex = new Mutex(false, @"Global\DYNEX.SystemSetup.V1");
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(1)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return 1618;
                return action().GetAwaiter().GetResult();
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        });
    }

    private static async Task<int> WriteRemovalResultAsync(string name, InstallerSystem.Result result)
    {
        // Both the name and destination are fixed to this executable. CreateNew
        // rejects an existing hard link/result planted during elevation.
        try
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (var item = directory; item is not null; item = item.Parent)
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return 13;
            await using var output = new FileStream(Path.Combine(AppContext.BaseDirectory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(output, System.Text.Encoding.Unicode);
            await writer.WriteAsync(result.Message);
            return result.ExitCode;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 2; }
    }
}
