using System.ComponentModel;
using Dynex.Desktop;

if (args is ["--probe-boot"])
{
    var identity = WindowsInstallerSystem.ReadBootIdentity();
    Console.WriteLine("Read-only Windows boot identity: " + (identity ?? "unavailable"));
    Environment.ExitCode = identity is null ? 1 : 0;
    return;
}

if (args is ["--probe-firewall"])
{
    object? temporaryRule = null;
    try
    {
        Console.WriteLine("Read-only firewall state: " + InstallerFirewall.IsConfigured());
        // Validate COM defaults and setters without adding a rule to Windows.
        foreach (var direction in new[] { 1, 2 })
        {
            temporaryRule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!);
            InstallerFirewall.ConfigureDiscoveryRule(temporaryRule!, InstallerFirewall.ApplicationPath, direction);
            if (!InstallerFirewall.Matches(temporaryRule!, InstallerFirewall.ApplicationPath, direction))
                throw new IOException("Unregistrierte COM-Firewallregel entspricht nicht der erwarteten Heimnetzfreigabe, Richtung " + direction);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(temporaryRule!);
            temporaryRule = null;
        }
        Console.WriteLine("Unregistered inbound/outbound COM rule configuration: verified; no firewall rule added.");
    }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    finally
    {
        if (temporaryRule is not null && System.Runtime.InteropServices.Marshal.IsComObject(temporaryRule))
            System.Runtime.InteropServices.Marshal.ReleaseComObject(temporaryRule);
    }
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
StartupChecks.Run(Check);
var result = await InstallerPrerequisites.RunAsync(() => true, () => true, () => { calls++; return Task.FromResult(0); }, () => true);
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
Check(await InstallerPrerequisites.ExecuteAsync(["--setup-machine-firewall", "unexpected"]) == 2, "independent firewall CLI rejects arguments before mutations");

var firewallFallbackCalls = 0;
var fullSetupCalls = 0;
try
{
    await InstallerPrerequisites.InstallMachineAsync(false,
        () => throw new HttpRequestException("isolated filter download unavailable"),
        _ => { fullSetupCalls++; return Task.FromResult(0); },
        () => { firewallFallbackCalls++; return Task.FromResult(0); });
    throw new Exception("download failure became success");
}
catch (IOException error)
{
    Check(error.Message.Contains("Firewallfreigabe") && error.Message.Contains("wurde eingerichtet") &&
        error.Message.Contains("isolated filter download unavailable") && firewallFallbackCalls == 1 && fullSetupCalls == 0,
        "failed filter download still configures LAN and reports the incomplete filter honestly");
}
var cancelledFallback = false;
try
{
    await InstallerPrerequisites.InstallMachineAsync(false, () => throw new HttpRequestException("download unavailable"),
        _ => Task.FromResult(0), () => Task.FromResult(1602));
}
catch (OperationCanceledException) { cancelledFallback = true; }
Check(cancelledFallback, "cancelled independent firewall setup never claims working LAN");

