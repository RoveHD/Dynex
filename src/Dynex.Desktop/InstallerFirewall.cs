using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Dynex.Desktop;

internal static class InstallerFirewall
{
    internal sealed record RuleInfo(string Name, string ApplicationName, bool Enabled, int Direction,
        int Profiles, int Action, int Protocol, string LocalPorts, string RemotePorts);
    internal sealed record PrivatePolicy(bool Enabled, bool BlockAllInboundTraffic, int DefaultOutboundAction);

    internal static string ApplicationPath => Path.Combine(AppContext.BaseDirectory, "DYNEX.exe");
    internal static string RuleName(string applicationPath, int direction = 1) => (direction == 2 ? "DYNEX AutoLAN Outbound " : "DYNEX AutoLAN ") +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(applicationPath).ToUpperInvariant())))[..16];

    internal static bool IsConfigured()
    {
        dynamic? policy = null;
        dynamic? rules = null;
        try
        {
            policy = CreatePolicy();
            ThrowIfPolicyBlocks(ReadPrivatePolicy(policy));
            rules = policy.Rules;
            ThrowIfBlocked(ReadApplicationBlocks(rules), ApplicationPath);
            return RulesConfigured(rules, ApplicationPath);
        }
        catch (COMException error)
        { throw new IOException("Der Windows-Firewallstatus konnte nicht gelesen werden: " + error.Message, error); }
        finally { Release(rules); Release(policy); }
    }

    internal static void EnsureConfigured(string? applicationPath = null)
    {
        applicationPath ??= ApplicationPath;
        dynamic? policy = null;
        dynamic? rules = null;
        try
        {
            policy = CreatePolicy();
            ThrowIfPolicyBlocks(ReadPrivatePolicy(policy));
            rules = policy.Rules;
            // A Windows "deny access" rule has precedence over our allow rule.
            // Diagnose it before any mutation; do not relax user or company policy.
            ThrowIfBlocked(ReadApplicationBlocks(rules, applicationPath), applicationPath);
            EnsureRules(rules, applicationPath);
        }
        finally { Release(rules); Release(policy); }
    }

    internal static bool RulesConfigured(object rules, string applicationPath)
    {
        foreach (var direction in new[] { 1, 2 })
        {
            object? rule = null;
            try
            {
                rule = FindRule(rules, RuleName(applicationPath, direction));
                if (rule is null || !Matches(rule, applicationPath, direction)) return false;
            }
            finally { Release(rule); }
        }
        return true;
    }

    internal static bool HasRules()
    {
        dynamic? policy = null, rules = null;
        try
        {
            policy = CreatePolicy();
            rules = policy.Rules;
            return RulesPresent(rules, ApplicationPath);
        }
        finally { Release(rules); Release(policy); }
    }

    internal static bool RulesPresent(object rules, string applicationPath)
    {
        foreach (var direction in new[] { 1, 2 })
        {
            object? rule = null;
            try
            {
                rule = FindRule(rules, RuleName(applicationPath, direction));
                if (rule is not null) return true;
            }
            finally { Release(rule); }
        }
        return false;
    }

    internal static void EnsureRules(object rules, string applicationPath, Func<object>? createRule = null)
    {
        ValidateOwnedRules(rules, applicationPath);
        foreach (var direction in new[] { 1, 2 })
        {
            object? existing = null, rule = null, installed = null;
            try
            {
                var name = RuleName(applicationPath, direction);
                existing = FindRule(rules, name);
                if (existing is not null)
                {
                    if (!Matches(existing, applicationPath, direction))
                        throw new IOException("Die vorhandene DYNEX-Firewallregel wurde verändert: " + name);
                    continue;
                }
                rule = createRule?.Invoke() ?? Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
                ConfigureDiscoveryRule(rule, applicationPath, direction);
                ((dynamic)rules).Add(rule);
                installed = FindRule(rules, name);
                if (installed is null || !Matches(installed, applicationPath, direction))
                    throw new IOException("Die DYNEX-Heimnetzregel konnte nicht bestätigt werden: " + name);
            }
            finally { Release(installed); Release(rule); Release(existing); }
        }
    }

    private static void ValidateOwnedRules(object rules, string applicationPath)
    {
        foreach (var direction in new[] { 1, 2 })
        {
            object? rule = null;
            try
            {
                var name = RuleName(applicationPath, direction);
                rule = FindRule(rules, name);
                if (rule is not null && !Matches(rule, applicationPath, direction))
                    throw new IOException("Die vorhandene DYNEX-Firewallregel wurde verändert. Bitte diese Regel in der Windows-Firewall prüfen: " + name);
            }
            finally { Release(rule); }
        }
    }

    private static IEnumerable<RuleInfo> ReadApplicationBlocks(dynamic rules, string? applicationPath = null)
    {
        applicationPath ??= ApplicationPath;
        foreach (dynamic candidate in rules)
        {
            try
            {
                var direction = (int)candidate.Direction;
                if (!(bool)candidate.Enabled || direction is not (1 or 2) || (int)candidate.Action != 0 ||
                    ((int)candidate.Profiles & 2) == 0) continue;
                var application = (string)candidate.ApplicationName;
                if (!SameApplication(application, applicationPath)) continue;
                var protocol = (int)candidate.Protocol;
                if (protocol is not (17 or 256)) continue;
                // Windows does not expose port lists for protocol "Any".
                yield return new RuleInfo((string)candidate.Name, application, true, direction, (int)candidate.Profiles,
                    0, protocol, protocol == 256 ? "*" : (string)candidate.LocalPorts,
                    protocol == 256 ? "*" : (string)candidate.RemotePorts);
            }
            finally { Release(candidate); }
        }
    }

    internal static void ThrowIfBlocked(IEnumerable<RuleInfo> rules, string applicationPath)
    {
        var conflicts = rules.Where(rule => BlocksDiscovery(rule, applicationPath)).Select(rule =>
        {
            var name = rule.Name.Replace('\r', ' ').Replace('\n', ' ');
            return (name.Length > 100 ? name[..100] : name) + (rule.Direction == 2 ? " (ausgehend)" : " (eingehend)");
        }).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray();
        if (conflicts.Length == 0) return;
        throw new IOException("Eine Windows-Firewall-Regel kann DYNEX im privaten Heimnetz blockieren (Regel: " +
            string.Join(", ", conflicts) + "). Öffne Windows-Sicherheit → Firewall & Netzwerkschutz → Erweiterte Einstellungen → " +
            "Eingehende/Ausgehende Regeln und prüfe diese DYNEX-Blockregel für private Netzwerke. Vorhandene Sperren werden nicht automatisch geändert.");
    }

    internal static bool BlocksDiscovery(RuleInfo rule, string applicationPath) =>
        rule.Enabled && rule.Direction is 1 or 2 && (rule.Profiles & 2) != 0 && rule.Action == 0 &&
        SameApplication(rule.ApplicationName, applicationPath) &&
        (rule.Protocol == 256 || rule.Protocol == 17 && IncludesDiscoveryPort(rule.LocalPorts) && IncludesDiscoveryPort(rule.RemotePorts));

    internal static PrivatePolicy ReadPrivatePolicy(dynamic policy)
    {
        try
        {
            return new PrivatePolicy((bool)policy.FirewallEnabled[2],
                (bool)policy.BlockAllInboundTraffic[2], (int)policy.DefaultOutboundAction[2]);
        }
        catch (COMException error)
        { throw new IOException("Die Windows-Firewallrichtlinie für das private Netzwerk konnte nicht gelesen werden: " + error.Message, error); }
    }

    internal static void ThrowIfPolicyBlocks(PrivatePolicy policy)
    {
        if (!policy.Enabled) return;
        var problems = new List<string>();
        if (policy.BlockAllInboundTraffic)
            problems.Add("Im Privatprofil ist ‚Alle eingehenden Verbindungen blockieren‘ aktiviert. Windows ignoriert damit auch die DYNEX-Heimnetzfreigabe.");
        // DefaultOutboundAction=Block is supported by our app-scoped outgoing
        // allow rule. Explicit block rules still win and are checked separately.
        if (problems.Count > 0)
            throw new IOException(string.Join(" ", problems) + " Prüfe Windows-Sicherheit → Firewall & Netzwerkschutz → Erweiterte Einstellungen. Globale Firewallrichtlinien werden nicht automatisch geändert.");
    }

    private static bool SameApplication(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(left)), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return false; }
    }

    private static bool IncludesDiscoveryPort(string? ports)
    {
        if (string.IsNullOrWhiteSpace(ports) || ports.Trim() == "*") return true;
        foreach (var item in ports.Split(','))
        {
            var range = item.Trim().Split('-', 2);
            if (int.TryParse(range[0], out var first) &&
                (range.Length == 1 ? first == 39274 : int.TryParse(range[1], out var last) && first <= 39274 && last >= 39274)) return true;
        }
        return false;
    }

    internal static void RemoveConfigured(string? applicationPath = null)
    {
        applicationPath ??= ApplicationPath;
        dynamic? policy = null;
        dynamic? rules = null;
        try
        {
            policy = CreatePolicy();
            rules = policy.Rules;
            RemoveRules(rules, applicationPath);
        }
        finally { Release(rules); Release(policy); }
    }

    internal static void RemoveRules(object rules, string applicationPath)
    {
        ValidateOwnedRules(rules, applicationPath);
        foreach (var direction in new[] { 1, 2 })
        {
            var name = RuleName(applicationPath, direction);
            object? rule = null;
            dynamic? remaining = null;
            try
            {
                rule = FindRule(rules, name);
                if (rule is null) continue;
                ((dynamic)rules).Remove(name);
                remaining = FindRule(rules, name);
                if (remaining is not null) throw new IOException("Die Entfernung der DYNEX-Firewallregel wurde nicht bestätigt: " + name);
            }
            finally { Release(remaining); Release(rule); }
        }
    }

    internal static void ConfigureDiscoveryRule(dynamic rule, string applicationPath, int direction = 1)
    {
        if (direction is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(direction));
        rule.Name = RuleName(applicationPath, direction);
        rule.Description = "DYNEX-PCs im selben privaten IPv4-Heimnetz automatisch verbinden.";
        rule.ApplicationName = applicationPath;
        rule.Protocol = 17; // UDP
        rule.LocalPorts = "39274";
        rule.RemotePorts = direction == 2 ? "39274" : "*";
        rule.LocalAddresses = "*";
        rule.RemoteAddresses = direction == 2 ? "LocalSubnet,239.255.39.74" : "LocalSubnet";
        rule.InterfaceTypes = "All";
        rule.Direction = direction;
        rule.Profiles = 2; // Private only
        rule.Action = 1; // allow
        rule.EdgeTraversal = false;
        rule.Enabled = true;
    }

    internal static bool Matches(dynamic rule, string applicationPath, int direction = 1) =>
        direction is 1 or 2 &&
        string.Equals(Path.GetFullPath((string)rule.ApplicationName), applicationPath, StringComparison.OrdinalIgnoreCase) &&
        (int)rule.Protocol == 17 && (string)rule.LocalPorts == "39274" &&
        (direction == 2 ? (string?)rule.RemotePorts == "39274" : IsUnrestricted((string?)rule.RemotePorts)) && IsUnrestricted((string?)rule.LocalAddresses) &&
        (string.IsNullOrEmpty((string?)rule.InterfaceTypes) || string.Equals((string?)rule.InterfaceTypes, "All", StringComparison.OrdinalIgnoreCase)) &&
        HasNoInterfaces((object?)rule.Interfaces) && string.IsNullOrEmpty((string?)rule.ServiceName) &&
        RemoteAddressesMatch((string)rule.RemoteAddresses, direction) &&
        (int)rule.Direction == direction && (int)rule.Profiles == 2 && (int)rule.Action == 1 &&
        (bool)rule.Enabled && !(bool)rule.EdgeTraversal;

    private static bool IsUnrestricted(string? value) => string.IsNullOrEmpty(value) || value == "*";
    private static bool HasNoInterfaces(object? value) => value is null or DBNull || value is Array { Length: 0 };

    private static bool RemoteAddressesMatch(string? addresses, int direction)
    {
        if (direction == 1) return string.Equals(addresses, "LocalSubnet", StringComparison.OrdinalIgnoreCase);
        if (addresses is null) return false;
        var values = addresses.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return values.Length == 2 && values.Any(value => string.Equals(value, "LocalSubnet", StringComparison.OrdinalIgnoreCase)) &&
            values.Any(value => value is "239.255.39.74" or "239.255.39.74/255.255.255.255" or "239.255.39.74/32");
    }

    internal static object? FindRule(object rules, string name)
    {
        try { return ((dynamic)rules).Item(name); }
        // Dynamic COM maps this HRESULT to FileNotFoundException on .NET 9.
        // Missing is normal on first install; all other errors remain failures.
        catch (Exception error) when (error.HResult == unchecked((int)0x80070002) &&
            error is COMException or FileNotFoundException) { return null; }
    }

    private static dynamic CreatePolicy() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
}
