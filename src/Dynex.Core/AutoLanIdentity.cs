using System.Security.Cryptography;

namespace Dynex.Core;

public static class AutoLanIdentity
{
    public static string GenerateSigningKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = key.ExportPkcs8PrivateKey();
        try { return Convert.ToBase64String(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static Guid GetDeviceId(string signingPrivateKeyBase64)
    {
        using var key = ImportPrivate(signingPrivateKeyBase64);
        return PublicId(key.ExportSubjectPublicKeyInfo());
    }

    internal static Guid PublicId(ReadOnlySpan<byte> publicKey) => new(SHA256.HashData(publicKey).AsSpan(0, 16));
    internal static ECDsa ImportPrivate(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(bytes, out var read);
            if (read != bytes.Length || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                throw new ArgumentException("AutoLAN benötigt einen ECDSA P-256 Identitätsschlüssel.");
            return key;
        }
        catch { key.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
