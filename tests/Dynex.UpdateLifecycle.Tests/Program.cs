using System.Runtime.InteropServices;
using Dynex.Desktop.Updates;

try
{
    if (OperatingSystem.IsWindows()) TestErrorMode.SuppressDialogs();
    var checks = 0;
    void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks++;
        Console.WriteLine("PASS: " + name);
    }
    var safe = false;
    var reads = 0;
    var launches = 0;
    var errors = new List<string>();
    using (var coordinator = new UpdateInstallCoordinator(() => safe,
        () => { reads++; return Task.FromResult<string?>("verified.exe"); },
        _ => { launches++; return Task.FromResult(true); }, errors.Add))
    {
        await coordinator.TryInstallAsync();
        Check(reads == 0 && launches == 0, "Disabled updates or pending edits prevent installer access");
        safe = true;
        await coordinator.TryInstallAsync();
        await coordinator.TryInstallAsync();
        Check(reads == 1 && launches == 1, "Successful handoff starts once per application lifetime");
    }

    var ready = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    launches = 0;
    using (var coordinator = new UpdateInstallCoordinator(() => safe, () => ready.Task,
        _ => { launches++; return Task.FromResult(true); }, errors.Add))
    {
        var pending = coordinator.TryInstallAsync();
        await coordinator.TryInstallAsync();
        safe = false;
        ready.SetResult("verified.exe");
        await pending;
        Check(launches == 0, "Disabling updates while verification runs prevents handoff");
        safe = true;
        await coordinator.TryInstallAsync();
        Check(launches == 1, "Deferred handoff resumes after settings are saved");
    }

    ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    reads = launches = 0;
    using (var coordinator = new UpdateInstallCoordinator(() => true,
        () => { reads++; return ready.Task; },
        _ => { launches++; return Task.FromResult(true); }, errors.Add))
    {
        var pending = coordinator.TryInstallAsync();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => coordinator.TryInstallAsync()));
        ready.SetResult("verified.exe");
        await pending;
        Check(reads == 1 && launches == 1, "Concurrent ready notifications cannot duplicate the installer");
    }

    launches = 0;
    using (var coordinator = new UpdateInstallCoordinator(() => true,
        () => Task.FromResult<string?>("verified.exe"),
        _ => { launches++; return Task.FromResult(false); }, errors.Add))
    {
        await coordinator.TryInstallAsync();
        await coordinator.TryInstallAsync();
        Check(launches == 1, "Refused handoff does not retry on its own status event");
        coordinator.AllowRetry();
        await coordinator.TryInstallAsync();
        Check(launches == 2, "Explicit check can retry a failed handoff");
    }

    launches = 0;
    string? selectedInstaller = null;
    using (var coordinator = new UpdateInstallCoordinator(() => true,
        () => Task.FromResult<string?>(selectedInstaller),
        _ => { launches++; return Task.FromResult(false); }, errors.Add))
    {
        await coordinator.TryInstallAsync();
        Check(launches == 0, "Missing or rejected installer never reaches process launch");
        selectedInstaller = "version-one.exe";
        await coordinator.TryInstallAsync();
        selectedInstaller = "version-two.exe";
        await coordinator.TryInstallAsync();
        Check(launches == 2, "A failed version does not suppress a different verified update");
    }

    launches = 0;
    errors.Clear();
    selectedInstaller = "restart-cache.exe";
    using (var coordinator = new UpdateInstallCoordinator(() => true,
        () => Task.FromResult<string?>(selectedInstaller),
        _ => { launches++; return Task.FromResult(false); }, errors.Add))
    {
        await coordinator.SkipReadyInstallerAsync();
        await coordinator.TryInstallAsync();
        Check(launches == 0, "Installer restart cannot automatically relaunch the same unconsumed cache");
        selectedInstaller = "newer-release.exe";
        await coordinator.TryInstallAsync();
        Check(launches == 1, "Restart suppression allows a different future release");
        selectedInstaller = "restart-cache.exe";
        coordinator.AllowRetry();
        await coordinator.TryInstallAsync();
        Check(launches == 2, "Explicit check can retry the version skipped after installer restart");
    }

    launches = 0;
    errors.Clear();
    using (var coordinator = new UpdateInstallCoordinator(() => true,
        () => Task.FromResult<string?>("verified.exe"),
        _ => { launches++; throw new IOException("simulated launch failure"); }, errors.Add))
    {
        await coordinator.TryInstallAsync();
        await coordinator.TryInstallAsync();
        Check(errors.Count == 1 && errors[0].Contains("simulated launch failure") && launches == 1,
            "Launch exceptions remain visible without a retry loop");
    }

    launches = 0;
    ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var coordinator = new UpdateInstallCoordinator(() => true, () => ready.Task,
        _ => { launches++; return Task.FromResult(true); }, errors.Add))
    {
        var pending = coordinator.TryInstallAsync();
        coordinator.Dispose();
        ready.SetResult("verified.exe");
        await pending;
        Check(launches == 0, "Application shutdown cancels an in-flight handoff before launch");
    }
    Console.WriteLine($"{checks} update lifecycle checks passed.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

internal static class TestErrorMode
{
    [DllImport("kernel32.dll")] private static extern uint GetErrorMode();
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    internal static void SuppressDialogs() => SetErrorMode(GetErrorMode() | 0x8003);
}
