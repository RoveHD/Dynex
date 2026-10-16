using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace Dynex.Desktop;

// This receipt is machine-wide: neither an update nor a second user may adopt
// a filter that was already present. Only the elevated setup writes it.
internal sealed record SetupOwnership(string FilterOrigin, string[] Applications, bool RemovalPending = false, string? RemovalBootIdentity = null)
{
    internal const string Owned = "DYNEX";
    internal const string Preexisting = "Preexisting";
    internal const string Pending = "InstallPending";
    internal const string Legacy = "LegacyUnknown";
}

internal interface IInstallerSystem
{
    bool FilterPresent { get; }
    bool KnownProductPresent { get; }
    string? BootIdentity { get; }
    SetupOwnership? ReadOwnership();
    void WriteOwnership(SetupOwnership? ownership);
    Task<PacketFilterSetup.InstallResult> InstallFilterAsync(string package);
    Task<int> RemoveFilterAsync();
    void EnsureFirewall(string application);
    void RemoveFirewall(string application);
}

internal static class InstallerSystem
{
    internal const int LegacyConfirmationRequired = 5201;
    internal const int UnsafeFilterIdentity = 5202;
    internal sealed record Result(int ExitCode, string Message);

    internal static async Task<int> InstallAsync(IInstallerSystem system, string application, string? package, bool legacyInstallation = false)
    {
        var ownership = system.ReadOwnership();
        Validate(ownership);
        if (ownership?.RemovalPending == true)
        {
            var pending = CheckPendingBoot(system, ownership);
            if (!pending.RestartConfirmed) throw new IOException(PendingRestartMessage(pending.Ownership, pending.BootAvailable));
            if (system.FilterPresent || system.KnownProductPresent)
                throw new IOException("Windows wurde neu gestartet, der zuvor entfernte Netzwerkfilter ist aber noch vorhanden. Bitte die DYNEX-Systembereinigung erneut ausführen, bevor DYNEX eingerichtet wird.");
            system.WriteOwnership(null);
            ownership = null;
        }
        ownership ??= new(system.FilterPresent || system.KnownProductPresent
            ? legacyInstallation ? SetupOwnership.Legacy : SetupOwnership.Preexisting : SetupOwnership.Pending, []);
        ownership = ownership with { Applications = ownership.Applications.Append(application).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        // Journal before MSI: an interrupted setup must not lose track of a
        // possible system change. Pending alone never proves filter ownership.
        system.WriteOwnership(ownership);
        // PC discovery is independent of the optional packet-filter download.
        // Keep LAN usable even when the MSI fails or needs another attempt.
        system.EnsureFirewall(application);
        var restart = false;
        if (!system.FilterPresent && !system.KnownProductPresent)
        {
            if (package is null) throw new InvalidDataException("Das verifizierte Einrichtungspaket fehlt.");
            ownership = ownership with { FilterOrigin = SetupOwnership.Pending };
            system.WriteOwnership(ownership);
            var installed = await system.InstallFilterAsync(package);
            if (installed.ExitCode == 1602) throw new OperationCanceledException("Die Filtereinrichtung wurde abgebrochen.");
            if (installed.ExitCode is not (0 or 3010)) throw new Win32Exception(installed.ExitCode);
            if (!system.KnownProductPresent || !system.FilterPresent) throw new IOException("Windows hat den erwarteten Netzwerkfilter nicht bestätigt.");
            ownership = ownership with { FilterOrigin = installed.InstalledByThisCall ? SetupOwnership.Owned : SetupOwnership.Preexisting };
            system.WriteOwnership(ownership);
            restart = installed.ExitCode == 3010;
        }
        return restart ? 3010 : 0;
    }

    internal static async Task<Result> RemoveAsync(IInstallerSystem system, string application, bool confirmLegacy)
    {
        try
        {
            var ownership = system.ReadOwnership();
            Validate(ownership);
            if (ownership?.RemovalPending == true)
            {
                var pending = CheckPendingBoot(system, ownership);
                ownership = pending.Ownership;
                if (!pending.RestartConfirmed)
                {
                    system.RemoveFirewall(application);
                    system.WriteOwnership(ownership with { Applications = ownership.Applications.Where(path => !string.Equals(path, application, StringComparison.OrdinalIgnoreCase)).ToArray() });
                    return new(3010, PendingRestartMessage(ownership, pending.BootAvailable));
                }
                // A confirmed restart removes the old kernel session. If the
                // package survived, retry its exact-product removal below.
                ownership = ownership with { RemovalPending = false, RemovalBootIdentity = null };
                system.WriteOwnership(ownership);
            }
            var remaining = ownership?.Applications.Where(path => !string.Equals(path, application, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
            if (remaining.Length > 0)
            {
                system.RemoveFirewall(application);
                system.WriteOwnership(ownership! with { Applications = remaining });
                return new(0, "Die Systemeinträge dieser DYNEX-Installation wurden entfernt. Der gemeinsame Netzwerkfilter bleibt für andere registrierte DYNEX-Installationen erhalten.");
            }
            if (ownership?.FilterOrigin == SetupOwnership.Preexisting)
            {
                system.RemoveFirewall(application);
                system.WriteOwnership(null);
                return new(0, "Die DYNEX-Systemeinträge wurden entfernt. Der bereits vor DYNEX vorhandene Netzwerkfilter wurde erhalten.");
            }
            if (system.FilterPresent || system.KnownProductPresent)
            {
                if (!system.KnownProductPresent)
                {
                    return new(UnsafeFilterIdentity, "Der installierte Netzwerkfilter entspricht nicht dem bekannten Windows-Packet-Filter-Paket. Er wurde nicht verändert. Die Herkunft muss vor einer Entfernung geklärt werden.");
                }
                if (ownership?.FilterOrigin != SetupOwnership.Owned && !confirmLegacy)
                    return new(LegacyConfirmationRequired, "Ein älteres DYNEX-Setup hat keinen Besitznachweis gespeichert. Installiert ist Windows Packet Filter x64 3.6.2.1. Soll dieses gemeinsame Netzwerkfilter-Paket ebenfalls entfernt werden? Andere Programme, die diesen Filter verwenden, können ihn danach nicht mehr nutzen. Die Netzwerkverbindung kann kurz unterbrochen werden.");
                var removalBoot = system.BootIdentity;
                var code = await system.RemoveFilterAsync();
                if (code == 1602) return new(1602, "Die Entfernung des Netzwerkfilters wurde abgebrochen. DYNEX und sein Besitznachweis bleiben für einen erneuten Versuch erhalten.");
                if (code is not (0 or 3010 or 1605)) return new(code, $"Windows konnte den Netzwerkfilter nicht entfernen (Fehler {code}). Die Deinstallation wurde nicht als vollständig bestätigt.");
                if (code != 3010 && (system.FilterPresent || system.KnownProductPresent))
                    return new(1, "Der Netzwerkfilter ist nach der Entfernung noch vorhanden. DYNEX bleibt für einen erneuten Versuch erhalten.");
                // Record a deferred removal before touching the firewall. Even a
                // later firewall failure must preserve the reboot requirement.
                ownership = new(SetupOwnership.Owned, [], code == 3010, code == 3010 ? removalBoot : null);
                system.WriteOwnership(ownership);
                system.RemoveFirewall(application);
                if (code == 3010)
                    return new(3010, "Die DYNEX-Systemeinträge und das Netzwerkfilter-Paket wurden entfernt. Windows muss neu gestartet werden, damit die Filterentfernung vollständig wirksam ist.");
            }
            system.RemoveFirewall(application);
            system.WriteOwnership(null);
            return new(0, "Die DYNEX-Systemeinträge und der von DYNEX eingerichtete Netzwerkfilter wurden vollständig entfernt.");
        }
        catch (Exception error) when (error is OperationCanceledException || error is Win32Exception { NativeErrorCode: 1223 })
        { return new(1602, "Die Windows-Rückfrage oder Entfernung wurde abgebrochen. DYNEX bleibt für einen erneuten Versuch erhalten."); }
        catch (Win32Exception error) { return new(error.NativeErrorCode == 0 ? 1 : error.NativeErrorCode, "Die Systembereinigung ist fehlgeschlagen: " + error.Message); }
        catch (Exception error) { return new(1, "Die Systembereinigung ist nicht vollständig: " + error.Message); }
    }

    private static void Validate(SetupOwnership? ownership)
    {
        if (ownership is null) return;
        if (ownership.FilterOrigin is not (SetupOwnership.Owned or SetupOwnership.Preexisting or SetupOwnership.Pending or SetupOwnership.Legacy) ||
            ownership.Applications.Length > 128 || ownership.Applications.Any(path => !ValidApplication(path)))
            throw new InvalidDataException("Der geschützte DYNEX-Besitznachweis ist ungültig; keine automatische Filterentfernung.");
    }

    private static (SetupOwnership Ownership, bool RestartConfirmed, bool BootAvailable) CheckPendingBoot(IInstallerSystem system, SetupOwnership ownership)
    {
        var current = system.BootIdentity;
        if (ownership.RemovalBootIdentity is null && current is not null)
        {
            // No previous boot witness: establish one, then require a later
            // restart. Driver registry absence alone never proves unload.
            ownership = ownership with { RemovalBootIdentity = current };
            system.WriteOwnership(ownership);
        }
        return (ownership, current is not null && ownership.RemovalBootIdentity is not null &&
            !string.Equals(current, ownership.RemovalBootIdentity, StringComparison.Ordinal), current is not null);
    }

    private static string PendingRestartMessage(SetupOwnership ownership, bool bootAvailable = true) => ownership.RemovalBootIdentity is null || !bootAvailable
        ? "Die Filterentfernung ist noch nicht vollständig bestätigt. Windows konnte den letzten Systemstart nicht zuverlässig ermitteln. Bitte Windows neu starten und die DYNEX-Systembereinigung erneut ausführen; wenn die Meldung bleibt, die Windows-Verwaltungsinstrumentation (WMI) prüfen lassen. Es wurde kein vollständiger Erfolg gemeldet."
        : "Die Filterentfernung benötigt einen bestätigten Windows-Neustart. Bitte Windows über Neu starten neu starten und danach DYNEX erneut einrichten oder die Systembereinigung wiederholen. Eine verschwundene Registrierung allein bestätigt nicht, dass der Filter bereits entladen ist.";

    internal static bool ValidApplication(string path) => Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) &&
        string.Equals(Path.GetFileName(path), "DYNEX.exe", StringComparison.OrdinalIgnoreCase) && !path.Contains('"');
}

internal sealed class WindowsInstallerSystem : IInstallerSystem
{
    private const string ReceiptKey = @"SOFTWARE\DYNEX\Setup";
    internal const string ProductCode = "{4EC4289E-7F4B-424C-8EA9-B7AC9850FFBE}";
    public bool FilterPresent => PacketFilterSetup.IsInstalled;
    public string? BootIdentity => ReadBootIdentity();
    public bool KnownProductPresent
    {
        get
        {
            var state = MsiQueryProductState(ProductCode);
            if (state == -1) return false; // INSTALLSTATE_UNKNOWN
            if (state != 5) throw new IOException($"Windows meldet einen unvollständigen MSI-Zustand ({state}) für das Netzwerkfilter-Paket.");
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var product = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + ProductCode);
            if (product?.GetValue("DisplayName") as string != "Windows Packet Filter x64" ||
                product.GetValue("DisplayVersion") as string != "3.6.2.1" || product.GetValue("Publisher") as string != "NT KERNEL")
                throw new InvalidDataException("Die MSI-Produktidentität des Netzwerkfilters stimmt nicht mit dem geprüften DYNEX-Paket überein.");
            return true;
        }
    }

    public SetupOwnership? ReadOwnership()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(ReceiptKey);
        if (key is null) return null;
        if (key.GetValue("ProductCode") as string != ProductCode || key.GetValue("Schema") is not int schema || schema != 1 ||
            key.GetValue("Ownership") is not string json) throw new InvalidDataException("Der DYNEX-Besitznachweis ist unvollständig oder nicht unterstützt.");
        return JsonSerializer.Deserialize<SetupOwnership>(json) ?? throw new InvalidDataException("Der DYNEX-Besitznachweis ist leer.");
    }

