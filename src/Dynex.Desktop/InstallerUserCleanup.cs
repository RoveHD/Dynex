using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Dynex.Desktop;

/// <summary>Removes only DYNEX-owned per-user state after machine cleanup has succeeded.</summary>
internal static class InstallerUserCleanup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DYNEX";

    internal sealed record Result(int ExitCode, string Message);
    internal sealed record Paths(string Profile, string Install, string Startup, string Programs, string Desktop);

    internal interface IRegistryValues
    {
        string? ReadRunValue(string name);
        void DeleteRunValue(string name);
    }

    internal interface IShortcutTargets
    {
        string? ReadTarget(string shortcut);
    }

    internal static Result Run()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(local) || string.IsNullOrWhiteSpace(roaming) || string.IsNullOrWhiteSpace(startMenu) || string.IsNullOrWhiteSpace(desktop))
            return new(1, "Windows konnte die DYNEX-Benutzerordner nicht bestimmen.");

        var paths = new Paths(
            Path.Combine(local, "DYNEX"),
            Path.Combine(local, "Programs", "DYNEX"),
            Path.Combine(roaming, "Microsoft", "Windows", "Start Menu", "Programs", "Startup"),
            Path.Combine(startMenu, "Programs"),
            desktop);
        return Run(paths, new CurrentUserRunValues(), new WindowsShortcutTargets());
    }

    // Injected roots and adapters keep tests isolated; the public installer CLI uses only Run().
    internal static Result Run(Paths paths, IRegistryValues registry, IShortcutTargets shortcuts)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(shortcuts);
        var errors = new List<string>();
        var notes = new List<string>();
        string installedExe;
        try { installedExe = Path.GetFullPath(Path.Combine(paths.Install, "DYNEX.exe")); }
        catch (Exception e) when (IsExpected(e)) { return new(1, "Der DYNEX-Installationspfad ist ungültig: " + e.Message); }

        try
        {
            var value = registry.ReadRunValue(RunValue);
            if (value is not null)
            {
                var target = ExecutableFromCommand(value);
                if (target is not null && SamePath(target, installedExe)) registry.DeleteRunValue(RunValue);
                else notes.Add("Der Autostartwert „DYNEX“ zeigt nicht auf diese DYNEX-Installation und wurde zum Schutz eines möglicherweise fremden Eintrags beibehalten.");
            }
        }
        catch (Exception e) when (IsExpected(e)) { errors.Add("Autostart: " + e.Message); }

        try { DeleteTreeWithoutFollowingLinks(paths.Profile); }
        catch (Exception e) when (IsExpected(e)) { errors.Add("Benutzerprofil: " + e.Message); }

        var shortcutDirectory = Path.Combine(paths.Programs, "DYNEX");
        var processStartMenu = true;
        try
        {
            if (Directory.Exists(shortcutDirectory) && (File.GetAttributes(shortcutDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                processStartMenu = false;
                notes.Add("Der DYNEX-Startmenüordner ist ein Link und wurde zum Schutz seines Ziels nicht verändert.");
            }
        }
        catch (Exception e) when (IsExpected(e)) { errors.Add("DYNEX-Startmenüordner: " + e.Message); processStartMenu = false; }
        if (processStartMenu)
        {
            RemoveShortcut(Path.Combine(shortcutDirectory, "DYNEX.lnk"), installedExe, "Startmenü-Verknüpfung", shortcuts, errors, notes);
            RemoveShortcut(Path.Combine(shortcutDirectory, "Installation.lnk"), Path.Combine(paths.Install, "docs", "installation.md"), "Installations-Verknüpfung", shortcuts, errors, notes);
        }
        RemoveShortcut(Path.Combine(paths.Desktop, "DYNEX.lnk"), installedExe, "Desktop-Verknüpfung", shortcuts, errors, notes);
        try
        {
            if (processStartMenu && Directory.Exists(shortcutDirectory) && (File.GetAttributes(shortcutDirectory) & FileAttributes.ReparsePoint) == 0)
            {
                if (Directory.EnumerateFileSystemEntries(shortcutDirectory).Any())
                    notes.Add("Der DYNEX-Startmenüordner enthält fremde Dateien und wurde beibehalten.");
                else
                {
                    try { Directory.Delete(shortcutDirectory, recursive: false); }
                    catch (IOException) when (Directory.Exists(shortcutDirectory) && Directory.EnumerateFileSystemEntries(shortcutDirectory).Any())
                    { notes.Add("Der DYNEX-Startmenüordner enthält fremde Dateien und wurde beibehalten."); }
                }
            }
        }
        catch (Exception e) when (IsExpected(e)) { errors.Add("DYNEX-Startmenüordner: " + e.Message); }

        if (errors.Count > 0)
        {
            var message = "DYNEX-Benutzerdaten konnten nicht vollständig entfernt werden: " + string.Join("; ", errors);
            if (notes.Count > 0) message += " " + string.Join(" ", notes);
            return new(1, message);
        }
        return new(0, notes.Count == 0
            ? "DYNEX-Benutzerprofil, eigene Autostart- und Verknüpfungseinträge wurden entfernt."
            : "DYNEX-Benutzerdaten wurden bereinigt. " + string.Join(" ", notes));
    }

    private static void RemoveShortcut(string shortcutPath, string expectedTarget, string description,
        IShortcutTargets shortcuts, List<string> errors, List<string> notes)
    {
        try
        {
            if (!File.Exists(shortcutPath)) return;
            var target = shortcuts.ReadTarget(shortcutPath);
            if (target is not null && SamePath(target, expectedTarget)) File.Delete(shortcutPath);
            else notes.Add($"Die {description} wurde beibehalten, da ihr Ziel nicht eindeutig zu DYNEX gehört.");
        }
        catch (Exception e) when (IsExpected(e)) { errors.Add(description + ": " + e.Message); }
    }

    private static void DeleteTreeWithoutFollowingLinks(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Der Profilpfad fehlt.");
        var full = Path.GetFullPath(root);
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full));
        if (string.IsNullOrEmpty(parent) || SamePath(full, parent)) throw new IOException("Der Profilpfad ist kein sicherer Unterordner.");
        DeleteEntry(full);
    }

    private static void DeleteEntry(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            // Deleting a link itself never enumerates or traverses its target.
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path, recursive: false);
            else File.Delete(path);
            return;
        }

        if ((attributes & FileAttributes.Directory) == 0) { File.Delete(path); return; }
        foreach (var child in Directory.EnumerateFileSystemEntries(path)) DeleteEntry(child);
        Directory.Delete(path, recursive: false);
    }

    private static string? ExecutableFromCommand(string command)
    {
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.Length == 0) return null;
        string executable;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end < 0) return null;
            executable = text[1..end];
        }
        else
        {
            var end = text.IndexOfAny([' ', '\t']);
            executable = end < 0 ? text : text[..end];
        }
        try { return Path.GetFullPath(executable); }
        catch (Exception e) when (IsExpected(e)) { return null; }
    }

    private static bool SamePath(string left, string right)
    {
        try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (IsExpected(e)) { return false; }
    }

    private static bool IsExpected(Exception e) => e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException or COMException or InvalidOperationException or TargetInvocationException;

    private sealed class CurrentUserRunValues : IRegistryValues
    {
        public string? ReadRunValue(string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
            return key.GetValue(name) as string ?? string.Empty;
        }
        public void DeleteRunValue(string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    private sealed class WindowsShortcutTargets : IShortcutTargets
    {
        public string? ReadTarget(string shortcut)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows-Verknüpfungen können nur unter Windows gelesen werden.");
            var type = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!;
            object? shell = null;
            object? link = null;
            try
            {
                shell = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Windows-Shell ist nicht verfügbar.");
                link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcut]);
                return link?.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string;
            }
            finally
            {
                if (link is not null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
                if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
        }
    }
}
