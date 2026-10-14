using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;

namespace Dynex.Desktop;

/// <summary>Verified setup of the official independent filter, also used by DYNEX setup.</summary>
internal static class PacketFilterSetup
{
    private const string DownloadUrl = "https://github.com/wiresock/ndisapi/releases/download/v3.6.2/Windows.Packet.Filter.3.6.2.1.x64.msi";
    private const string ExpectedHash = "9C388C0B7F189F7FA98720BAE2CAECF7D64F30910838B80B438ECF8956B8502C";
    private const long ExpectedSize = 819200;
    public static bool IsInstalled
    {
        get
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var service = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\NDISRD");
            return service is not null;
        }
    }

    public static async Task<bool> InstallAsync(Action<string> progress, CancellationToken token = default)
    {
        if (IsInstalled) return false;
        return await InstallPackageAsync(await PreparePackageAsync(progress, token), progress, token);
    }

    internal static async Task<string> PreparePackageAsync(Action<string> progress, CancellationToken token = default)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DYNEX", "setup");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Windows.Packet.Filter.3.6.2.1.x64.msi");
        progress("Die Downloadbegrenzung wird vorbereitet …");
        if (!await HasExpectedHashAsync(path, token))
        {
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".download");
            try
            {
                using var download = CancellationTokenSource.CreateLinkedTokenSource(token);
                download.CancelAfter(TimeSpan.FromSeconds(60));
                var downloadToken = download.Token;
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, downloadToken);
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(downloadToken))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[65536];
                    long total = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, downloadToken)) > 0)
                    {
                        total += read;
                        if (total > ExpectedSize) throw new InvalidDataException("Das Einrichtungspaket hat eine unerwartete Größe.");
                        await output.WriteAsync(buffer.AsMemory(0, read), downloadToken);
                    }
                }
                if (!await HasExpectedHashAsync(temporary, token)) throw new InvalidDataException("Die Prüfung des Einrichtungspakets ist fehlgeschlagen.");
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        VerifySignature(path);
        return path;
    }

    internal static async Task<bool> InstallPackageAsync(string path, Action<string> progress, CancellationToken token = default)
    {
        if (IsInstalled) return false;
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains('"'))
            throw new InvalidDataException("Das Einrichtungspaket muss eine lokale absolute Datei sein.");
        // Keep the verified file read-locked through the elevated installation;
        // another process cannot replace it between verification and msiexec.
        await using var verifiedPackage = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (verifiedPackage.Length != ExpectedSize || Convert.ToHexString(await SHA256.HashDataAsync(verifiedPackage, token)) != ExpectedHash)
            throw new InvalidDataException("Das Einrichtungspaket wurde vor der Prüfung verändert.");
        verifiedPackage.Position = 0;
        VerifySignatureCore(path, verifiedPackage.SafeFileHandle.DangerousGetHandle());
        // Another setup may have completed while this package was downloaded.
        if (IsInstalled) return false;
        progress("Windows richtet die Downloadbegrenzung ein. Bitte die Windows-Rückfrage bestätigen …");
        var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        {
            UseShellExecute = !elevated, Verb = elevated ? "" : "runas", CreateNoWindow = elevated,
            Arguments = $"/i \"{path}\" /passive /norestart"
        }) ?? throw new IOException("Die Windows-Einrichtung konnte nicht gestartet werden.");
        await process.WaitForExitAsync(token);
        if (process.ExitCode == 1602) throw new OperationCanceledException("Die Einrichtung wurde abgebrochen.");
        if (process.ExitCode is not (0 or 3010)) throw new System.ComponentModel.Win32Exception(process.ExitCode, $"Die Windows-Einrichtung meldet Fehler {process.ExitCode}.");
        if (!IsInstalled) throw new IOException("Der Netzwerkfilter wurde noch nicht registriert.");
        return process.ExitCode == 3010;
    }

    private static async Task<bool> HasExpectedHashAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != ExpectedSize) return false;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token)) == ExpectedHash;
    }

    internal static void VerifySignature(string path) => VerifySignatureCore(path, IntPtr.Zero);

    private static void VerifySignatureCore(string path, IntPtr lockedHandle)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path, FileHandle = lockedHandle };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(file, pointer, false);
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = pointer };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (result != 0) throw new InvalidDataException($"Windows hat die Paketsignatur nicht bestätigt (0x{result:X8}).");
        }
        finally { Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        internal uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] internal string Path;
        internal IntPtr FileHandle, KnownSubject;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        internal uint Size;
        internal IntPtr Policy, Sip;
        internal uint UiChoice, RevocationChecks, UnionChoice;
        internal IntPtr File;
        internal uint StateAction;
        internal IntPtr StateData, Url;
        internal uint ProviderFlags, UiContext;
        internal IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