var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DYNEX One", "DYNEX.exe");
Check(InstallerFirewall.RuleName(path) == InstallerFirewall.RuleName(path.ToUpperInvariant()), "own firewall identity is stable across path casing");
Check(InstallerFirewall.RuleName(path) != InstallerFirewall.RuleName(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DYNEX Two", "DYNEX.exe")),
    "separate installation paths do not share firewall ownership");

var block = new InstallerFirewall.RuleInfo("DYNEX deny", path, true, 1, 2, 0, 17, "39274", "*");
var configuredRule = new FakeFirewallRule();
InstallerFirewall.ConfigureDiscoveryRule(configuredRule, path);
Check(InstallerFirewall.Matches(configuredRule, path) && configuredRule.Name == InstallerFirewall.RuleName(path),
    "actual setup configuration creates an enabled exact-app inbound private UDP39274 LocalSubnet rule without edge traversal");
configuredRule.Profiles = 7;
Check(!InstallerFirewall.Matches(configuredRule, path), "public/domain expansion cannot pass private-LAN verification");
configuredRule.Profiles = 2; configuredRule.ApplicationName = path + ".other";
Check(!InstallerFirewall.Matches(configuredRule, path), "another executable cannot pass firewall ownership verification");
InstallerFirewall.ConfigureDiscoveryRule(configuredRule, path);
configuredRule.RemotePorts = "443";
Check(!InstallerFirewall.Matches(configuredRule, path), "modified remote ports cannot confirm a nonfunctional discovery allow rule");
configuredRule.RemotePorts = "*"; configuredRule.LocalAddresses = "127.0.0.1";
Check(!InstallerFirewall.Matches(configuredRule, path), "loopback-only local address cannot confirm LAN firewall setup");
configuredRule.LocalAddresses = "*"; configuredRule.InterfaceTypes = "Wireless";
Check(!InstallerFirewall.Matches(configuredRule, path), "wireless-only restriction cannot confirm discovery for Ethernet PCs");
configuredRule.InterfaceTypes = "All"; configuredRule.Interfaces = new[] { "Old adapter" };
Check(!InstallerFirewall.Matches(configuredRule, path), "restriction to an old adapter cannot confirm firewall setup");
configuredRule.Interfaces = null; configuredRule.ServiceName = "*";
Check(!InstallerFirewall.Matches(configuredRule, path), "Windows-service-only restriction cannot confirm the desktop application's rule");
configuredRule.ServiceName = ""; configuredRule.Interfaces = Array.Empty<string>();
Check(InstallerFirewall.Matches(configuredRule, path), "unrestricted empty COM interface array remains valid");
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
Check(InstallerFirewall.BlocksDiscovery(block with { Direction = 2 }, path), "explicit outbound discovery block is detected for the exact app");
Check(!InstallerFirewall.BlocksDiscovery(block with { Direction = 2, LocalPorts = "80" }, path) &&
    !InstallerFirewall.BlocksDiscovery(block with { Direction = 2, RemotePorts = "443" }, path),
    "unrelated outgoing source or destination ports do not block UDP39274 discovery");
Check(!InstallerFirewall.BlocksDiscovery(block with { Direction = 3 }, path), "invalid rule direction cannot become a discovery blocker");
Check(!InstallerFirewall.BlocksDiscovery(block with { Profiles = 5 }, path), "public and domain blocks are preserved without blocking private discovery setup");
Check(!InstallerFirewall.BlocksDiscovery(block with { Protocol = 6 }, path), "TCP-only block does not block UDP discovery");
Check(!InstallerFirewall.BlocksDiscovery(block with { LocalPorts = "80,443" }, path), "unrelated local ports do not block discovery setup");
Check(!InstallerFirewall.BlocksDiscovery(block with { RemotePorts = "80,443" }, path), "unrelated remote ports do not block discovery setup");
var diagnosed = false;
try { InstallerFirewall.ThrowIfBlocked([block], path); }
catch (IOException error) { diagnosed = error.Message.Contains(block.Name) && error.Message.Contains("Windows-Firewall") && error.Message.Contains("nicht automatisch geändert"); }
Check(diagnosed, "blocking rule produces a concrete diagnosis and preserves existing security policy");
diagnosed = false;
try { InstallerFirewall.ThrowIfBlocked([block with { Direction = 2 }], path); }
catch (IOException error) { diagnosed = error.Message.Contains("ausgehend") && error.Message.Contains(block.Name); }
Check(diagnosed, "outgoing app block diagnosis names the rule and correct direction");

var privatePolicy = InstallerFirewall.ReadPrivatePolicy(new FakePrivatePolicy());
Check(privatePolicy is { Enabled: true, BlockAllInboundTraffic: false, DefaultOutboundAction: 1 },
    "private firewall policy reads indexed profile 2 without mixing public or domain settings");
InstallerFirewall.ThrowIfPolicyBlocks(privatePolicy);
diagnosed = false;
try { InstallerFirewall.ThrowIfPolicyBlocks(privatePolicy with { BlockAllInboundTraffic = true }); }
catch (IOException error) { diagnosed = error.Message.Contains("Alle eingehenden") && error.Message.Contains("ignoriert") && error.Message.Contains("nicht automatisch geändert"); }
Check(diagnosed, "block-all-inbound policy explains that Windows ignores the DYNEX allow rule");
InstallerFirewall.ThrowIfPolicyBlocks(privatePolicy with { DefaultOutboundAction = 0 });
Check(true, "default outbound block permits setup because DYNEX creates its own narrow outgoing exception");
InstallerFirewall.ThrowIfPolicyBlocks(privatePolicy with { Enabled = false, BlockAllInboundTraffic = true, DefaultOutboundAction = 0 });
Check(true, "disabled firewall does not falsely apply dormant block-all or outbound-default policy");
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

await UninstallChecks.RunAsync(Check);
FirewallRulesChecks.Run(Check);
Console.WriteLine($"PASS: {checks} setup checks; no download, elevation, driver installation or firewall mutation performed.");

public sealed class MissingRules(Exception error)
{
    public object Item(string name) => throw error;
}

public sealed class FakeFirewallRule
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ApplicationName { get; set; } = "";
    public string LocalPorts { get; set; } = "";
    public string RemotePorts { get; set; } = "*";
    public string LocalAddresses { get; set; } = "*";
    public string RemoteAddresses { get; set; } = "";
    public string InterfaceTypes { get; set; } = "All";
    public object? Interfaces { get; set; }
    public string ServiceName { get; set; } = "";
    public int Protocol { get; set; }
    public int Direction { get; set; }
    public int Profiles { get; set; }
    public int Action { get; set; }
    public bool Enabled { get; set; }
    public bool EdgeTraversal { get; set; }
}

public sealed class FakePrivatePolicy
{
    public bool[] FirewallEnabled { get; } = [false, false, true];
    public bool[] BlockAllInboundTraffic { get; } = [true, true, false];
    public int[] DefaultOutboundAction { get; } = [0, 0, 1];
}
