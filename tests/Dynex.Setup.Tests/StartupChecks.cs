using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Dynex.Desktop;

internal static class StartupChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var name = @"Local\DYNEX.StartupCheck." + Guid.NewGuid().ToString("N");
        check(!StartupObjects.MutexExists(name), "missing startup gate does not block startup");
        using (var first = StartupObjects.CreateInstance(name))
        {
            check(first is not null && StartupObjects.MutexExists(name), "first instance owns a real named startup mutex");
            check(StartupObjects.CreateInstance(name) is null, "second instance does not acquire or release another instance's ownership");
            first!.ReleaseMutex();
        }
        check(!StartupObjects.MutexExists(name), "closed instance leaves no stale startup gate");

        // Equivalent access failure to opening an elevated object's write rights,
        // without elevation or modifying any production object/security policy.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:(D;;0x00000001;;;WD)(A;;0x00100000;;;WD)", 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            using var restricted = CreateMutex(ref attributes, false, name);
            if (restricted.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var reproduced = false;
            try { using var old = new Mutex(true, name, out _); }
            catch (UnauthorizedAccessException) { reproduced = true; }
            check(reproduced, "original named Mutex constructor reproduces access-to-path-denied for a read-only gate");
            check(StartupObjects.MutexExists(name), "startup detects protected gate using only read/synchronize rights");
            check(StartupObjects.CreateInstance(name) is null, "protected running instance is detected without another access-denied startup failure");
        }
        finally { LocalFree(descriptor); }
        check(!StartupObjects.MutexExists(name), "isolated permission fixture is fully removed after handle disposal");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool Inherit;
    }
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", EntryPoint = "CreateMutexW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateMutex(ref SecurityAttributes attributes, [MarshalAs(UnmanagedType.Bool)] bool owned, string name);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr value);
}
