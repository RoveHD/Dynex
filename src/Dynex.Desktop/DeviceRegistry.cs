using System.IO;
using System.Text.Json;
using Dynex.Core;

namespace Dynex.Desktop;

public enum DeviceDisposition { Normal, Ignored, Blocked }
public sealed record KnownDevice(Guid DeviceId, string DeviceName, string? Alias, DateTimeOffset LastSeenUtc,
    string? Address, string? AppVersion, DeviceDisposition Disposition = DeviceDisposition.Normal, int? RemotePort = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? DeviceName : Alias;
}
public sealed record DeviceRegistryEntry(KnownDevice Device, bool Online)
{
    public override string ToString() => DisplayName;
    public Guid DeviceId => Device.DeviceId;
    public string DisplayName => Device.DisplayName;
    public string DeviceName => Device.DeviceName;
    public string LastSeenText => Device.LastSeenUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
    public string Status => Device.Disposition switch { DeviceDisposition.Ignored => "Ignoriert", DeviceDisposition.Blocked => "Gesperrt", _ => Online ? "Online" : "Offline" };
    public string Address => Device.Address ?? "Unbekannt";
}

/// <summary>Only authenticated peer snapshots enter this local registry. Preferences are never sent over LAN.</summary>
internal sealed class DeviceRegistry
{
    private readonly string path;
    private readonly object gate = new();
    private Dictionary<Guid, KnownDevice> devices;
    private HashSet<Guid> online = [];
    private DateTimeOffset savedAt;
    private bool dirty;
    internal DeviceRegistry(string directory)
    {
        path = Path.Combine(directory, "devices.json");
        if (!File.Exists(path)) { devices = []; return; }
        // Leave room for all 512 entries with JSON-escaped Unicode names and aliases.
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Geräteliste ist zu groß: " + path);
        var entries = JsonSerializer.Deserialize<KnownDevice[]>(File.ReadAllText(path)) ?? throw new InvalidDataException("Leere Geräteliste: " + path);
        if (entries.Length > 512 || entries.Any(e => e is null) || entries.Select(e => e.DeviceId).Distinct().Count() != entries.Length) throw new InvalidDataException("Ungültige Geräteliste: " + path);
        foreach (var entry in entries) Validate(entry);
        devices = entries.ToDictionary(d => d.DeviceId);
    }
    internal IReadOnlyCollection<Guid> ExcludedIds { get { lock (gate) return devices.Values.Where(d => d.Disposition != DeviceDisposition.Normal).Select(d => d.DeviceId).ToArray(); } }
    internal IReadOnlyList<DeviceRegistryEntry> Snapshot()
    {
        lock (gate) return devices.Values.Select(d => new DeviceRegistryEntry(d, online.Contains(d.DeviceId) && d.Disposition == DeviceDisposition.Normal))
            .OrderByDescending(d => d.Online).ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal void Observe(IReadOnlyList<PeerAdvertisement> authenticated)
    {
        lock (gate)
        {
            online = [];
            foreach (var peer in authenticated)
            {
                if (devices.TryGetValue(peer.DeviceId, out var prior) && prior.Disposition != DeviceDisposition.Normal) continue;
                if (prior is null && devices.Count >= 512) continue;
                var next = new KnownDevice(peer.DeviceId, peer.DeviceName, prior?.Alias, peer.LastSeenUtc, peer.Address, peer.AppVersion, prior?.Disposition ?? DeviceDisposition.Normal, peer.RemotePort);
                Validate(next);
                online.Add(peer.DeviceId);
                if (prior != next) { devices[peer.DeviceId] = next; dirty = true; }
            }
            // First sighting is durable immediately; subsequent heartbeats are coalesced.
            if (dirty && (savedAt == default || DateTimeOffset.UtcNow - savedAt >= TimeSpan.FromSeconds(30))) SaveLocked(devices);
        }
    }
    internal void SetPreferences(Guid id, string? alias, DeviceDisposition disposition)
    {
        lock (gate)
        {
            if (!devices.TryGetValue(id, out var prior)) throw new ArgumentException("Gerät nicht in der lokalen Liste.");
            var next = prior with { Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim(), Disposition = disposition };
            Validate(next);
            var candidate = new Dictionary<Guid, KnownDevice>(devices) { [id] = next };
            SaveLocked(candidate); // A failed write must not apply an unpersisted block preference.
            devices = candidate;
            if (disposition != DeviceDisposition.Normal) online.Remove(id);
        }
    }
    internal void Flush() { lock (gate) if (dirty) SaveLocked(devices); }
    private void SaveLocked(Dictionary<Guid, KnownDevice> candidate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(candidate.Values.ToArray(), new JsonSerializerOptions { WriteIndented = true });
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
        File.Move(temporary, path, true);
        dirty = false; savedAt = DateTimeOffset.UtcNow;
    }
    private static void Validate(KnownDevice device)
    {
        if (device.DeviceId == Guid.Empty || string.IsNullOrWhiteSpace(device.DeviceName) || device.DeviceName.Length > 80 || device.DeviceName.Any(char.IsControl) ||
            device.Alias is { } alias && (alias.Length > 80 || alias.Any(char.IsControl)) || !Enum.IsDefined(device.Disposition) ||
            device.Address?.Length > 64 || device.AppVersion?.Length > 16 || device.RemotePort is < 1 or > 65535)
            throw new InvalidDataException("Ungültiger Eintrag in der Geräteliste.");
    }
}