    public void WriteOwnership(SetupOwnership? ownership)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        if (ownership is null) { machine.DeleteSubKeyTree(ReceiptKey, false); return; }
        using var key = machine.CreateSubKey(ReceiptKey, true);
        var security = new RegistrySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(sid, null), RegistryRights.FullControl,
                InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), RegistryRights.ReadKey,
            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        key.SetAccessControl(security);
        key.SetValue("Schema", 1, RegistryValueKind.DWord);
        key.SetValue("ProductCode", ProductCode, RegistryValueKind.String);
        key.SetValue("Ownership", JsonSerializer.Serialize(ownership), RegistryValueKind.String);
        key.Flush();
    }

    public Task<PacketFilterSetup.InstallResult> InstallFilterAsync(string package) => PacketFilterSetup.InstallPackageTrackedAsync(package, _ => { });
    public async Task<int> RemoveFilterAsync()
    {
        // Revalidate immediately before msiexec; never uninstall by display-name,
        // wildcard, caller supplied product code, registry command or executable.
        if (!KnownProductPresent) return 1605;
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        { UseShellExecute = false, CreateNoWindow = true, Arguments = $"/x {ProductCode} /passive /norestart" })
            ?? throw new IOException("Die Windows-Filterentfernung konnte nicht gestartet werden.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
    public void EnsureFirewall(string application) => InstallerFirewall.EnsureConfigured(application);
    public void RemoveFirewall(string application) => InstallerFirewall.RemoveConfigured(application);

    internal static string? ReadBootIdentity()
    {
        // Documented Win32_OperatingSystem.LastBootUpTime (CIM_DATETIME).
        // No uptime subtraction or undocumented kernel structure is needed.
        object? locator = null, services = null, rows = null, row = null, properties = null, property = null;
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)!);
            services = ((dynamic)locator!).ConnectServer(".", @"root\cimv2");
            rows = ((dynamic)services).ExecQuery("SELECT LastBootUpTime FROM Win32_OperatingSystem", "WQL", 0);
            if ((int)((dynamic)rows).Count != 1) return null;
            row = ((dynamic)rows).ItemIndex(0);
            properties = ((dynamic)row).Properties_;
            property = ((dynamic)properties).Item("LastBootUpTime", 0);
            return ParseBootIdentity((string)((dynamic)property).Value);
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or System.Security.SecurityException or FormatException or ArgumentException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally
        {
            foreach (var value in new[] { property, properties, row, rows, services, locator })
                if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
        return null;
    }

    internal static string? ParseBootIdentity(string value)
    {
        if (value.Length != 25 || value[14] != '.' || value[21] is not ('+' or '-') || value.Contains('*')) return null;
        if (!DateTime.TryParseExact(value[..21], "yyyyMMddHHmmss.ffffff", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var local) ||
            !int.TryParse(value.AsSpan(22, 3), out var offsetMinutes) || offsetMinutes > 14 * 60) return null;
        var offset = TimeSpan.FromMinutes(value[21] == '-' ? -offsetMinutes : offsetMinutes);
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset).UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiQueryProductStateW")]
    private static extern int MsiQueryProductState(string product);
}
