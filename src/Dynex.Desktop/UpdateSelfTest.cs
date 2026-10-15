using System.IO;

namespace Dynex.Desktop;

/// <summary>
/// Minimal start check for a freshly installed update. It must not create mutexes, workers,
/// network sockets or settings; it only proves the new binary runs and its payload is complete.
/// </summary>
internal static class UpdateSelfTest
{
    internal const int Healthy = 0, WrongVersion = 21, MissingPayload = 22, Failed = 23;

    internal static int Run(string? expectedVersion)
    {
        try
        {
            if (expectedVersion is not null && expectedVersion != DynexRuntime.AppVersion) return WrongVersion;
            var shaper = Path.Combine(AppContext.BaseDirectory, "shaper");
            if (!File.Exists(Path.Combine(shaper, "Dynex.Shaper.exe")) || !File.Exists(Path.Combine(shaper, "Dynex.Shaper.dll")))
                return MissingPayload;
            // Settings written by the previous version must still load.
            new SettingsStore().Load();
            return Healthy;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Failed; }
    }
}
