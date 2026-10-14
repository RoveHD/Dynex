using System.ComponentModel;
using Dynex.Desktop;

if (args is ["--probe-firewall"])
{
    try { Console.WriteLine("Read-only firewall state: " + InstallerFirewall.IsConfigured()); }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    return;
}

var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    checks++;
}

var calls = 0;
var result = await InstallerPrerequisites.RunAsync(() => true, () => true, () => { calls++; return Task.FromResult(0); });
Check(result.ExitCode == 0 && calls == 0, "existing prerequisites skip download, UAC and machine mutations");

var filter = false;
var firewall = false;
result = await InstallerPrerequisites.RunAsync(() => filter, () => firewall,
    () => { calls++; filter = firewall = true; return Task.FromResult(0); });
Check(result.ExitCode == 0 && calls == 1, "first installation runs one bootstrap and checks both results");

firewall = false;
result = await InstallerPrerequisites.RunAsync(() => true, () => firewall,
    () => { calls++; firewall = true; return Task.FromResult(0); });
Check(result.ExitCode == 0 && calls == 2, "existing filter with missing firewall only needs the bootstrap once");

filter = firewall = false;
result = await InstallerPrerequisites.RunAsync(() => filter, () => firewall,
    () => { filter = firewall = true; return Task.FromResult(3010); });
Check(result.ExitCode == 3010 && result.Message.Contains("nicht automatisch neu"), "restart-required is retained without automatic restart");

result = await InstallerPrerequisites.RunAsync(() => false, () => false, () => Task.FromResult(1602));
Check(result.ExitCode == 1602 && result.Message.Contains("abgebrochen") && result.Message.Contains("nicht vollständig"), "native cancellation is an explicit incomplete setup result");

result = await InstallerPrerequisites.RunAsync(() => false, () => false, () => throw new Win32Exception(1223));
Check(result.ExitCode == 1602, "cancelled UAC is represented as cancellation");

result = await InstallerPrerequisites.RunAsync(() => false, () => false, () => throw new OperationCanceledException());
Check(result.ExitCode == 1602, "cancelled MSI is represented as cancellation");

result = await InstallerPrerequisites.RunAsync(() => false, () => false, () => Task.FromResult(1603));
Check(result.ExitCode == 1603 && result.Message.Contains("1603"), "MSI failure retains its exit code in the visible result");

result = await InstallerPrerequisites.RunAsync(() => false, () => false, () => throw new InvalidDataException("Signatur nicht bestätigt"));
Check(result.ExitCode != 0 && result.Message.Contains("Signatur nicht bestätigt"), "download and trust errors do not become success");

result = await InstallerPrerequisites.RunAsync(() => false, () => true, () => Task.FromResult(0));
Check(result.ExitCode != 0, "successful process exit without registered filter is rejected");
result = await InstallerPrerequisites.RunAsync(() => true, () => false, () => Task.FromResult(3010));
Check(result.ExitCode == 1, "restart status cannot hide missing private-LAN rule");

Check(await InstallerPrerequisites.ExecuteAsync(["--setup-prerequisites"]) == 2, "malformed setup CLI does not run a machine workflow");
Check(await InstallerPrerequisites.ExecuteAsync(["--setup-prerequisites", "--setup-result", "relative.txt"]) == 2,
    "relative setup result path is rejected before work");
Check(await InstallerPrerequisites.ExecuteAsync([]) == 2, "empty setup CLI is rejected before work");
Check(await InstallerPrerequisites.ExecuteAsync(["--setup-prerequisites", "--setup-result", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "arbitrary.txt")]) == 2,
    "arbitrary absolute result destination is rejected before work");
Check(await InstallerPrerequisites.ExecuteAsync(["--setup-machine", "a", "b"]) == 2, "machine CLI rejects extra arguments before elevation or mutations");
Check(await InstallerPrerequisites.ExecuteAsync(["--remove-machine-firewall", "unexpected"]) == 2, "removal CLI rejects arguments before mutations");

