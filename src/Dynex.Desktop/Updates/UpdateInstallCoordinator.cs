namespace Dynex.Desktop.Updates;

/// <summary>Serializes ready notifications without launching an installer from a download thread.</summary>
internal sealed class UpdateInstallCoordinator(
    Func<bool> canInstall,
    Func<Task<string?>> readReadyInstaller,
    Func<string, Task<bool>> handoff,
    Action<string> reportFailure) : IDisposable
{
    private readonly object sync = new();
    private bool busy, completed, disposed;
    private string? failedInstaller;

    /// <summary>The installer restart must not relaunch the same cached version after an aborted update.</summary>
    public async Task SkipReadyInstallerAsync()
    {
        lock (sync)
        {
            if (busy || completed || disposed) return;
            busy = true;
        }
        try
        {
            var installer = await readReadyInstaller();
            lock (sync)
                if (!disposed && installer is not null) failedInstaller = installer;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { reportFailure(error.Message); }
        finally { lock (sync) busy = false; }
    }

    public async Task TryInstallAsync()
    {
        lock (sync)
        {
            if (busy || completed || disposed) return;
            busy = true;
        }
        string? installer = null;
        try
        {
            if (!canInstall()) return;
            installer = await readReadyInstaller();
            if (installer is null) return;
            lock (sync)
                if (disposed || installer == failedInstaller) return;
            // Settings can be edited or automatic updates disabled while the cached
            // installer is being hashed. Never act on the earlier permission snapshot.
            if (!canInstall()) return;
            var started = await handoff(installer);
            lock (sync)
            {
                if (started) completed = true;
                else failedInstaller = installer;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            lock (sync) failedInstaller = installer;
            reportFailure(error.Message);
        }
        finally { lock (sync) busy = false; }
    }

    // A failed launch retains its verified installer. Retry only on an explicit
    // user request, instead of looping on the failure's own Changed notification.
    public void AllowRetry() { lock (sync) failedInstaller = null; }
    public void Dispose() { lock (sync) disposed = true; }
}
