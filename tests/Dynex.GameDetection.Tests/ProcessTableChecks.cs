using System.Diagnostics;
using Dynex.Native;

internal static class ProcessTableChecks
{
    internal static void Run(Action<bool, string> check)
    {
        using var self = Process.GetCurrentProcess();
        var table = ProcessTable.Snapshot();
        check(table.Count > 10, "handle-free process table lists running processes");
        check(table.TryGetValue(self.Id, out var identity) &&
            identity.StartedAtUtc == new DateTimeOffset(self.StartTime.ToUniversalTime()) &&
            identity.Name.Equals(self.ProcessName, StringComparison.OrdinalIgnoreCase),
            "process table identity matches Process.StartTime and ProcessName exactly");
    }
}
