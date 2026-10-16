using Dynex.Desktop;

internal static class StartupStorageChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "DYNEX-startup-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SettingsStore(directory);
            store.Save(new UserSettings());
            var settingsPath = Path.Combine(directory, "settings.json");
            using (var locked = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var diagnosed = false;
                try { store.Load(); }
                catch (IOException error)
                { diagnosed = error.Message.Contains("Einstellungen lesen") && error.Message.Contains(settingsPath) && error.InnerException is IOException; }
                check(diagnosed, "locked startup settings report operation and actual path without resetting the profile");
            }
            var previous = File.ReadAllBytes(settingsPath);
            Directory.CreateDirectory(settingsPath + ".tmp");
            var writeDiagnosed = false;
            try { store.Save(new UserSettings { DeviceName = "Must-not-overwrite" }); }
            catch (UnauthorizedAccessException error)
            { writeDiagnosed = error.Message.Contains("Lokale Daten speichern") && error.Message.Contains(settingsPath) && error.InnerException is UnauthorizedAccessException; }
            check(writeDiagnosed && File.ReadAllBytes(settingsPath).SequenceEqual(previous),
                "access-to-path-denied during atomic save names its target and preserves the original settings");

            var identity = store.LoadIdentity();
            var identityPath = Path.Combine(directory, "identity.dpapi");
            using (var locked = new FileStream(identityPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var diagnosed = false;
                try { store.LoadIdentity(); }
                catch (IOException error)
                { diagnosed = error.Message.Contains("Geräteidentität lesen") && error.Message.Contains(identityPath); }
                check(diagnosed, "unreadable startup identity names the exact protected file");
            }
            check(store.LoadIdentity() == identity, "startup read failure preserves pairing identity after the lock is released");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
