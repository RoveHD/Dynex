using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Dynex.Desktop;

internal static class InstallerFirewall
{
    internal sealed record RuleInfo(string Name, string ApplicationName, bool Enabled, int Direction,
        int Profiles, int Action, int Protocol, string LocalPorts, string RemotePorts);

    internal static string ApplicationPath => Path.Combine(AppContext.BaseDirectory, "DYNEX.exe");
    internal static string RuleName(string applicationPath) => "DYNEX AutoLAN " +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(applicationPath).ToUpperInvariant())))[..16];

    internal static bool IsConfigured()
    {
        dynamic? policy = null;
        dynamic? rules = null;
        dynamic? rule = null;
        try
        {
            policy = CreatePolicy();
            rules = policy.Rules;
            ThrowIfBlocked(ReadApplicationBlocks(rules), ApplicationPath);
            rule = rules.Item(RuleName(ApplicationPath));
            return Matches(rule);
        }
        catch (COMException) { return false; }
        finally { Release(rule); Release(rules); Release(policy); }
    }

    internal static void EnsureConfigured()
    {
        dynamic? policy = null;
        dynamic? rules = null;
        dynamic? existing = null;
        dynamic? rule = null;
        dynamic? installed = null;
        try
        {
            policy = CreatePolicy();
            rules = policy.Rules;
            // A Windows "deny access" rule has precedence over our allow rule.
            // Diagnose it before any mutation; do not relax user or company policy.
            ThrowIfBlocked(ReadApplicationBlocks(rules), ApplicationPath);
            var name = RuleName(ApplicationPath);
            try { existing = rules.Item(name); }
            catch (COMException error) when (error.HResult == unchecked((int)0x80070002)) { }
            if (existing is not null && Matches(existing)) return;
            if (existing is not null) rules.Remove(name);
            rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
            rule.Name = name;
            rule.Description = "DYNEX-PCs im selben privaten IPv4-Heimnetz automatisch verbinden.";
            rule.ApplicationName = ApplicationPath;
            rule.Protocol = 17; // UDP
            rule.LocalPorts = "39274";
            rule.RemoteAddresses = "LocalSubnet";
            rule.Direction = 1; // inbound
            rule.Profiles = 2; // Private only
            rule.Action = 1; // allow
            rule.EdgeTraversal = false;
            rule.Enabled = true;
            rules.Add(rule);
            installed = rules.Item(name);
            if (!Matches(installed)) throw new IOException("Die DYNEX-Heimnetzregel konnte nicht bestätigt werden.");
        }
        finally { Release(installed); Release(rule); Release(existing); Release(rules); Release(policy); }
    }

    private static IEnumerable<RuleInfo> ReadApplicationBlocks(dynamic rules)
    {
        foreach (dynamic candidate in rules)
        {
            try
            {
                if (!(bool)candidate.Enabled || (int)candidate.Direction != 1 || (int)candidate.Action != 0 ||
                    ((int)candidate.Profiles & 2) == 0) continue;
                var application = (string)candidate.ApplicationName;
                if (!SameApplication(application, ApplicationPath)) continue;
                var protocol = (int)candidate.Protocol;
                if (protocol is not (17 or 256)) continue;
                // Windows does not expose port lists for protocol "Any".
                yield return new RuleInfo((string)candidate.Name, application, true, 1, (int)candidate.Profiles,
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
            return name.Length > 100 ? name[..100] : name;
        }).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray();
        if (conflicts.Length == 0) return;
        throw new IOException("Eine Windows-Firewall-Regel kann DYNEX im privaten Heimnetz blockieren (Regel: " +
            string.Join(", ", conflicts) + "). Öffne Windows-Sicherheit → Firewall & Netzwerkschutz → Erweiterte Einstellungen → " +
            "Eingehende Regeln und prüfe diese DYNEX-Blockregel für private Netzwerke. Vorhandene Sperren werden nicht automatisch geändert.");
    }

    internal static bool BlocksDiscovery(RuleInfo rule, string applicationPath) =>
        rule.Enabled && rule.Direction == 1 && (rule.Profiles & 2) != 0 && rule.Action == 0 &&
        SameApplication(rule.ApplicationName, applicationPath) &&
        (rule.Protocol == 256 || rule.Protocol == 17 && IncludesDiscoveryPort(rule.LocalPorts) && IncludesDiscoveryPort(rule.RemotePorts));

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

    internal static void RemoveConfigured()
    {
        dynamic? policy = null;
        dynamic? rules = null;
        dynamic? rule = null;
        try
        {
            policy = CreatePolicy();
            rules = policy.Rules;
            try { rule = rules.Item(RuleName(ApplicationPath)); }
            catch (COMException error) when (error.HResult == unchecked((int)0x80070002)) { return; }
            // Preserve rules somebody else has modified, and never touch any
            // other application's rules or the shared system-wide filter.
            if (Matches(rule)) rules.Remove(RuleName(ApplicationPath));
        }
        finally { Release(rule); Release(rules); Release(policy); }
    }

    private static bool Matches(dynamic rule) =>
        string.Equals(Path.GetFullPath((string)rule.ApplicationName), ApplicationPath, StringComparison.OrdinalIgnoreCase) &&
        (int)rule.Protocol == 17 && (string)rule.LocalPorts == "39274" &&
        string.Equals((string)rule.RemoteAddresses, "LocalSubnet", StringComparison.OrdinalIgnoreCase) &&
        (int)rule.Direction == 1 && (int)rule.Profiles == 2 && (int)rule.Action == 1 &&
        (bool)rule.Enabled && !(bool)rule.EdgeTraversal;

    private static dynamic CreatePolicy() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
}
