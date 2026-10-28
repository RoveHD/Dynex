using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Dynex.Native;

namespace Dynex.Shaper;

/// <summary>SCM host only. Packet capture belongs to short-lived, parent-bound workers.</summary>
internal static class PrioritizationService
{
    private static readonly ServiceMainDelegate MainCallback = ServiceMain;
    private static readonly HandlerDelegate HandlerCallback = HandleControl;
    private static readonly CancellationTokenSource Stop = new();
    private static readonly object StatusLock = new();
    private static IntPtr statusHandle;
    private static uint exitCode;
    private static uint checkpoint;

    internal static int Run()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
            throw new UnauthorizedAccessException("Der installierte Netzwerkdienst muss als LocalSystem laufen.");
        var table = new[] { new ServiceTable { Name = PrioritizationServiceClient.ServiceName, Main = MainCallback }, new ServiceTable() };
        if (!StartServiceCtrlDispatcher(table)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (int)exitCode;
    }

    private static void ServiceMain(uint count, IntPtr args)
    {
        statusHandle = RegisterServiceCtrlHandlerEx(PrioritizationServiceClient.ServiceName, HandlerCallback, IntPtr.Zero);
        if (statusHandle == IntPtr.Zero) { exitCode = (uint)Marshal.GetLastWin32Error(); return; }
        Report(2);
        try
        {
            using var broker = new PrioritizationBroker();
            // Create the protected endpoint before telling SCM that clients may connect.
            var endpoint = broker.CreateEndpoint();
            Report(4);
            broker.RunAsync(endpoint, Stop.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (Stop.IsCancellationRequested) { }
        catch { exitCode = 1064; }
        finally { Report(1); }
    }

    private static uint HandleControl(uint control, uint eventType, IntPtr data, IntPtr context)
    {
        if (control is 1 or 5)
        {
            Report(3);
            Stop.Cancel();
        }
        else if (control == 4) Report(Stop.IsCancellationRequested ? 3u : 4u);
        return 0;
    }

    private static void Report(uint state)
    {
        lock (StatusLock)
        {
            var status = new ServiceStatus
            {
                Type = 0x10, State = state, Controls = state == 4 ? 1u | 4u : 0,
                Win32Exit = state == 1 ? exitCode : 0, Checkpoint = state is 2 or 3 ? ++checkpoint : 0,
                WaitHint = state is 2 or 3 ? 15000u : 0
            };
            SetServiceStatus(statusHandle, ref status);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainDelegate(uint count, IntPtr args);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint HandlerDelegate(uint control, uint eventType, IntPtr data, IntPtr context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTable { [MarshalAs(UnmanagedType.LPWStr)] internal string? Name; internal ServiceMainDelegate? Main; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus { internal uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint; }
    [DllImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTable[] table);
    [DllImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string name, HandlerDelegate callback, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
}
