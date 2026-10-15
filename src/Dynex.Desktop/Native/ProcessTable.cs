using System.Runtime.InteropServices;

namespace Dynex.Native;

/// <summary>
/// Process identities (PID, creation time, image name) from one NtQuerySystemInformation call.
/// Unlike Process.StartTime or OpenProcess, this opens no handle to any process, so anti-cheat
/// systems that audit handle access to protected game processes never see DYNEX touching the game.
/// </summary>
internal static class ProcessTable
{
    internal readonly record struct Identity(DateTimeOffset StartedAtUtc, string Name);

    private const int SystemProcessInformation = 5;
    private const uint StatusInfoLengthMismatch = 0xC0000004;

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int informationClass, nint buffer, int length, out int returnLength);

    /// <summary>Empty on non-Windows or on failure; callers then treat identities as unknown.</summary>
    internal static IReadOnlyDictionary<int, Identity> Snapshot()
    {
        var result = new Dictionary<int, Identity>();
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return result;
        var size = 512 * 1024;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out var needed);
                if (status == StatusInfoLengthMismatch) { size = Math.Max(size * 2, needed + 64 * 1024); continue; }
                if (status != 0) return result;
                Parse(buffer, size, result);
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return result;
    }

    // SYSTEM_PROCESS_INFORMATION (x64): NextEntryOffset@0, CreateTime@32, ImageName{Length@56, Buffer@64}, UniqueProcessId@80.
    private static void Parse(nint buffer, int size, Dictionary<int, Identity> result)
    {
        var offset = 0;
        while (offset >= 0 && offset + 88 <= size)
        {
            var entry = buffer + offset;
            var next = Marshal.ReadInt32(entry, 0);
            var pid = Marshal.ReadInt64(entry, 80);
            var created = Marshal.ReadInt64(entry, 32);
            var nameLength = (ushort)Marshal.ReadInt16(entry, 56);
            var namePointer = Marshal.ReadIntPtr(entry, 64);
            if (pid is > 0 and <= int.MaxValue && created > 0 && namePointer != 0 && nameLength is > 0 and <= 520)
            {
                var image = Marshal.PtrToStringUni(namePointer, nameLength / 2);
                var name = image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? image[..^4] : image;
                result[(int)pid] = new(DateTimeOffset.FromFileTime(created).ToUniversalTime(), name);
            }
            if (next <= 0) break;
            offset += next;
        }
    }
}
