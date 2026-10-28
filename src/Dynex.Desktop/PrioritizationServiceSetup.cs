using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Dynex.Desktop;

/// <summary>Installer-only provisioning. Runtime never elevates or runs a helper from a user-writable folder.</summary>
internal static class PrioritizationServiceSetup
{
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DYNEX", "Prioritization");
    private static readonly Lazy<PrioritizationPayload> Payload = new(PrioritizationPayload.Load);
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\DYNEX.Prioritization";
    private const string RegistryPath = @"SOFTWARE\DYNEX\Prioritization";
    private const string ServiceName = "DYNEX.Prioritization";
    private const string ServiceSecurity = "D:(D;;RPWPDCWDWO;;;NU)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCRP;;;AU)";

    internal static bool IsConfigured(string? application = null)
    {
        try
        {
            application ??= InstallerFirewall.ApplicationPath;
            using var identity = WindowsIdentity.GetCurrent();
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var receipt = machine.OpenSubKey(RegistryPath);
            using var client = receipt?.OpenSubKey(@"Clients\" + identity.User!.Value);
            if (receipt?.GetValue("Schema") is not int schema || schema != 1 ||
                receipt.GetValue("PayloadHash") as string != Payload.Value.Hash ||
                !string.Equals(client?.GetValue("ApplicationPath") as string, application, StringComparison.OrdinalIgnoreCase)) return false;
            var expected = Path.Combine(Root, Payload.Value.Hash, "Dynex.Shaper.exe");
            var command = ReadManagedServiceCommand(machine);
            if (command is null || !string.Equals(command, ServiceCommand(expected), StringComparison.OrdinalIgnoreCase)) return false;
            CheckProtectedDirectory(Root);
            CheckProtectedDirectory(Path.GetDirectoryName(expected)!);
            return File.Exists(expected) && File.Exists(Path.Combine(Path.GetDirectoryName(expected)!, "Dynex.Shaper.dll"));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    internal static async Task EnsureAsync(string application)
    {
        var sid = ResolveInstallationSid(application);
        var payload = Payload.Value;
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var previousCommand = ReadManagedServiceCommand(machine);
        EnsureProtectedDirectory(Path.GetDirectoryName(Root)!);
        EnsureProtectedDirectory(Root);
        var directory = Path.Combine(Root, payload.Hash);
        if (Directory.Exists(directory))
        {
            CheckProtectedDirectory(directory);
            payload.VerifyDirectory(directory);
        }
        else
        {
            var staging = Path.Combine(Root, ".stage-" + Guid.NewGuid().ToString("N"));
            EnsureProtectedDirectory(staging);
            try
            {
                payload.CopyVerified(Path.Combine(AppContext.BaseDirectory, "shaper"), staging);
                payload.VerifyDirectory(staging);
                Directory.Move(staging, directory);
            }
            finally { if (Directory.Exists(staging)) DeleteOwnedDirectory(staging); }
        }
        var command = ServiceCommand(Path.Combine(directory, "Dynex.Shaper.exe"));
        var changed = !string.Equals(command, previousCommand, StringComparison.OrdinalIgnoreCase);
        using var receipt = machine.CreateSubKey(RegistryPath, true);
        SecureRegistry(receipt);
        var previousSchema = receipt.GetValue("Schema");
        var previousHash = receipt.GetValue("PayloadHash");
        using var clients = receipt.CreateSubKey("Clients", true);
        using var client = clients.CreateSubKey(sid, true);
        var previousApplication = client.GetValue("ApplicationPath");
        if (changed && previousCommand is not null) await StopAsync();
        try
        {
            client.SetValue("ApplicationPath", application, RegistryValueKind.String);
            receipt.SetValue("Schema", 1, RegistryValueKind.DWord);
            receipt.SetValue("PayloadHash", payload.Hash, RegistryValueKind.String);
            receipt.Flush();
            if (previousCommand is null)
                await ScAsync("create", ServiceName, "binPath=", command, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "DYNEX Paketpriorisierung");
            else if (changed)
                await ScAsync("config", ServiceName, "binPath=", command, "start=", "auto", "obj=", "LocalSystem");
            await ScAsync("sdset", ServiceName, ServiceSecurity);
            await StartAsync();
        }
        catch
        {
            // A failed upgrade retains the former trusted binary and registration.
            // For a first install retain the complete journal: a failed service
            // start must remain removable/recoverable, not an unowned SYSTEM service.
            if (previousCommand is not null)
            {
                RestoreValue(receipt, "Schema", previousSchema);
                RestoreValue(receipt, "PayloadHash", previousHash);
                RestoreValue(client, "ApplicationPath", previousApplication);
                if (previousApplication is null) clients.DeleteSubKey(sid, false);
                receipt.Flush();
            }
            if (previousCommand is not null && changed)
            {
                try
                {
                    await StopAsync();
                    await ScAsync("config", ServiceName, "binPath=", previousCommand, "start=", "auto", "obj=", "LocalSystem");
                    await StartAsync();
                }
                catch (Exception rollback) { throw new IOException("Die Dienstaktualisierung und das Wiederherstellen des vorherigen Dienstes sind fehlgeschlagen. DYNEX-Setup erneut starten.", rollback); }
            }
            throw;
        }
    }

    internal static async Task RemoveAsync(string application)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var receipt = machine.OpenSubKey(RegistryPath, true);
        if (receipt is null) return; // Releases predating the service never owned it.
        if (receipt.GetValue("Schema") is not int schema || schema != 1) throw new InvalidDataException("Der DYNEX-Dienstnachweis ist ungültig.");
        using var clients = receipt.OpenSubKey("Clients", true) ?? throw new InvalidDataException("DYNEX-Dienstregistrierungen fehlen.");
        var owned = new List<string>();
        var others = 0;
        foreach (var name in clients.GetSubKeyNames())
        {
            using var client = clients.OpenSubKey(name);
            if (client?.GetValue("ApplicationPath") is not string path || !InstallerSystem.ValidApplication(path))
                throw new InvalidDataException("Eine DYNEX-Dienstregistrierung ist ungültig.");
            if (string.Equals(path, application, StringComparison.OrdinalIgnoreCase)) owned.Add(name);
            else others++;
        }
        if (others > 0)
        {
            foreach (var name in owned) clients.DeleteSubKey(name);
            return;
        }
        // An empty receipt also belongs to an interrupted/previous uninstall.
        if (ReadManagedServiceCommand(machine) is not null)
        {
            await StopAsync();
            await ScAsync("delete", ServiceName);
        }
        if (Directory.Exists(Root))
        {
            CheckProtectedDirectory(Root);
            foreach (var directory in Directory.EnumerateDirectories(Root))
            {
                var hash = Path.GetFileName(directory);
                if (!PrioritizationPayload.ValidHash(hash)) continue; // Preserve unknown files/directories.
                CheckProtectedDirectory(directory);
                var manifest = new PrioritizationPayload(File.ReadAllBytes(Path.Combine(directory, PrioritizationPayload.ReceiptName)));
                if (manifest.Hash != hash) throw new InvalidDataException("Der geschützte DYNEX-Dienstnachweis stimmt nicht mit seinem Verzeichnis überein.");
                manifest.VerifyDirectory(directory);
                DeleteOwnedDirectory(directory);
            }
            if (!Directory.EnumerateFileSystemEntries(Root).Any()) Directory.Delete(Root);
        }
        machine.DeleteSubKeyTree(RegistryPath, false);
    }

    private static void RestoreValue(RegistryKey key, string name, object? value)
    {
        if (value is null) key.DeleteValue(name, false);
        else key.SetValue(name, value);
    }

    internal static string ServiceCommand(string executable) => "\"" + executable + "\" --service";

    internal static bool IsManagedCommand(string command)
    {
        var prefix = Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!command.StartsWith('"') || !command.EndsWith("\\Dynex.Shaper.exe\" --service", StringComparison.OrdinalIgnoreCase)) return false;
        var path = command[1..^"\" --service".Length];
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var relative = Path.GetRelativePath(Root, path);
        var parts = relative.Split(Path.DirectorySeparatorChar);
        return parts.Length == 2 && PrioritizationPayload.ValidHash(parts[0]) && parts[1].Equals("Dynex.Shaper.exe", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadManagedServiceCommand(RegistryKey machine)
    {
        using var service = machine.OpenSubKey(ServiceKey);
        if (service is null) return null;
        if (service.GetValue("ImagePath") is not string command || !IsManagedCommand(command) ||
            service.GetValue("ObjectName") as string != "LocalSystem" || service.GetValue("Type") is not int type || type != 0x10 ||
            service.GetValue("Start") is not int start || start != 2)
            throw new InvalidDataException("Der vorhandene DYNEX-Dienst hat eine unerwartete Konfiguration und wurde nicht verändert.");
        PrioritizationPayload.RejectReparsePoints(Path.GetDirectoryName(command[1..^"\" --service".Length])!);
        return command;
    }

    internal static string ResolveInstallationSid(string application)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var profiles = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList")!;
        foreach (var sid in profiles.GetSubKeyNames())
        {
            if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !sid.StartsWith("S-1-12-1-", StringComparison.Ordinal)) continue;
            try
            {
                if (string.Equals(InstallerPrerequisites.CleanupApplication(sid), application, StringComparison.OrdinalIgnoreCase)) return sid;
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException) { /* An unrelated stale/.bak profile cannot identify this installation. */ }
        }
        throw new InvalidDataException("Die ursprüngliche DYNEX-Benutzerinstallation konnte nicht bestätigt werden. Bitte das DYNEX-Setup im ursprünglichen Windows-Benutzerkonto verwenden.");
    }

    private static void SecureRegistry(RegistryKey key)
    {
        var security = new RegistrySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(sid, null), RegistryRights.FullControl,
                InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), RegistryRights.ReadKey,
            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        key.SetAccessControl(security);
    }

