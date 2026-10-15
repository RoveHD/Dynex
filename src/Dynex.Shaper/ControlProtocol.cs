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
    public sealed record Command(double? Mbps, ProtectedProcess[] ProtectedProcesses, bool QosEnabled = false, double? UploadMbps = null);

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
        var rate = ParseRate(line);
        using var json = JsonDocument.Parse(line);
        var qos = json.RootElement.TryGetProperty("qosEnabled", out var qosValue) && qosValue.GetBoolean();
        var upload = ParseUploadRate(json.RootElement);
        if (!json.RootElement.TryGetProperty("protectedProcesses", out var processes))
            return new(rate, [], qos, upload);
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
        return new(rate, result.Distinct().OrderBy(p => p.ProcessId).ToArray(), qos, upload);
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
