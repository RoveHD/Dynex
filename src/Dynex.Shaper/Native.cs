using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Dynex.Shaper;

internal static unsafe class Native
{
    static Native() => NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != "WinDivert.dll") return 0;
        // Never search CWD or PATH from this elevated helper.
        return NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "native", "WinDivert.dll"));
    }

    // v2.2 ABI: 8-byte timestamp + two 32-bit flag/reserved words + 64-byte union.
    [StructLayout(LayoutKind.Sequential, Size = 80)]
    internal struct Address
    {
        public long Timestamp;
        public uint Flags;
        public uint Reserved;
        public fixed byte Data[64];
    }

    [DllImport("WinDivert.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern nint WinDivertOpen(string filter, int layer, short priority, ulong flags);
    [DllImport("WinDivert.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertRecv(nint handle, [Out] byte[] packet, uint capacity, out uint length, out Address address);
    [DllImport("WinDivert.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertSend(nint handle, byte[] packet, uint length, out uint sent, ref Address address);
    [DllImport("WinDivert.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertSetParam(nint handle, int parameter, ulong value);
    [DllImport("WinDivert.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertShutdown(nint handle, int how);
    [DllImport("WinDivert.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinDivertClose(nint handle);

    internal static Win32Exception Error(string operation)
    {
        var code = Marshal.GetLastPInvokeError();
        return new Win32Exception(code, $"{operation} (Win32 {code}: {new Win32Exception(code).Message})");
    }
}
