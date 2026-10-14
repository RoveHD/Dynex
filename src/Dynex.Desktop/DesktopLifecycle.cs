using System.IO;
using System.IO.MemoryMappedFiles;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dynex.Desktop;

internal static class DesktopLifecycle
{
    private static Mutex? mutex;
    private static EventWaitHandle? stopEvent, showEvent;
    private static RegisteredWaitHandle? stopWait, showWait;
    private static bool ownsMutex;
    private static MemoryMappedFile? processMap;
    public static int ExitCode { get; private set; }
    public static string? ProfilePath { get; private set; }
    private static string? verifyLiveOutputPath;
    private static string? verifyLiveArgumentError;
    public static Task<bool> InitializeAsync(string[] args)
    {
        // A silent update owns this gate from before the parent exits until all
        // files are replaced. Do not start a second runtime during that handoff.
        if (UpdateBlocksStart(args)) return Task.FromResult(false);
        var verifyIndex = Array.IndexOf(args, "--verify-live");
        if (verifyIndex >= 0)
        {
            if (verifyIndex + 1 >= args.Length)
                verifyLiveArgumentError = "Verwendung: DYNEX.exe --verify-live <absoluter Ausgabeordner>";
            else if (!Path.IsPathFullyQualified(args[verifyIndex + 1]))
                verifyLiveArgumentError = "Der Ausgabeordner für --verify-live muss absolut angegeben werden.";
            else
                verifyLiveOutputPath = Path.GetFullPath(args[verifyIndex + 1]);
        }
        if (verifyIndex < 0 && args.Contains("--smoke-test"))
        {
            ProfilePath = Path.Combine(Path.GetTempPath(), "DYNEX-smoke-" + Guid.NewGuid().ToString("N"));
            return Task.FromResult(true);
        }
        var suffix = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var testIndex = Array.IndexOf(args, "--test-instance");
        if (testIndex >= 0)
        {
            if (testIndex + 1 >= args.Length || !Guid.TryParse(args[testIndex + 1], out var id)) throw new ArgumentException("Testinstanz benötigt eine GUID.");
            suffix += "." + id.ToString("N");
            ProfilePath = Path.Combine(Path.GetTempPath(), "DYNEX-instance-" + id.ToString("N"));
        }
        var name = @"Local\DYNEX.Instance." + suffix;
        mutex = new Mutex(true, name, out var created);
        ownsMutex = created;
        if (args.Contains("--update-idle"))
        {
            // Unlike --shutdown, the background installer must never terminate
            // an unrelated running instance. Its update gate prevents new ones.
            ExitCode = created ? 0 : 2;
            Cleanup();
            return Task.FromResult(false);
        }
        if (args.Contains("--shutdown"))
        {
            if (!created)
            {
                try
                {
                    using var map = MemoryMappedFile.OpenExisting(@"Local\DYNEX.Process." + suffix, MemoryMappedFileRights.Read);
                    using var data = map.CreateViewAccessor(0, 16, MemoryMappedFileAccess.Read);
                    using var running = Process.GetProcessById(data.ReadInt32(0));
                    if (running.StartTime.ToUniversalTime().Ticks != data.ReadInt64(8)) throw new InvalidOperationException("Instanzidentität stimmt nicht überein.");
                    using var signal = EventWaitHandle.OpenExisting(@"Local\DYNEX.Stop." + suffix);
                    signal.Set();
                    // Wait for the real process exit, not merely a closing window or released mutex.
                    if (!running.WaitForExit(16000)) ExitCode = 2;
                }
                catch (ArgumentException) { /* Process already exited after reading its identity. */ }
                catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or IOException or InvalidOperationException) { ExitCode = 3; }
            }
            Cleanup();
            return Task.FromResult(false);
        }
        if (!created)
        {
            try { using var signal = EventWaitHandle.OpenExisting(@"Local\DYNEX.Show." + suffix); signal.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            mutex.Dispose(); mutex = null;
            return Task.FromResult(false);
        }
        // Close the check/create race: if setup acquired its gate after our
        // first check, relinquish the instance before any worker can start.
        if (UpdateBlocksStart(args))
        {
            Cleanup();
            return Task.FromResult(false);
        }
        stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DYNEX.Stop." + suffix);
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DYNEX.Show." + suffix);
        processMap = MemoryMappedFile.CreateNew(@"Local\DYNEX.Process." + suffix, 16);
        using (var data = processMap.CreateViewAccessor())
        using (var current = Process.GetCurrentProcess())
        { data.Write(0, Environment.ProcessId); data.Write(8, current.StartTime.ToUniversalTime().Ticks); }
        return Task.FromResult(true);
    }
    private static bool UpdateBlocksStart(string[] args)
    {
        if (args.Contains("--shutdown") || args.Contains("--update-idle") ||
            args.Contains("--smoke-test") || args.Contains("--test-instance")) return false;
        if (!Mutex.TryOpenExisting(@"Local\DYNEX.Update", out var gate)) return false;
        gate.Dispose();
        return true;
    }
    public static async Task AfterWindowAsync(MainWindow window, DynexRuntime runtime, string[] args)
    {
        if (stopEvent != null) stopWait = ThreadPool.RegisterWaitForSingleObject(stopEvent, (_, _) => window.Dispatcher.BeginInvoke(async () => await window.RequestExitAsync()), null, Timeout.Infinite, false);
        if (showEvent != null) showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => window.Dispatcher.BeginInvoke(() => { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }), null, Timeout.Infinite, false);
        if (args.Contains("--tray") && verifyLiveOutputPath is null && verifyLiveArgumentError is null) window.Hide();
        if (verifyLiveOutputPath is not null || verifyLiveArgumentError is not null)
        {
            await VerifyLiveAsync(window, runtime);
            return;
        }
        var index = Array.IndexOf(args, "--smoke-test");
        if (index < 0) return;
        var directory = index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : Path.Combine(AppContext.BaseDirectory, "smoke-results");
        Directory.CreateDirectory(directory);
        try
        {
            // Live isolated profile: no fabricated telemetry, no UAC, no startup or production settings changes.
            var timeout = DateTime.UtcNow.AddSeconds(8);
            while (((runtime.Snapshot?.History.Count ?? 0) < 3 || runtime.Snapshot!.History.All(p => !p.RouterMs.HasValue && !p.ExternalMs.HasValue)) && DateTime.UtcNow < timeout) await Task.Delay(100);
            if ((runtime.Snapshot?.History.Count ?? 0) < 3 || runtime.Snapshot!.History.All(p => !p.RouterMs.HasValue && !p.ExternalMs.HasValue))
                throw new InvalidOperationException("Keine drei Live-Messungen mit mindestens einem Ping-Wert im Smoke-Test angekommen.");
            VerifyHistoryHover();
            window.Width = 1440; window.Height = 940; window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var history = runtime.Snapshot!.History.TakeLast(90).ToArray();
            if (history.Length == 0) throw new InvalidOperationException("Live-Verlauf fehlt für den visuellen Hover-Test.");
            var measuredIndex = Array.FindIndex(history, point => point.RouterMs.HasValue || point.ExternalMs.HasValue);
            if (measuredIndex < 0) throw new InvalidOperationException("Kein tatsächlicher Ping-Messpunkt für den Hover-Render vorhanden.");
            var hoverRatio = history.Length <= 1 ? 0 : (double)measuredIndex / (history.Length - 1);
            window.SetHistoryHoverAt(hoverRatio);
            var expectedHover = MainWindow.FormatHistoryHover(history[MainWindow.HistoryIndexForPointer(hoverRatio, history.Length)]);
            var hoverText = (System.Windows.Controls.TextBlock)window.FindName("HistoryHoverText");
            var hoverCanvas = (System.Windows.Controls.Canvas)window.FindName("HistoryHoverCanvas");
            if (hoverText.Text != expectedHover || hoverCanvas.Children.Count < 2)
                throw new InvalidOperationException("Der sichtbare Ping-Hover zeigt nicht den ausgewählten Live-Messpunkt, Cursor und Messwertmarker.");
            var chartPanel = (FrameworkElement)window.FindName("ChartPanel");
            chartPanel.UpdateLayout(); window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            Capture(chartPanel, Path.Combine(directory, "ping-hover-live.png"));
            window.ClearHistoryHover();
            Capture(window, Path.Combine(directory, "dashboard-1440.png"));
            window.WindowState = WindowState.Normal;
            foreach (var width in new[] { 900, 1240, 1920, 2560 })
            {
                window.Width = width;
                window.Height = Math.Min(940, Math.Max(650, SystemParameters.WorkArea.Height - 48));
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (Math.Abs(window.ActualWidth - width) > 2) throw new InvalidOperationException($"WPF-Fensterbreite {width}px konnte nicht gerendert werden (Ist {window.ActualWidth:0}px).");
                Capture(window, Path.Combine(directory, $"responsive-{width}.png"));
            }
            window.WindowState = WindowState.Maximized;
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (window.ActualHeight > SystemParameters.WorkArea.Height + 2)
                throw new InvalidOperationException("Maximiertes Fenster überschreitet die Windows-Arbeitsfläche.");
            Capture(window, Path.Combine(directory, "dashboard-maximized-workarea.png"));
            window.WindowState = WindowState.Normal;
            window.Width = 1000; window.Height = 720; window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Capture(window, Path.Combine(directory, "dashboard-1000.png"));
            var scroll = FindScroll(window);
            scroll?.ScrollToBottom(); window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Capture(window, Path.Combine(directory, "settings-1000.png"));
            var diagnostic = await runtime.ExportDiagnosticsAsync();
            File.Copy(diagnostic, Path.Combine(directory, "live-diagnostics.json"), true);
            await VerifyUpdateCheckboxAsync(window, runtime);
            await VerifyRuntimeAsync(runtime);
            await VerifyPeerTrafficAsync(window, runtime, directory);
            await VerifyProcessPickerAsync(window, runtime, directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: live snapshot; WPF render at 900/1000/1240/1440/1920/2560 and maximized work area; live ping-hover screenshot with actual selected timestamp/values/cursor; normalized pointer mapping, bounds clamp, resize-stable sample selection, and distinct missing/zero values; settings validation/persistence; DPAPI pairing roundtrip; diagnostics; peer traffic over encrypted loopback at 900/1240/1440/1920/2560; disconnect expiry; clean shutdown. Peer traffic screenshots use labeled synthetic measurements. No driver or physical two-PC test.\n");
        }
        catch (Exception ex)
        {
            ExitCode = 1;
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), ex.ToString());
        }
        finally { await window.RequestExitAsync(); }
    }
    private static async Task VerifyUpdateCheckboxAsync(MainWindow window, DynexRuntime runtime)
    {
        var checkbox = (System.Windows.Controls.CheckBox)window.FindName("AutoUpdateCheck");
        if (checkbox.IsChecked == true || runtime.Settings.AutoUpdate)
            throw new InvalidOperationException("Automatische Updates müssen im neuen Profil ausgeschaltet sein.");
        foreach (var enabled in new[] { true, false })
        {
            checkbox.IsChecked = enabled;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!checkbox.IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(20);
            if (!checkbox.IsEnabled || runtime.Settings.AutoUpdate != enabled ||
                new SettingsStore(ProfilePath).Load().AutoUpdate != enabled)
                throw new InvalidOperationException("Die Update-Checkbox speichert nicht sofort.");
        }
        if (((System.Windows.Controls.Button)window.FindName("CheckUpdatesButton")).IsEnabled)
            throw new InvalidOperationException("Isolierter Smoke-Test darf keine Updateverbindung öffnen.");
    }
    private static async Task VerifyLiveAsync(MainWindow window, DynexRuntime runtime)
    {
        var directory = verifyLiveOutputPath ?? Path.Combine(Path.GetTempPath(), "DYNEX-verify-live-" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(directory);
            if (verifyLiveArgumentError is not null) throw new ArgumentException(verifyLiveArgumentError);

            // Count only fresh monitoring snapshots, not a normal-start capacity-test status update.
            var sync = new object();
            var snapshots = 0;
            RuntimeSnapshot? latest = null;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnUpdated(RuntimeSnapshot snapshot)
            {
                if (runtime.IsSpeedTestRunning || snapshot.State == "Speedtest beendet") return;
                lock (sync)
                {
                    latest = snapshot;
                    snapshots++;
                    if (snapshots >= 3) ready.TrySetResult();
                }
            }

            runtime.Updated += OnUpdated;
            try
            {
                // A first-run normal profile may perform its existing automatic capacity test (up to 10 s).
                // The overall bound accommodates that existing startup path plus fresh two-second samples.
                if (!ready.Task.IsCompleted && await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(45))) != ready.Task)
                    throw new TimeoutException("Innerhalb von 45 Sekunden sind keine drei frischen Runtime-Snapshots eingetroffen.");
            }
            finally { runtime.Updated -= OnUpdated; }

            RuntimeSnapshot snapshot;
            int snapshotCount;
            lock (sync) { snapshot = latest ?? throw new InvalidOperationException("Runtime-Snapshot fehlt."); snapshotCount = snapshots; }
            var diagnostic = await runtime.ExportDiagnosticsAsync();
            File.Copy(diagnostic, Path.Combine(directory, "live-diagnostics.json"), true);
            // Runtime_Updated queues the UI binding update. Let it and the WPF
            // layout pass finish before rendering generated rows and polylines.
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Capture(window, Path.Combine(directory, "dashboard-live.png"));
            var result = $"PASS: {snapshotCount} frische Runtime-Snapshots.\n" +
                $"Zeit (UTC): {DateTimeOffset.UtcNow:O}\n" +
                $"Fenstergröße (WPF-Einheiten, Export mit 96 DPI): {(int)Math.Ceiling(window.ActualWidth)} × {(int)Math.Ceiling(window.ActualHeight)}\n" +
                $"Status: {snapshot.State}\nDetails: {snapshot.Detail}\n" +
                $"Gaming: {snapshot.IsGaming} ({snapshot.Game})\n" +
                $"Treiber aktiv/verfügbar: {snapshot.DriverActive}/{snapshot.DriverAvailable}\nTreiberstatus: {snapshot.DriverState}\n" +
                $"Priorisierung aktiv: {snapshot.QosActive}; Spiel-/Sprachpakete: {snapshot.GamePriorityPackets}/{snapshot.VoicePriorityPackets}\n" +
                $"Downloadlimit: {snapshot.LimitMbps?.ToString("0.##") ?? "unbekannt"} Mbit/s\n" +
                $"Gemeinsames Downloadbudget: {snapshot.SharedDownloadBudgetMbps?.ToString("0.##") ?? "unbekannt"} Mbit/s für {snapshot.ParticipantCount} PCs; vollständig abgestimmt: {snapshot.SharedBudgetComplete}; Anteile bestätigt: {snapshot.ConfirmedParticipantCount}/{snapshot.ParticipantCount}\n" +
                $"Download/Upload: {snapshot.DownloadMbps?.ToString("0.##") ?? "unbekannt"}/{snapshot.UploadMbps?.ToString("0.##") ?? "unbekannt"} Mbit/s\n" +
                $"Router-Ping: {snapshot.Router.Current?.ToString("0.##") ?? "unbekannt"} ms; Internet-Ping: {snapshot.External.Current?.ToString("0.##") ?? "unbekannt"} ms\n" +
                $"Geräte: {snapshot.Devices.Count}; Verlaufspunkte: {snapshot.History.Count}\n" +
                $"Netzwerk: {snapshot.NetworkStatus}\n";
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExitCode = 1;
            try
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "error.txt"), ex.ToString());
                await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "FAIL: Live-Diagnose fehlgeschlagen. Siehe error.txt. Die normale Anwendung bleibt geöffnet.\n");
            }
            catch { /* A destination I/O failure must not close the normally running application. */ }
        }
    }
    private static async Task VerifyRuntimeAsync(DynexRuntime runtime)
    {
        try { await runtime.SaveSettingsAsync(runtime.Settings with { CapacityMbps = double.NaN }); throw new Exception("NaN akzeptiert."); }
        catch (ArgumentException) { }
        try { await runtime.SaveSettingsAsync(runtime.Settings with { DirectPeers = "8.8.8.8" }); throw new Exception("Öffentliche Peer-Adresse akzeptiert."); }
        catch (ArgumentException) { }
        var code = runtime.ExportPairingCode();
        await runtime.ImportPairingCodeAsync(code);
        if (code != runtime.ExportPairingCode()) throw new Exception("Gruppencode verändert.");
        if (runtime.Settings.AutoConnectLan) throw new Exception("Manuelle Gruppe aktiviert den privaten Gruppenmodus nicht.");
        await runtime.SaveSettingsAsync(runtime.Settings with { CapacityMbps = 150 });
        if (new SettingsStore(ProfilePath).Load().CapacityMbps != 150) throw new Exception("Einstellungen nicht gespeichert.");
        if (new SettingsStore(ProfilePath).LoadIdentity().GroupKey != code[7..]) throw new Exception("DPAPI-Roundtrip fehlgeschlagen.");
        var signedId = Dynex.Core.AutoLanIdentity.GetDeviceId(new SettingsStore(ProfilePath).LoadIdentity().SigningPrivateKeyBase64!);
        if (signedId == Guid.Empty) throw new Exception("AutoLAN-Geräteidentität fehlt.");
        await runtime.SaveSettingsAsync(runtime.Settings with { DeviceName = "changed", BackgroundPercent = 95, ReservePerGamerMbps = 0, Adaptive = false, AutoConnectLan = true });
        if (runtime.Settings.DeviceName != Environment.MachineName || runtime.Settings.BackgroundPercent != 65 || !runtime.Settings.Adaptive || runtime.Settings.ReservePerGamerMbps <= 0)
            throw new Exception("Windows-PC-Name und automatische Richtlinie wurden überschrieben.");
        if (Dynex.Core.AutoLanIdentity.GetDeviceId(new SettingsStore(ProfilePath).LoadIdentity().SigningPrivateKeyBase64!) != signedId)
            throw new Exception("AutoLAN-Schlüssel blieb nicht erhalten.");
        var migrationStore = new SettingsStore(Path.Combine(ProfilePath!, "legacy-identity"));
        var legacyIdentity = new SettingsStore.Identity(Guid.NewGuid(), code[7..]);
        migrationStore.SaveIdentity(legacyIdentity);
        var migrated = migrationStore.LoadIdentity();
        if (migrated.DeviceId != legacyIdentity.DeviceId || migrated.GroupKey != legacyIdentity.GroupKey ||
            migrated.SigningPrivateKeyBase64 is null || migrated.SigningPrivateKeyBase64 != migrationStore.LoadIdentity().SigningPrivateKeyBase64)
            throw new Exception("Migration der vorhandenen Geräteidentität fehlgeschlagen.");
        var previousHistoryCount = runtime.Snapshot!.History.Count;
        var previousSample = runtime.Snapshot.History[^1].Time;
        await runtime.SaveSettingsAsync(runtime.Settings with { Mode = GamingMode.On });
        var updateDeadline = DateTime.UtcNow.AddSeconds(5);
        while (runtime.Snapshot!.History[^1].Time <= previousSample && DateTime.UtcNow < updateDeadline) await Task.Delay(100);
        if (runtime.Snapshot.History.Count <= previousHistoryCount) throw new Exception("Gaming-Umschaltung hat den Vergleichsverlauf verworfen.");
        using (var lockedIdentity = new FileStream(Path.Combine(ProfilePath!, "identity.dpapi"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { await runtime.ImportPairingCodeAsync("DYNEX1:" + Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))); throw new Exception("Dateisperre beim Pairing nicht erkannt."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (runtime.ExportPairingCode() != code) throw new Exception("Fehlgeschlagenes Pairing änderte den laufenden Schlüssel.");
        }
        var view = NetworkMonitor.View(new double?[] { 10, null, 20, 24 }, "test");
        if (view.Current != 24 || view.Loss != 25 || view.Jitter != 4) throw new Exception("Ping/Jitter/Verlust-Berechnung fehlerhaft.");
    }
    private static void VerifyHistoryHover()
    {
        var time = new DateTimeOffset(2026, 10, 14, 12, 34, 56, TimeSpan.Zero);
        var points = new[]
        {
            new HistoryPoint(time.AddSeconds(-2), 12, 15, null, false),
            new HistoryPoint(time, null, 0, null, false),
            new HistoryPoint(time.AddSeconds(2), 20, 24, null, true)
        };
        var midpointIndex = MainWindow.HistoryIndexForPointer(.5, points.Length);
        if (midpointIndex != 1 || MainWindow.HistoryIndexForPointer(.5, points.Length) != midpointIndex ||
            MainWindow.HistoryIndexForPointer(-1, points.Length) != 0 || MainWindow.HistoryIndexForPointer(2, points.Length) != 2)
            throw new InvalidOperationException("Ping-Hover ordnet Zeitpunkte bei Resize oder am Rand falsch zu.");
        var hover = MainWindow.FormatHistoryHover(points[midpointIndex]);
        if (!hover.Contains(time.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")) || !hover.Contains("Router keine Messung") || !hover.Contains($"Internet {0d:0.0} ms"))
            throw new InvalidOperationException("Ping-Hover unterscheidet fehlende Werte nicht korrekt von 0 ms oder zeigt keinen Zeitstempel.");
    }
    private static async Task VerifyPeerTrafficAsync(MainWindow window, DynexRuntime runtime, string directory)
    {
        // Real encrypted transport and bindings, synthetic rates. Only the isolated smoke profile changes.
        await runtime.SaveSettingsAsync(runtime.Settings with { AutoConnectLan = false });
        int port;
        using (var reservation = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
            port = ((System.Net.IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        const string peerName = "TEST-PC · simulierte Messwerte";
        await using var peer = new Dynex.Core.PeerService(new()
        {
            DeviceName = peerName, GroupKeyBase64 = runtime.ExportPairingCode()[7..],
            LoopbackOnly = true, Port = port,
            DirectPeers = [new(System.Net.IPAddress.Loopback, runtime.PeerPort)]
        });
        peer.Publish(false, 120.5, appliedLimitMbps: 42, shaperActive: true, uploadMbps: 12.25);
        await peer.StartAsync();
        async Task WaitFor(Func<bool> ready, int seconds = 8)
        {
            var watch = Stopwatch.StartNew();
            while (!ready())
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) throw new InvalidOperationException("Peer-Durchsatz im Smoke-Test nicht angekommen/abgelaufen.");
                await Task.Delay(50);
            }
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
        }
        DeviceView? Remote() => runtime.Snapshot?.Devices.FirstOrDefault(d => d.Name == peerName);
        await WaitFor(() => Remote() is { DownloadMbps: 120.5, UploadMbps: 12.25, LimitMbps: 42 });
        await WaitFor(() => runtime.Snapshot is { SharedDownloadBudgetMbps: 90, ParticipantCount: 2, GamingCount: 1,
            SharedCapacityMbps: 150, SharedBudgetComplete: true, ConfirmedParticipantCount: 1 });
        var budgetLabel = (System.Windows.Controls.TextBlock)window.FindName("SharedBudgetText");
        var coordinationLabel = (System.Windows.Controls.TextBlock)window.FindName("CoordinationText");
        if (!budgetLabel.Text.Contains($"{90:0.0} Mbit/s für 2 PCs") || !coordinationLabel.Text.Contains("1/2 PCs bestätigt"))
            throw new Exception("Gemeinsames Budget oder bestätigte Filteranzahl fehlen in der Oberfläche.");
        var grid = (System.Windows.Controls.DataGrid)window.FindName("DeviceGrid");
        string Cell(int column)
        {
            var row = grid.Items.OfType<DeviceView>().Single(d => d.Name == peerName);
            return (grid.Columns[column].GetCellContent(row) as System.Windows.Controls.TextBlock)?.Text ?? throw new Exception("Durchsatzzelle fehlt.");
        }
        foreach (var width in new[] { 900, 1240, 1440, 1920, 2560 })
        {
            window.Width = width; window.Height = 940;
            FindScroll(window)?.ScrollToTop(); window.UpdateLayout();
            grid.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            if (grid.Columns.Sum(c => c.ActualWidth) > grid.ActualWidth + 1)
                throw new Exception("Gerätetabelle ist breiter als das Fenster.");
            if (Cell(3) != $"{120.5:0.0} Mbps" || Cell(4) != $"{12.25:0.0} Mbps" || Cell(5) != $"{42:0.0} Mbps")
                throw new Exception("Download, Upload und Limit sind nicht korrekt gebunden.");
            Capture(window, Path.Combine(directory, $"peer-traffic-fixture-{width}.png"));
        }
        var diagnostic = await runtime.ExportDiagnosticsAsync();
        using (var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(diagnostic)))
        {
            var remote = json.RootElement.GetProperty("Snapshot").GetProperty("Devices").EnumerateArray().Single(d => d.GetProperty("Name").GetString() == peerName);
            if (remote.GetProperty("DownloadMbps").GetDouble() != 120.5 || remote.GetProperty("UploadMbps").GetDouble() != 12.25)
                throw new Exception("Diagnoseexport verliert Peer-Durchsatz.");
        }
        peer.Publish(false, 120.5, appliedLimitMbps: 99, shaperActive: true, uploadMbps: 12.25);
        await WaitFor(() => Remote() is { LimitMbps: 99 } && runtime.Snapshot is { ConfirmedParticipantCount: 0 });
        if (!coordinationLabel.Text.Contains("0/2 PCs bestätigt"))
            throw new Exception("Ein altes zu hohes Limit wird als neues gemeinsames Budget bestätigt.");
        peer.Publish(false, 0, uploadMbps: 0);
        await WaitFor(() => Remote() is { DownloadMbps: 0, UploadMbps: 0, LimitMbps: null });
        if (Cell(3) != $"{0:0.0} Mbps" || Cell(4) != $"{0:0.0} Mbps" || Cell(5) != "—") throw new Exception("Gemessener Stillstand erscheint nicht als Null.");
        peer.Publish(false);
        await WaitFor(() => Remote() is { DownloadMbps: null, UploadMbps: null });
        if (Cell(3) != "—" || Cell(4) != "—") throw new Exception("Fehlende Messwerte erscheinen nicht als unbekannt.");
        await peer.StopAsync();
        await WaitFor(() => Remote() is null, 14);
    }
    private static async Task VerifyProcessPickerAsync(MainWindow window, DynexRuntime runtime, string directory)
    {
        var picker = (System.Windows.Controls.ComboBox)window.FindName("RunningProcessesBox");
        var refresh = (System.Windows.Controls.Button)window.FindName("RefreshProcessesButton");
        var add = (System.Windows.Controls.Button)window.FindName("AddProcessButton");
        var namesBox = (System.Windows.Controls.TextBox)window.FindName("ProcessesBox");
        var advanced = (System.Windows.Controls.Expander)window.FindName("AdvancedExpander");
        const string original = "cs2.EXE; Existing-Game";
        namesBox.Text = original;
        advanced.IsExpanded = true;
        foreach (var width in new[] { 900, 1440 })
        {
            window.Width = width; window.Height = 940; window.UpdateLayout();
            FindScroll(window)?.ScrollToBottom(); window.UpdateLayout();
            picker.IsDropDownOpen = true; // Exercise the real open -> refresh event, not only the helper.
            var deadline = Stopwatch.StartNew();
            while (!refresh.IsEnabled)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(8)) throw new Exception("Prozessauswahl lädt nicht fertig.");
                await Task.Delay(25);
            }
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            if (namesBox.Text != original || !picker.Items.Cast<string>().SequenceEqual(Games.GameDetector.FilterSelectableProcessNames(picker.Items.Cast<string>())))
                throw new Exception("Prozessauswahl ändert vorhandene Eingaben oder liefert ungültige Namen.");
            if (add.IsEnabled) throw new Exception("Hinzufügen ist ohne Auswahl aktiv.");
            Capture(window, Path.Combine(directory, $"process-picker-{width}.png"));
            var popup = (System.Windows.Controls.Primitives.Popup)picker.Template.FindName("PART_Popup", picker);
            if (!popup.IsOpen || popup.Child is not FrameworkElement popupContent) throw new Exception("Prozess-Dropdown öffnet nicht.");
            Capture(popupContent, Path.Combine(directory, $"process-picker-list-{width}.png"));
            picker.IsDropDownOpen = false;
        }
        picker.ItemsSource = new[] { "cs2", "DynexPickerTest" }; // Deterministic selection fixture; no process is started.
        picker.SelectedIndex = 0;
        add.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (namesBox.Text != original) throw new Exception("Auswahl legt .exe-/Großschreibungsduplikat an.");
        picker.SelectedIndex = 1;
        if (!add.IsEnabled) throw new Exception("Hinzufügen bleibt trotz Auswahl gesperrt.");
        add.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var expected = original + "; DynexPickerTest";
        if (namesBox.Text != expected) throw new Exception("Prozessauswahl überschreibt vorhandene Namen.");
        await runtime.SaveSettingsAsync(runtime.Settings with { GameProcesses = namesBox.Text });
        if (new SettingsStore(ProfilePath).Load().GameProcesses != expected) throw new Exception("Ausgewählte Prozessnamen werden nicht gespeichert.");
        await File.WriteAllTextAsync(Path.Combine(directory, "process-picker-result.txt"),
            "PASS: live process dropdown refresh on open; 900/1440 rendering; no selection disabled; duplicate protection; append preserves manual text; isolated settings persistence. No game process launched.\n");
    }
    private static void Capture(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static System.Windows.Controls.ScrollViewer? FindScroll(DependencyObject element)
    {
        if (element is System.Windows.Controls.ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindScroll(VisualTreeHelper.GetChild(element, i)) is { } found) return found;
        return null;
    }
    public static void Cleanup()
    {
        stopWait?.Unregister(null); showWait?.Unregister(null);
        stopEvent?.Dispose(); showEvent?.Dispose();
        processMap?.Dispose(); processMap = null;
        if (ownsMutex) { mutex?.ReleaseMutex(); ownsMutex = false; }
        mutex?.Dispose(); mutex = null;
        // Smoke artifacts are intentionally preserved in their explicitly named test directory.
    }
}
