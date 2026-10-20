using System.Text.Json;

namespace Dynex.Shaper;

public static class ControlProtocol
{
    public const int MaxLineLength = 16384;
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(7);

    public static double? ParseRate(string line)
    {
        using var json = JsonDocument.Parse(line);
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("mbps", out var rate))
            throw new FormatException("Erwartet: {\"mbps\": Zahl oder null}.");
        if (rate.ValueKind == JsonValueKind.Null) return null;
        var value = rate.GetDouble();
        PacketPacer.ValidateRate(value);
        return value;
    }

    public sealed record ProtectedProcess(int ProcessId, long? StartedAtUtcTicks);
    public sealed record Command(double? Mbps, ProtectedProcess[] ProtectedProcesses, bool QosEnabled = false, double? UploadMbps = null,
        bool ObserveGameTraffic = false, ApplicationLimit[]? ApplicationRules = null);


    /// <summary>Optional limit for large non-game uploads. Absent or null means uploads stay unlimited.</summary>
    public static double? ParseUploadRate(JsonElement root)
    {
        if (!root.TryGetProperty("uploadMbps", out var rate) || rate.ValueKind == JsonValueKind.Null) return null;
        var value = rate.GetDouble();
        PacketPacer.ValidateRate(value);
        return value;
    }

    public static Command ParseCommand(string line)
    {
        // Validate the bounded legacy envelope, but never activate its caps.
        _ = ParseRate(line);
        using var json = JsonDocument.Parse(line);
        var qos = json.RootElement.TryGetProperty("qosEnabled", out var qosValue) && qosValue.GetBoolean();
        _ = ParseUploadRate(json.RootElement);
        var observe = json.RootElement.TryGetProperty("observeGameTraffic", out var observeValue) && observeValue.GetBoolean();
        _ = ParseApplicationRules(json.RootElement);
        if (!json.RootElement.TryGetProperty("protectedProcesses", out var processes))
            return new(null, [], qos, null, observe, []);
        if (processes.ValueKind != JsonValueKind.Array || processes.GetArrayLength() > 128)
            throw new FormatException("Ungültige Liste geschützter Programme.");
        var result = new List<ProtectedProcess>();
        foreach (var item in processes.EnumerateArray())
        {
            var pid = item.GetProperty("processId").GetInt32();
            long? ticks = item.TryGetProperty("startedAtUtcTicks", out var start) && start.ValueKind != JsonValueKind.Null ? start.GetInt64() : null;
            if (pid <= 0 || ticks is <= 0 || ticks > DateTime.MaxValue.Ticks) throw new FormatException("Ungültige Prozessidentität.");
            result.Add(new(pid, ticks));
        }
        return new(null, result.Distinct().OrderBy(p => p.ProcessId).ToArray(), qos, null, observe, []);
    }

    private static ApplicationLimit[] ParseApplicationRules(JsonElement root)
    {
        if (!root.TryGetProperty("applicationRules", out var rules)) return [];
        if (rules.ValueKind != JsonValueKind.Array || rules.GetArrayLength() > 32) throw new FormatException("Ungültige Programmregeln.");
        var result = new List<ApplicationLimit>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in rules.EnumerateArray())
        {
            var name = item.GetProperty("processName").GetString()?.Trim().ToLowerInvariant() ?? "";
            if (name.EndsWith(".exe", StringComparison.Ordinal)) name = name[..^4];
            if (name.Length is < 1 or > 64 || name is "." or ".." || name.Any(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c)) || !names.Add(name))
                throw new FormatException("Ungültiger oder doppelter Programmname.");
            double? Read(string key)
            {
                if (!item.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
                var rate = value.GetDouble(); PacketPacer.ValidateRate(rate); return rate;
            }
            var down = Read("downloadMbps"); var up = Read("uploadMbps");
            if (down is null && up is null) throw new FormatException("Programmregel ohne Limit.");
            result.Add(new(name, down, up));
        }
        return result.ToArray();
    }

    public static async Task<string?> ReadLineAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var chars = new char[MaxLineLength];
        var one = new char[1];
        var count = 0;
        while (await reader.ReadAsync(one, cancellationToken).ConfigureAwait(false) != 0)
        {
            if (one[0] == '\n') return new string(chars, 0, count).TrimEnd('\r');
            if (count == chars.Length) throw new FormatException("IPC-Zeile überschreitet das Größenlimit.");
            chars[count++] = one[0];
        }
        if (count != 0) throw new EndOfStreamException("Unvollständiger IPC-Befehl.");
        return null;
    }
}