var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DYNEX One", "DYNEX.exe");
Check(InstallerFirewall.RuleName(path) == InstallerFirewall.RuleName(path.ToUpperInvariant()), "own firewall identity is stable across path casing");
Check(InstallerFirewall.RuleName(path) != InstallerFirewall.RuleName(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DYNEX Two", "DYNEX.exe")),
    "separate installation paths do not share firewall ownership");

var block = new InstallerFirewall.RuleInfo("DYNEX deny", path, true, 1, 2, 0, 17, "39274", "*");
Check(InstallerFirewall.BlocksDiscovery(block, path), "active exact-app private UDP block overrides the allow rule");
Check(InstallerFirewall.BlocksDiscovery(block with { Protocol = 256, Profiles = int.MaxValue, LocalPorts = "", RemotePorts = "" }, path),
    "Windows app block for any protocol and all profiles is detected");
Check(InstallerFirewall.BlocksDiscovery(block with { LocalPorts = "80,39200-39300", RemotePorts = "39274" }, path),
    "discovery port inside a blocked comma-separated range is detected");
Check(InstallerFirewall.BlocksDiscovery(block with { ApplicationName = path.ToUpperInvariant(), LocalPorts = "*" }, path),
    "exact application ownership ignores Windows path casing");
Check(!InstallerFirewall.BlocksDiscovery(block with { ApplicationName = "" }, path), "generic firewall policies are not treated as DYNEX-owned blocks");
Check(!InstallerFirewall.BlocksDiscovery(block with { ApplicationName = path + ".other" }, path), "other applications are not treated as DYNEX-owned blocks");
Check(!InstallerFirewall.BlocksDiscovery(block with { Enabled = false }, path), "disabled block rule does not block setup");
Check(!InstallerFirewall.BlocksDiscovery(block with { Action = 1 }, path), "allow rule does not block setup");
Check(!InstallerFirewall.BlocksDiscovery(block with { Direction = 2 }, path), "outbound rule is not misreported as an inbound Windows-access block");
Check(!InstallerFirewall.BlocksDiscovery(block with { Profiles = 5 }, path), "public and domain blocks are preserved without blocking private discovery setup");
Check(!InstallerFirewall.BlocksDiscovery(block with { Protocol = 6 }, path), "TCP-only block does not block UDP discovery");
Check(!InstallerFirewall.BlocksDiscovery(block with { LocalPorts = "80,443" }, path), "unrelated local ports do not block discovery setup");
Check(!InstallerFirewall.BlocksDiscovery(block with { RemotePorts = "80,443" }, path), "unrelated remote ports do not block discovery setup");
var diagnosed = false;
try { InstallerFirewall.ThrowIfBlocked([block], path); }
catch (IOException error) { diagnosed = error.Message.Contains(block.Name) && error.Message.Contains("Windows-Firewall") && error.Message.Contains("nicht automatisch geändert"); }
Check(diagnosed, "blocking rule produces a concrete diagnosis and preserves existing security policy");
var blockedInstallCalls = 0;
bool BlockingFirewall() { InstallerFirewall.ThrowIfBlocked([block], path); return true; }
result = await InstallerPrerequisites.RunAsync(() => true, BlockingFirewall,
    () => { blockedInstallCalls++; return Task.FromResult(0); });
Check(result.ExitCode != 0 && result.Message.Contains(block.Name) && blockedInstallCalls == 0,
    "existing filter plus conflicting app rule is never reported as configured");
result = await InstallerPrerequisites.RunAsync(() => false, BlockingFirewall,
    () => { blockedInstallCalls++; return Task.FromResult(0); });
Check(result.ExitCode != 0 && result.Message.Contains(block.Name) && blockedInstallCalls == 0,
    "first install diagnoses conflicting firewall rule before downloading or elevating");
var firewallReads = 0;
bool RacingFirewall()
{
    if (++firewallReads > 1) InstallerFirewall.ThrowIfBlocked([block], path);
    return false;
}
result = await InstallerPrerequisites.RunAsync(() => true, RacingFirewall, () => Task.FromResult(1));
Check(result.ExitCode != 0 && result.Message.Contains(block.Name), "policy change during elevated setup preserves concrete blocker diagnosis");

Check(InstallerFirewall.FindRule(new MissingRules(new FileNotFoundException("Schnellcachedaten")), "new-rule") is null,
    "missing firewall Item mapped to FileNotFoundException is normal first-install state");
Check(InstallerFirewall.FindRule(new MissingRules(new System.Runtime.InteropServices.COMException("Missing", unchecked((int)0x80070002))), "new-rule") is null,
    "missing firewall Item mapped to COMException remains supported");
var denied = false;
try { InstallerFirewall.FindRule(new MissingRules(new UnauthorizedAccessException("denied")), "new-rule"); }
catch (UnauthorizedAccessException) { denied = true; }
Check(denied, "firewall lookup access denied cannot masquerade as a missing rule");
var unrelated = false;
try { InstallerFirewall.FindRule(new MissingRules(new System.Runtime.InteropServices.COMException("COM failure", unchecked((int)0x80004005))), "new-rule"); }
catch (System.Runtime.InteropServices.COMException) { unrelated = true; }
Check(unrelated, "unrelated COM lookup error cannot masquerade as a missing rule");

Console.WriteLine($"PASS: {checks} setup checks; no download, elevation, driver installation or firewall mutation performed.");

public sealed class MissingRules(Exception error)
{
    public object Item(string name) => throw error;
}
