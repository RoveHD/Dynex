using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dynex.Native;

/// <summary>Keeps DYNEX out of the game's way on the CPU.</summary>
internal static class ProcessEfficiency
{
    private const int ProcessPowerThrottling = 4;
    private const uint ExecutionSpeed = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(nint process, int informationClass, ref PowerThrottlingState information, int size);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    /// <summary>
    /// Desktop app: below-normal priority plus Windows efficiency mode (EcoQoS). Monitoring every two
    /// seconds tolerates both; a busy game always wins the CPU.
    /// </summary>
    internal static void UseBackgroundMode() => Apply(ProcessPriorityClass.BelowNormal, efficient: true);

    /// <summary>
    /// Packet helper: it sits in the path of every network packet while gaming, so it must never be
    /// throttled. It would otherwise inherit the desktop's below-normal class from its parent.
    /// </summary>
    internal static void UseLatencyCriticalMode() => Apply(ProcessPriorityClass.AboveNormal, efficient: false);

    private static void Apply(ProcessPriorityClass priority, bool efficient)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { using var self = Process.GetCurrentProcess(); self.PriorityClass = priority; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        try
        {
            // ControlMask selects execution-speed throttling; StateMask 1 = on (EcoQoS), 0 = explicitly off.
            var state = new PowerThrottlingState { Version = 1, ControlMask = ExecutionSpeed, StateMask = efficient ? ExecutionSpeed : 0 };
            SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
        }
        catch (EntryPointNotFoundException) { /* Older Windows without EcoQoS. */ }
    }
}
