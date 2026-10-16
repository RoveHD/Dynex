using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using global::Android.Content;
using global::Android.Content.PM;
using global::Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using CipherMode = Javax.Crypto.CipherMode;

namespace Dynex.Android;

/// <summary>
/// AdGuard 4.x automation requests. Sending a broadcast does not acknowledge
/// that AdGuard accepted its password or applied the requested configuration.
/// https://adguard.com/kb/adguard-for-android/solving-problems/tasker/
/// </summary>
public static class AdGuardIntegration
{
    private const string Package = "com.adguard.android";
    private const string Receiver = "com.adguard.android.receiver.AutomationReceiver";
    private const string KeyAlias = "dynex.adguard.automation.aes.v1";
    private const int MaximumPasswordBytes = 4096;
    private static readonly object PasswordGate = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] Header = [0x44, 0x59, 0x4e, 1];

    /// <summary>Presence only; this does not reveal whether its integration is active.</summary>
    public static bool IsVpnInstalled(Context context)
    {
        try { using var info = context.PackageManager!.GetApplicationInfo("com.adguard.vpn", (PackageInfoFlags)0); return info is not null; }
        catch (PackageManager.NameNotFoundException) { return false; }
        catch (Java.Lang.SecurityException) { return false; }
    }

    /// <summary>Requires manifest package visibility for com.adguard.android on Android 11+.</summary>
    public static bool IsAvailable(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            using var component = new ComponentName(Package, Receiver);
            using var info = context.PackageManager!.GetReceiverInfo(component, (PackageInfoFlags)0);
            return info is { Enabled: true, Exported: true } && info.ApplicationInfo?.Enabled == true &&
                (string.IsNullOrEmpty(info.Permission) ||
                    context.CheckSelfPermission(info.Permission) == global::Android.Content.PM.Permission.Granted);
        }
        catch (PackageManager.NameNotFoundException) { return false; }
        catch (Java.Lang.SecurityException) { return false; }
    }

    /// <summary>False also covers unreadable, modified or undecryptable saved credentials.</summary>
    public static bool HasSavedPassword(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (PasswordGate)
        {
            try { return ReadPassword(context) is not null; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (Java.Lang.Exception) { return false; }
            catch (CryptographicException) { return false; }
            catch (ArgumentException) { return false; }
        }
    }

    /// <summary>
    /// Saves only AES-GCM ciphertext in app-private NoBackupFilesDir. The distinct
    /// non-exportable Android Keystore key is never shared with proxy credentials.
    /// </summary>
    public static void SavePassword(Context context, string password)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidatePassword(password);
        var clear = StrictUtf8.GetBytes(password);
        try
        {
            lock (PasswordGate)
            {
                using var store = OpenStore();
                if (!store.ContainsAlias(KeyAlias))
                {
                    using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
                    using var spec = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                        .SetBlockModes(KeyProperties.BlockModeGcm)
                        .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
                        .SetRandomizedEncryptionRequired(true)
                        .SetKeySize(256).Build();
                    generator.Init(spec);
                    using var generated = generator.GenerateKey();
                }
                using var key = store.GetKey(KeyAlias, null) ?? throw new IOException("Der AdGuard-Schlüssel ist nicht verfügbar.");
                using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
                cipher.Init(CipherMode.EncryptMode, key);
                cipher.UpdateAAD(Encoding.UTF8.GetBytes(KeyAlias));
                var encrypted = cipher.DoFinal(clear)!;
                var iv = cipher.GetIV()!;
                if (iv.Length != 12) throw new IOException("Das AdGuard-Passwort konnte nicht sicher verschlüsselt werden.");
                var path = PasswordPath(context);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        file.Write(Header); file.Write(iv); file.Write(encrypted);
                        file.Flush(flushToDisk: true);
                    }
                    File.Move(temporary, path, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    /// <summary>Queues proxy_default followed by proxy_state. No configuration success or ACK is implied.</summary>
    public static void ConfigureProxy(Context context, MobileProxySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(settings));
        // AdGuard documents an ampersand-separated parameter string, but no
        // escaping convention. Our generated credentials are safe ASCII; reject
        // separators rather than guessing URL-decoding behavior in AdGuard.
        ValidateProxyCredential(settings.Username); ValidateProxyCredential(settings.Password);
        var password = RequirePassword(context);
        using var configure = Request("proxy_default", password);
        configure.PutExtra("server", "host=127.0.0.1&port=" + settings.Port.ToString(CultureInfo.InvariantCulture) +
            "&type=SOCKS5&username=" + settings.Username + "&password=" + settings.Password + "&udp=true");
        using var enable = Request("proxy_state", password);
        enable.PutExtra("enable", true);
        context.SendBroadcast(configure);
        context.SendBroadcast(enable);
    }

    /// <summary>Requests only outbound-proxy deactivation; protection and DNS filtering remain untouched.</summary>
    public static void DisableProxy(Context context)
    {
        using var disable = Request("proxy_state", RequirePassword(context));
        disable.PutExtra("enable", false);
        context.SendBroadcast(disable);
    }

    private static Intent Request(string action, string password)
    {
        var intent = new Intent(action);
        intent.SetClassName(Package, Receiver);
        intent.PutExtra("password", password);
        intent.PutExtra("quiet", true);
        return intent;
    }

    private static string RequirePassword(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsAvailable(context)) throw new InvalidOperationException("Der AdGuard-Automatisierungsempfänger ab Version 4 ist nicht verfügbar.");
        lock (PasswordGate)
            return ReadPassword(context) ?? throw new InvalidOperationException("Das AdGuard-Automatisierungspasswort fehlt. Bitte erneut lokal eingeben.");
    }

    private static string? ReadPassword(Context context)
    {
        var path = PasswordPath(context);
        if (!File.Exists(path)) return null;
        var length = new FileInfo(path).Length;
        if (length is < 33 or > MaximumPasswordBytes + 32) throw new IOException("Das gespeicherte AdGuard-Passwort ist beschädigt.");
        var envelope = File.ReadAllBytes(path);
        if (envelope.Length != length || !envelope.AsSpan(0, 4).SequenceEqual(Header))
            throw new IOException("Das gespeicherte AdGuard-Passwort ist beschädigt.");
        using var store = OpenStore();
        if (!store.ContainsAlias(KeyAlias)) return null;
        using var key = store.GetKey(KeyAlias, null) ?? throw new IOException("Der AdGuard-Schlüssel ist nicht verfügbar.");
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        using var parameters = new GCMParameterSpec(128, envelope[4..16]);
        cipher.Init(CipherMode.DecryptMode, key, parameters);
        cipher.UpdateAAD(Encoding.UTF8.GetBytes(KeyAlias));
        var clear = cipher.DoFinal(envelope[16..])!;
        try
        {
            var password = StrictUtf8.GetString(clear);
            ValidatePassword(password);
            return password;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private static KeyStore OpenStore()
    {
        var store = KeyStore.GetInstance("AndroidKeyStore")!;
        try { store.Load(null); return store; }
        catch { store.Dispose(); throw; }
    }

    private static string PasswordPath(Context context) => Path.Combine(
        context.NoBackupFilesDir?.AbsolutePath ?? throw new IOException("Der private Passwortspeicher ist nicht verfügbar."),
        "dynex-adguard-automation.v1.prefs");

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length > 1024 || password.Any(char.IsControl) ||
            StrictUtf8.GetByteCount(password) > MaximumPasswordBytes)
            throw new ArgumentException("Das Automatisierungspasswort muss 1–1024 lesbare Zeichen enthalten.", nameof(password));
    }

    private static void ValidateProxyCredential(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 255 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-' and not '~'))
            throw new ArgumentException("Die Proxy-Zugangsdaten enthalten nicht unterstützte Zeichen.");
    }
}
