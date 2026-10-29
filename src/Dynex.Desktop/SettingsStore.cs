using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dynex.Core;

namespace Dynex.Desktop;

internal sealed class SettingsStore
{
    private readonly string directory;
    public string DirectoryPath => directory;
    public SettingsStore(string? path = null) => directory = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DYNEX");
    public UserSettings Load()
    {
        var path = Path.Combine(directory, "settings.json");
        if (!File.Exists(path)) return new();
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw FileFailure(error, "Einstellungen lesen", path); }
        var settings = Normalize(JsonSerializer.Deserialize<UserSettings>(json) ?? throw new InvalidDataException("Leere Einstellungsdatei."));
        Validate(settings);
        return settings;
    }
    public void Save(UserSettings value)
    {
        value = Normalize(value);
        Validate(value);
        AtomicWrite("settings.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })));
    }
    public Identity LoadIdentity()
    {
        var path = Path.Combine(directory, "identity.dpapi");
        if (File.Exists(path))
        {
            byte[] protectedIdentity;
            try { protectedIdentity = File.ReadAllBytes(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw FileFailure(error, "Geräteidentität lesen", path); }
            var stored = JsonSerializer.Deserialize<Identity>(Dpapi.Transform(protectedIdentity, false)) ?? throw new InvalidDataException("Ungültige Geräteidentität.");
            if (stored.SigningPrivateKeyBase64 is null)
            {
                stored = stored with { SigningPrivateKeyBase64 = AutoLanIdentity.GenerateSigningKey() };
                SaveIdentity(stored);
            }
            _ = AutoLanIdentity.GetDeviceId(stored.SigningPrivateKeyBase64);
            return stored;
        }
        var identity = new Identity(Guid.NewGuid(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), AutoLanIdentity.GenerateSigningKey());
        SaveIdentity(identity);
        return identity;
    }
    public void SaveIdentity(Identity identity) => AtomicWrite("identity.dpapi", Dpapi.Transform(JsonSerializer.SerializeToUtf8Bytes(identity), true));
    private void AtomicWrite(string name, byte[] data)
    {
        var path = Path.Combine(directory, name);
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { file.Write(data); file.Flush(true); }
            File.Move(temp, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw FileFailure(error, "Lokale Daten speichern", path); }
    }
    private static Exception FileFailure(Exception error, string operation, string path)
    {
        var message = $"{operation} fehlgeschlagen: {path}. {error.Message}";
        return error is UnauthorizedAccessException ? new UnauthorizedAccessException(message, error) : new IOException(message, error);
    }
    public static void Validate(UserSettings value)
    {
        ApplicationRules.Validate(value.ApplicationRules);
        NetworkProfiles.Validate(value);
        if (string.IsNullOrWhiteSpace(value.DeviceName) || value.DeviceName.Length > 64 || value.DeviceName.Any(char.IsControl)) throw new ArgumentException("Gerätename: 1–64 sichtbare Zeichen.");
        Range(value.CapacityMbps, 1, 10000, "Anschlussgeschwindigkeit");
        Range(value.BackgroundPercent, 10, 95, "Downloadbudget (%)");
        Range(value.ReservePerGamerMbps, 0, value.CapacityMbps, "Reserve je Gaming-PC");
        if (value.ManualLimitMbps is double limit) Range(limit, 0.1, 10000, "Manuelles Limit");
        if (value.Profile is not ("Turnier" or "Standard" or "Entspannt" or "Eigenes")) throw new ArgumentException("Unbekanntes Profil.");
        if (value.SharePriority is not (0.5 or 1 or 2)) throw new ArgumentException("Priorität muss Niedrig, Normal oder Hoch sein.");
        if (value.UploadCapacityMbps is double upload) Range(upload, 0.1, 10000, "Gemessener Upload");
        if (value.ManualUploadLimitMbps is double uploadLimit) Range(uploadLimit, 0.1, 10000, "Upload-Obergrenze");
        foreach (var latency in new[] { value.IdleLatencyMs, value.LoadedLatencyMs })
            if (latency is double ms) Range(ms, 0, 10000, "Gemessener Ping");
        if (!Enum.IsDefined(value.Mode)) throw new ArgumentException("Ungültiger Gaming-Modus.");
        foreach (var target in new[] { value.RouterTarget, value.ExternalTarget })
            if (!string.IsNullOrWhiteSpace(target) && (!IPAddress.TryParse(target, out var ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))) throw new ArgumentException("Messziele müssen gültige IP-Adressen sein.");
        if (string.IsNullOrWhiteSpace(value.ExternalTarget)) throw new ArgumentException("Externes Messziel fehlt.");
        if (value.GameProcesses.Length > 4096) throw new ArgumentException("Zu viele Prozessnamen.");
        foreach (var process in Split(value.GameProcesses))
            if (!Regex.IsMatch(process, @"^[a-zA-Z0-9][a-zA-Z0-9_. -]{0,79}$")) throw new ArgumentException("Nur Prozessnamen ohne Pfad (maximal 80 Zeichen) angeben.");
        foreach (var (list, label) in new[] { (value.EarlyLimitGames, "Ab Spielstart priorisierte Spiele") })
        {
            if (list.Length > 2048 || Split(list).Length > 32) throw new ArgumentException($"{label}: höchstens 32 Namen.");
            foreach (var process in Split(list))
                if (!Regex.IsMatch(process, @"^[a-zA-Z0-9][a-zA-Z0-9_. -]{0,63}$")) throw new ArgumentException($"{label}: nur Prozessnamen ohne Pfad angeben.");
        }
        if (!TimeOnly.TryParseExact(value.NightFrom, "HH:mm", out _) || !TimeOnly.TryParseExact(value.NightTo, "HH:mm", out _))
            throw new ArgumentException("Zeitplan: Uhrzeiten im Format HH:mm angeben (z. B. 01:00).");
        if (value.WeekendProfile is not ("" or "Turnier" or "Standard" or "Entspannt")) throw new ArgumentException("Zeitplan: unbekanntes Wochenendprofil.");
        if (Split(value.DirectPeers).Length > 32) throw new ArgumentException("Maximal 32 direkte Geräteadressen.");
        foreach (var address in Split(value.DirectPeers))
            if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || !IsPrivate(ip)) throw new ArgumentException("Direkte Geräte benötigen eine private IPv4-Adresse.");
    }
    internal static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31);
    }
    internal static string[] Split(string value) => value.Split([';', ',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static void Range(double n, double min, double max, string field)
    {
        if (!double.IsFinite(n) || n < min || n > max) throw new ArgumentException($"{field}: Wert zwischen {min} und {max} erforderlich.");
    }
    internal static UserSettings Normalize(UserSettings value) => NetworkProfiles.Capture(value with
    {
        DeviceName = Environment.MachineName,
        // Automatic detection replaces the retired focus/always-on/off selector.
        Mode = value.Mode is GamingMode.On or GamingMode.Off ? GamingMode.Auto : value.Mode,
        // Legacy fields remain readable so an upgrade/import never loses unrelated settings.
        // None may restore bandwidth shaping in the prioritization-only product.
        ApplicationRules = [],
        ManualLimitMbps = null,
        ManualUploadLimitMbps = null,
        UploadLimitEnabled = false,
        ReservePerGamerMbps = 0,
        Adaptive = false,
        NightFree = false,
        WeekendProfile = "",
        NetworkProfiles = value.NetworkProfiles is null ? null! : value.NetworkProfiles.Select(profile =>
            profile is null || profile.Policy is null ? profile! : profile with { Policy = profile.Policy.WithoutLimits() }).ToArray()
    });
    internal sealed record Identity(Guid DeviceId, string GroupKey, string? SigningPrivateKeyBase64 = null);

    private static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
        internal static byte[] Transform(byte[] data, bool encrypt)
        {
            var input = new Blob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
            try
            {
                Marshal.Copy(data, 0, input.Data, data.Length);
                Blob output;
                var success = encrypt ? CryptProtectData(ref input, "DYNEX LAN identity", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
                if (!success) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "DYNEX-Schlüssel konnte nicht mit Windows geschützt/gelesen werden.");
                try { var bytes = new byte[output.Length]; Marshal.Copy(output.Data, bytes, 0, bytes.Length); return bytes; }
                finally { LocalFree(output.Data); }
            }
            finally { Marshal.FreeHGlobal(input.Data); }
        }
    }
}
