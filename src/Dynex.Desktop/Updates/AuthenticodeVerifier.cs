using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Dynex.Desktop.Updates;

/// <summary>Authenticode state of a file. Trusted means Windows validated signature, file hash and chain.</summary>
public sealed record SignatureCheck(bool Signed, bool Trusted, string? Subject)
{
    public static readonly SignatureCheck Unsigned = new(false, false, null);
}

public static class SignaturePolicy
{
    /// <summary>
    /// Publisher continuity: once the running DYNEX is signed, an update must be validly signed by the same
    /// certificate subject (renewed certificates keep the subject). Unsigned legacy builds still accept
    /// hash-verified updates, but never a file that carries a broken or untrusted signature.
    /// </summary>
    public static string? Reject(SignatureCheck current, SignatureCheck candidate)
    {
        if (candidate.Signed && !candidate.Trusted) return "Die Signatur des Updates ist ungültig oder nicht vertrauenswürdig.";
        if (!current.Trusted) return null;
        if (!candidate.Trusted) return "Das Update ist nicht signiert, die installierte Version aber schon.";
        return string.Equals(current.Subject, candidate.Subject, StringComparison.Ordinal) ? null
            : "Das Update stammt von einem anderen Herausgeber als die installierte Version.";
    }
}

public static class AuthenticodeVerifier
{
    public static SignatureCheck Inspect(string path)
    {
        if (!OperatingSystem.IsWindows()) return SignatureCheck.Unsigned;
        var status = Verify(path);
        if (status == TrustENoSignature) return SignatureCheck.Unsigned;
        string? subject = null;
        try
        {
#pragma warning disable SYSLIB0057 // Reads the embedded signer only; WinVerifyTrust above validates it.
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            subject = certificate.Subject;
        }
        catch (System.Security.Cryptography.CryptographicException) { }
        return new(true, status == 0 && subject is not null, subject);
    }

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static int Verify(string path)
    {
        var file = new FileInfoData { Size = Marshal.SizeOf<FileInfoData>(), FilePath = path };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoData>());
        try
        {
            Marshal.StructureToPtr(file, filePointer, false);
            var data = new TrustData
            {
                Size = Marshal.SizeOf<TrustData>(), UiChoice = 2 /* WTD_UI_NONE */, RevocationChecks = 0 /* WTD_REVOKE_NONE */,
                UnionChoice = 1 /* WTD_CHOICE_FILE */, File = filePointer, StateAction = 1 /* WTD_STATEACTION_VERIFY */,
                ProviderFlags = 0x1000 /* WTD_CACHE_ONLY_URL_RETRIEVAL: never block on network */
            };
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.StateAction = 2; // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result;
        }
        finally
        {
            Marshal.DestroyStructure<FileInfoData>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfoData
    {
        public int Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustData
    {
        public int Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public int UiChoice;
        public int RevocationChecks;
        public int UnionChoice;
        public IntPtr File;
        public int StateAction;
        public IntPtr StateData;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UrlReference;
        public int ProviderFlags;
        public int UiContext;
        public IntPtr SignatureSettings;
    }
}
