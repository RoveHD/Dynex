using Dynex.Desktop;

internal static class FirewallRulesChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var application = Path.Combine(Path.GetTempPath(), "DYNEX-rule-fixture", "DYNEX.exe");
        var rules = new FakeFirewallRules();
        var incoming = new FakeFirewallRule();
        InstallerFirewall.ConfigureDiscoveryRule(incoming, application);
        rules.Add(incoming);
        check(!InstallerFirewall.RulesConfigured(rules, application) && InstallerFirewall.RulesPresent(rules, application),
            "legacy inbound-only installation requests outgoing upgrade but remains discoverable for cleanup");
        InstallerFirewall.EnsureRules(rules, application, () => new FakeFirewallRule());
        var outgoing = (FakeFirewallRule)rules.Item(InstallerFirewall.RuleName(application, 2));
        check(InstallerFirewall.RulesConfigured(rules, application) && rules.Adds == 2 &&
            ReferenceEquals(incoming, rules.Item(InstallerFirewall.RuleName(application))),
            "upgrade retains the exact old inbound rule and creates only its separate outgoing rule");
        check(outgoing.Name != incoming.Name && outgoing.Direction == 2 && outgoing.Protocol == 17 &&
            outgoing.LocalPorts == "39274" && outgoing.RemotePorts == "39274" && outgoing.Profiles == 2 &&
            outgoing.RemoteAddresses == "LocalSubnet,239.255.39.74" && outgoing.ApplicationName == application,
            "outgoing exception is scoped to this executable, private UDP39274, local subnet and the discovery multicast group");
        check(InstallerFirewall.Matches(outgoing, application, 2) && !InstallerFirewall.Matches(outgoing, application),
            "outgoing rule cannot masquerade as the incoming exception");
        outgoing.RemoteAddresses = "239.255.39.74/255.255.255.255,LocalSubnet";
        check(InstallerFirewall.Matches(outgoing, application, 2), "canonical COM multicast host mask and address ordering preserve exact scope");
        outgoing.RemoteAddresses = "LocalSubnet,239.255.39.0/24";
        check(!InstallerFirewall.Matches(outgoing, application, 2), "expanded multicast range is not accepted as an owned outgoing rule");
        outgoing.RemoteAddresses = "LocalSubnet,239.255.39.74";
        InstallerFirewall.EnsureRules(rules, application, () => new FakeFirewallRule());
        check(rules.Adds == 2, "repeated setup is idempotent for both owned directions");
        outgoing.RemotePorts = "*";
        var changedRejected = false;
        try { InstallerFirewall.EnsureRules(rules, application, () => new FakeFirewallRule()); }
        catch (IOException) { changedRejected = true; }
        check(changedRejected && rules.Adds == 2 && !InstallerFirewall.RulesConfigured(rules, application),
            "modified outgoing destination ports fail verification without replacing user-edited rules");
        var removalRejected = false;
        try { InstallerFirewall.RemoveRules(rules, application); }
        catch (IOException) { removalRejected = true; }
        check(removalRejected && rules.Removes == 0, "uninstall validates both directions before removing either modified installation rule");
        outgoing.RemotePorts = "39274";
        var unrelated = new FakeFirewallRule { Name = "Another application", ApplicationName = application + ".other" };
        rules.Add(unrelated);
        InstallerFirewall.RemoveRules(rules, application);
        check(rules.Removes == 2 && rules.Count == 1 && ReferenceEquals(unrelated, rules.Item(unrelated.Name)),
            "uninstall removes both exact owned rules while preserving unrelated rules");
        InstallerFirewall.RemoveRules(rules, application);
        check(rules.Removes == 2 && !InstallerFirewall.RulesPresent(rules, application), "repeated two-direction firewall removal is idempotent and unrelated rules do not trigger cleanup UAC");

        rules = new FakeFirewallRules();
        rules.Add(incoming);
        InstallerFirewall.RemoveRules(rules, application);
        check(rules.Count == 0 && rules.Removes == 1, "legacy inbound-only uninstall also succeeds when outgoing rule was never created");
        rules = new FakeFirewallRules();
        InstallerFirewall.EnsureRules(rules, application, () => new FakeFirewallRule());
        check(rules.Adds == 2 && InstallerFirewall.RulesConfigured(rules, application),
            "fresh installation creates and verifies both app-scoped rules");
    }
}

public sealed class FakeFirewallRules
{
    private readonly Dictionary<string, object> rules = new(StringComparer.OrdinalIgnoreCase);
    public int Adds { get; private set; }
    public int Removes { get; private set; }
    public int Count => rules.Count;
    public object Item(string name) => rules.TryGetValue(name, out var rule) ? rule : throw new FileNotFoundException("Missing isolated rule");
    public void Add(object rule)
    {
        rules.Add(((dynamic)rule).Name, rule);
        Adds++;
    }
    public void Remove(string name)
    {
        if (rules.Remove(name)) Removes++;
    }
}
