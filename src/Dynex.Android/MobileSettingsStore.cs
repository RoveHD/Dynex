using System.Globalization;
using Dynex.Core;
using global::Android.Content;
using global::Android.OS;
using global::Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace Dynex.Android;

public sealed record MobileSettings
{
    public string DeviceName { get; init; } = (Build.Model ?? "Handy") + " · Android";
    public bool AutoConnect { get; init; } = true;
    public bool AutoStart { get; init; } = true;
    public bool UseAdGuardProxy { get; init; } = true;
    public double? GamingDownloadLimitMbps { get; init; }
    public double SharePriority { get; init; } = 1;
}

public static class MobileSettingsStore
{
    private static readonly object ProxyIdentityGate = new();
    private static ISharedPreferences Preferences(Context context) =>
        context.GetSharedPreferences("dynex-settings", FileCreationMode.Private)!;

    public static MobileSettings Load(Context context)
    {
        using var prefs = Preferences(context);
        var defaults = new MobileSettings();
        var raw = prefs.GetString("gamingDownloadLimit", null);
        double? limit = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            double.IsFinite(parsed) && parsed is >= .1 and <= 100000 ? parsed : null;
        var weight = prefs.GetFloat("sharePriority", 1);
        return defaults with
        {
            DeviceName = prefs.GetString("name", defaults.DeviceName) ?? defaults.DeviceName,
            AutoConnect = prefs.GetBoolean("autoConnect", true),
            AutoStart = prefs.GetBoolean("autoStart", true),
            UseAdGuardProxy = prefs.Contains("adGuardProxy") ? prefs.GetBoolean("adGuardProxy", true)
                : AdGuardIntegration.IsAvailable(context),
            GamingDownloadLimitMbps = limit,
            SharePriority = weight is >= .25f and <= 4 ? weight : 1
        };
    }

    public static void Save(Context context, MobileSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DeviceName) || settings.DeviceName.Length > 80 || settings.DeviceName.Any(char.IsControl))
            throw new ArgumentException("Der Gerätename muss 1–80 lesbare Zeichen enthalten.");
        if (settings.GamingDownloadLimitMbps is { } limit && (!double.IsFinite(limit) || limit is < .1 or > 100000))
            throw new ArgumentException("Das Downloadmaximum muss zwischen 0,1 und 100000 Mbit/s liegen.");
        if (!double.IsFinite(settings.SharePriority) || settings.SharePriority is < .25 or > 4)
            throw new ArgumentException("Die Gerätepriorität ist ungültig.");
        using var prefs = Preferences(context);
        using var edit = prefs.Edit()!;
        edit.PutString("name", settings.DeviceName.Trim());
        edit.PutBoolean("autoConnect", settings.AutoConnect);
        edit.PutBoolean("autoStart", settings.AutoStart);
        edit.PutBoolean("adGuardProxy", settings.UseAdGuardProxy);
        edit.PutString("gamingDownloadLimit", settings.GamingDownloadLimitMbps?.ToString("R", CultureInfo.InvariantCulture));
        edit.PutFloat("sharePriority", (float)settings.SharePriority);
        if (!edit.Commit()) throw new IOException("Die Einstellungen konnten nicht gespeichert werden.");
    }

    public static MobileProxySettings LoadProxySettings(Context context)
    {
        lock (ProxyIdentityGate) return GetOrCreateProxySettings(context);
    }

    private static MobileProxySettings GetOrCreateProxySettings(Context context)
    {
        // App-private, not backed up. Stable credentials are required for AdGuard's saved proxy.
        using var prefs = Preferences(context);
        var password = prefs.GetString("proxyPassword", null);
        if (password is null)
        {
            password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
            using var edit = prefs.Edit()!;
            if (!edit.PutString("proxyPassword", password)!.Commit())
                throw new IOException("Die Proxy-Zugangsdaten konnten nicht gespeichert werden.");
        }
        return new(37831, "dynex", password);
    }

    internal static bool IsEnabled(Context context)
    {
        using var prefs = Preferences(context);
        return prefs.GetBoolean("enabled", false);
    }

    internal static void SetEnabled(Context context, bool enabled)
    {
        using var prefs = Preferences(context);
        using var edit = prefs.Edit()!;
        if (!edit.PutBoolean("enabled", enabled)!.Commit()) throw new IOException("Dienststatus konnte nicht gespeichert werden.");
    }

    // The persistent signing identity is encrypted by a non-exportable Android Keystore key.
    // Android backup is disabled: copying this ciphertext must never clone the device identity.
    internal static string LoadIdentity(Context context)
    {
        const string alias = "dynex.identity.aes.v1";
        using var keyStore = KeyStore.GetInstance("AndroidKeyStore")!;
        keyStore.Load(null);
        if (!keyStore.ContainsAlias(alias))
        {
            using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
            using var spec = new KeyGenParameterSpec.Builder(alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
                .SetKeySize(256).Build();
            generator.Init(spec);
            using var generated = generator.GenerateKey();
        }
        using var key = keyStore.GetKey(alias, null)!;
        using var prefs = context.GetSharedPreferences("dynex-identity", FileCreationMode.Private)!;
        var stored = prefs.GetString("protectedPkcs8", null);
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        if (stored is not null)
        {
            var bytes = Convert.FromBase64String(stored);
            if (bytes.Length < 29) throw new IOException("Die gespeicherte Geräteidentität ist beschädigt.");
            using var parameters = new GCMParameterSpec(128, bytes[..12]);
            cipher.Init(CipherMode.DecryptMode, key, parameters);
            var clear = cipher.DoFinal(bytes[12..])!;
            try { return System.Text.Encoding.UTF8.GetString(clear); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(clear); }
        }
        var identity = AutoLanIdentity.GenerateSigningKey();
        var plaintext = System.Text.Encoding.UTF8.GetBytes(identity);
        try
        {
            cipher.Init(CipherMode.EncryptMode, key);
            var encrypted = cipher.DoFinal(plaintext)!;
            var encoded = Convert.ToBase64String(cipher.GetIV()!.Concat(encrypted).ToArray());
            using var edit = prefs.Edit()!;
            if (!edit.PutString("protectedPkcs8", encoded)!.Commit()) throw new IOException("Die Geräteidentität konnte nicht gespeichert werden.");
            return identity;
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext); }
    }
}

public sealed record MobileProxySettings(int Port, string Username, string Password);
