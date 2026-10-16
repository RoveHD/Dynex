using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dynex.Desktop;

internal static class StartupObjects
{
    internal static bool MutexExists(string name)
    {
        // Presence checks must work when setup or a previous app was elevated.
        // Creating/opening an owned Mutex asks for write rights, which are
        // unnecessary here and can be denied by Windows' integrity policy.
        using var handle = OpenMutex(0x00100000, false, name); // SYNCHRONIZE only
        if (!handle.IsInvalid) return true;
        var error = Marshal.GetLastWin32Error();
        if (error == 2) return false;
        if (error == 5) return true; // Existing protected gate: never race setup.
        throw new Win32Exception(error, "Windows-Startschutz konnte nicht geprüft werden: " + name);
    }

    internal static Mutex? CreateInstance(string name)
    {
        if (MutexExists(name)) return null;
        try
        {
            var mutex = new Mutex(true, name, out var created);
            if (created) return mutex;
            mutex.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException) when (MutexExists(name))
        {
            // Another integrity level may have won the check/create race.
            return null;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "OpenMutexW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle OpenMutex(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
}
