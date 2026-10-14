using Dynex.Desktop;
using System.Diagnostics;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    checks++;
}

var root = Path.Combine(Path.GetTempPath(), "DYNEX-user-cleanup-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var paths = PathsFor(Path.Combine(root, "case1"));
    Directory.CreateDirectory(paths.Profile);
    Directory.CreateDirectory(Path.Combine(paths.Profile, "updates", "cache"));
    File.WriteAllText(Path.Combine(paths.Profile, "settings.json"), "settings");
    File.WriteAllText(Path.Combine(paths.Profile, "identity.dpapi"), "identity");
    File.WriteAllText(Path.Combine(paths.Profile, "updates", "cache", "setup.exe"), "update");
    Directory.CreateDirectory(Path.Combine(paths.Install, "docs"));
    File.WriteAllText(Path.Combine(paths.Install, "DYNEX.exe"), "app");
    File.WriteAllText(Path.Combine(paths.Install, "docs", "installation.md"), "docs");
    Directory.CreateDirectory(Path.Combine(paths.Programs, "DYNEX"));
    Directory.CreateDirectory(paths.Desktop);
    Directory.CreateDirectory(paths.Startup);
    var installedTarget = Path.Combine(paths.Install, "DYNEX.exe");
    var installationTarget = Path.Combine(paths.Install, "docs", "installation.md");
    File.WriteAllText(Path.Combine(paths.Programs, "DYNEX", "DYNEX.lnk"), "link");
    File.WriteAllText(Path.Combine(paths.Programs, "DYNEX", "Installation.lnk"), "link");
    File.WriteAllText(Path.Combine(paths.Desktop, "DYNEX.lnk"), "link");
    var registry = new FakeRegistry { RunValue = $"\"{installedTarget}\" --tray" };
    var shortcuts = new FakeShortcuts();
    shortcuts.Targets[Path.Combine(paths.Programs, "DYNEX", "DYNEX.lnk")] = installedTarget;
    shortcuts.Targets[Path.Combine(paths.Programs, "DYNEX", "Installation.lnk")] = installationTarget;
    shortcuts.Targets[Path.Combine(paths.Desktop, "DYNEX.lnk")] = installedTarget;

    var result = InstallerUserCleanup.Run(paths, registry, shortcuts);
    Check(result.ExitCode == 0 && !Directory.Exists(paths.Profile), "DYNEX profile including settings, pairing identity and update cache is removed");
    Check(registry.RunValue is null, "owned DYNEX startup value is removed only when it points at this install");
    Check(!Directory.Exists(Path.Combine(paths.Programs, "DYNEX")) && !File.Exists(Path.Combine(paths.Desktop, "DYNEX.lnk")), "only verified owned shortcuts are removed");
    Check(File.Exists(installedTarget) && File.Exists(installationTarget), "installer cleanup does not delete install files");

    var missing = PathsFor(Path.Combine(root, "missing"));
    var idempotent = InstallerUserCleanup.Run(missing, new FakeRegistry(), new FakeShortcuts());
    Check(idempotent.ExitCode == 0, "missing profile and shortcuts are an idempotent success");

    var foreign = PathsFor(Path.Combine(root, "foreign"));
    Directory.CreateDirectory(foreign.Profile);
    File.WriteAllText(Path.Combine(foreign.Profile, "settings.json"), "settings");
    Directory.CreateDirectory(foreign.Desktop);
    Directory.CreateDirectory(Path.Combine(foreign.Programs, "DYNEX"));
    File.WriteAllText(Path.Combine(foreign.Desktop, "DYNEX.lnk"), "foreign shortcut");
    var foreignStartShortcut = Path.Combine(foreign.Programs, "DYNEX", "DYNEX.lnk");
    File.WriteAllText(foreignStartShortcut, "foreign start shortcut");
    var foreignRegistry = new FakeRegistry { RunValue = @"C:\OtherApp\DYNEX.exe --tray" };
    var foreignShortcuts = new FakeShortcuts();
    foreignShortcuts.Targets[Path.Combine(foreign.Desktop, "DYNEX.lnk")] = @"C:\OtherApp\unrelated.exe";
    foreignShortcuts.Targets[foreignStartShortcut] = @"C:\OtherApp\unrelated.exe";
    result = InstallerUserCleanup.Run(foreign, foreignRegistry, foreignShortcuts);
    Check(result.ExitCode == 0 && foreignRegistry.RunValue is not null && File.Exists(Path.Combine(foreign.Desktop, "DYNEX.lnk")) &&
          File.Exists(foreignStartShortcut) && Directory.Exists(Path.Combine(foreign.Programs, "DYNEX")) && result.Message.Contains("beibehalten"),
        "foreign same-name startup value and desktop/start-menu shortcuts are preserved with a clear note");

    var failing = PathsFor(Path.Combine(root, "failure"));
    Directory.CreateDirectory(failing.Profile);
    File.WriteAllText(Path.Combine(failing.Profile, "settings.json"), "settings");
    var failingRegistry = new FakeRegistry { RunValue = $"\"{Path.Combine(failing.Install, "DYNEX.exe")}\"", FailDelete = true };
    result = InstallerUserCleanup.Run(failing, failingRegistry, new FakeShortcuts());
    Check(result.ExitCode != 0 && result.Message.Contains("Autostart"), "cleanup errors remain failures for retry instead of reporting success");

    var locked = PathsFor(Path.Combine(root, "locked"));
    Directory.CreateDirectory(locked.Profile);
    var lockedPath = Path.Combine(locked.Profile, "settings.json");
    File.WriteAllText(lockedPath, "settings");
    using (new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        result = InstallerUserCleanup.Run(locked, new FakeRegistry(), new FakeShortcuts());
        Check(result.ExitCode != 0 && result.Message.Contains("Benutzerprofil"), "locked profile file prevents false cleanup success");
    }
    result = InstallerUserCleanup.Run(locked, new FakeRegistry(), new FakeShortcuts());
    Check(result.ExitCode == 0 && !Directory.Exists(locked.Profile), "locked profile cleanup succeeds when retried after release");

    var outside = Path.Combine(root, "outside");
    Directory.CreateDirectory(outside);
    var sentinel = Path.Combine(outside, "keep.txt");
    File.WriteAllText(sentinel, "outside");
    var linked = PathsFor(Path.Combine(root, "linked"));
    Directory.CreateDirectory(linked.Profile);
    var junction = Path.Combine(linked.Profile, "external");
    var linkCreated = TryCreateDirectoryLink(junction, outside);
    result = InstallerUserCleanup.Run(linked, new FakeRegistry(), new FakeShortcuts());
    Check(result.ExitCode == 0 && File.Exists(sentinel), linkCreated
        ? "profile cleanup removes a junction without traversing or deleting its target"
        : "profile cleanup removes normal files and remains safe when junction creation is unavailable");

    if (linkCreated)
    {
        var linkedMenu = PathsFor(Path.Combine(root, "linked-menu"));
        Directory.CreateDirectory(linkedMenu.Profile);
        Directory.CreateDirectory(linkedMenu.Programs);
        var menuSentinel = Path.Combine(outside, "DYNEX.lnk");
        File.WriteAllText(menuSentinel, "outside shortcut");
        var menuLink = Path.Combine(linkedMenu.Programs, "DYNEX");
        Check(TryCreateDirectoryLink(menuLink, outside), "isolated start-menu junction fixture created");
        result = InstallerUserCleanup.Run(linkedMenu, new FakeRegistry(), new FakeShortcuts());
        Check(result.ExitCode == 0 && File.Exists(menuSentinel) && Directory.Exists(menuLink) && result.Message.Contains("Link"),
            "start-menu junction is skipped without probing or deleting entries through it");
    }
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

Console.WriteLine($"User cleanup checks: {checks} passed.");

static InstallerUserCleanup.Paths PathsFor(string root) => new(
    Path.Combine(root, "LocalAppData", "DYNEX"),
    Path.Combine(root, "LocalAppData", "Programs", "DYNEX"),
    Path.Combine(root, "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup"),
    Path.Combine(root, "Roaming", "Microsoft", "Windows", "Start Menu", "Programs"),
    Path.Combine(root, "Desktop"));

static bool TryCreateDirectoryLink(string link, string target)
{
    try { Directory.CreateSymbolicLink(link, target); return true; }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "/c", "mklink", "/J", link, target }
            });
            return process is not null && process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch (Exception fallbackError) when (fallbackError is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return false; }
    }
}

sealed class FakeRegistry : InstallerUserCleanup.IRegistryValues
{
    public string? RunValue { get; set; }
    public bool FailDelete { get; set; }
    public string? ReadRunValue(string name) => name == "DYNEX" ? RunValue : null;
    public void DeleteRunValue(string name)
    {
        if (FailDelete) throw new UnauthorizedAccessException("isolated test failure");
        if (name == "DYNEX") RunValue = null;
    }
}

sealed class FakeShortcuts : InstallerUserCleanup.IShortcutTargets
{
    public Dictionary<string, string> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ReadTarget(string shortcut) => Targets.GetValueOrDefault(shortcut);
}