    private static void EnsureProtectedDirectory(string path)
    {
        PrioritizationPayload.RejectReparsePoints(path);
        if (Directory.Exists(path)) { CheckProtectedDirectory(path); return; }
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        FileSystemAclExtensions.Create(new DirectoryInfo(path), security);
        CheckProtectedDirectory(path);
    }

    private static void CheckProtectedDirectory(string path)
    {
        PrioritizationPayload.RejectReparsePoints(path);
        var security = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path), AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !IsPrivileged(owner)) throw new InvalidDataException("Der DYNEX-Dienstordner hat einen unerwarteten Besitzer.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow &&
                (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                    FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0 && !IsPrivileged((SecurityIdentifier)rule.IdentityReference))
                throw new InvalidDataException("Der DYNEX-Dienstordner ist nicht gegen Änderungen geschützt.");
    }

    private static bool IsPrivileged(SecurityIdentifier sid) => sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid);

    private static void DeleteOwnedDirectory(string path)
    {
        var absolute = Path.GetFullPath(path);
        if (!absolute.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Die Dienstbereinigung liegt außerhalb des DYNEX-Dienstordners.");
        PrioritizationPayload.RejectReparsePoints(absolute);
        foreach (var item in Directory.EnumerateFileSystemEntries(absolute))
        {
            var attributes = File.GetAttributes(item);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Verknüpfung bei der DYNEX-Dienstbereinigung.");
            if ((attributes & FileAttributes.Directory) != 0) DeleteOwnedDirectory(item);
            else File.Delete(item);
        }
        Directory.Delete(absolute);
    }

    private static async Task ScAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Die Windows-Dienstverwaltung konnte nicht gestartet werden.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        // Do not abandon an outstanding SCM mutation and race it with rollback.
        // State transitions themselves are checked with a deadline below.
        await process.WaitForExitAsync();
        await Task.WhenAll(output, error);
        if (process.ExitCode != 0) throw new Win32Exception(process.ExitCode, $"Die DYNEX-Diensteinrichtung ist fehlgeschlagen (Windows-Code {process.ExitCode}).");
    }

    private static async Task StartAsync()
    {
        var state = ReadState();
        if (state == 1) await ScAsync("start", ServiceName);
        await WaitForStateAsync(4);
    }

    private static async Task StopAsync()
    {
        var state = ReadState(out var exitCode);
        if (state == 1) { CheckCleanStop(exitCode); return; }
        if (state == 2) { await WaitForStateAsync(4); state = 4; }
        if (state != 3) await ScAsync("stop", ServiceName);
        await WaitForStateAsync(1);
    }

    private static async Task WaitForStateAsync(uint expected)
    {
        var deadline = Stopwatch.GetTimestamp();
        while (true)
        {
            var state = ReadState(out var exitCode);
            if (state == expected) { if (expected == 1) CheckCleanStop(exitCode); return; }
            if (expected == 4 && state == 1) throw new IOException($"Der DYNEX-Dienst konnte nicht starten (Windows-Code {exitCode}).");
            if (Stopwatch.GetElapsedTime(deadline).TotalSeconds > 25) throw new IOException("Der DYNEX-Dienst hat seinen Start oder das saubere Beenden nicht bestätigt.");
            await Task.Delay(100);
        }
    }

    internal static void CheckCleanStop(uint exitCode)
    {
        if (exitCode != 0) throw new IOException($"Der DYNEX-Dienst hat keine erfolgreiche Freigabe bestätigt (Windows-Code {exitCode}). Die Systemkomponenten wurden nicht als sauber beendet bestätigt.");
    }

    private static uint ReadState() => ReadState(out _);
    private static uint ReadState(out uint exitCode)
    {
        using var manager = OpenSCManager(null, null, 1);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var service = OpenService(manager, ServiceName, 4);
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!QueryServiceStatus(service, out var status)) throw new Win32Exception(Marshal.GetLastWin32Error());
        exitCode = status.ExitCode;
        return status.State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus { public uint Type, State, Accepted, ExitCode, SpecificExitCode, Checkpoint, WaitHint; }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(ServiceHandle service, out ServiceStatus status);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
