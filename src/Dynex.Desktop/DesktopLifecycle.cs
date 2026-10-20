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
    internal static void ReportStartupFailure(string[] args, Exception error, string step)
    {
        var report = $"FAIL: DYNEX-Teststart · {step}\n{error}";
        try { Console.Error.WriteLine(report); } catch (IOException) { }
        var index = Array.FindIndex(args, arg => arg is "--smoke-test" or "--verify-live");
        if (index < 0 || index + 1 >= args.Length || !Path.IsPathFullyQualified(args[index + 1])) return;
        try
        {
            Directory.CreateDirectory(args[index + 1]);
            File.WriteAllText(Path.Combine(args[index + 1], "result.txt"), report);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
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
            // Explicit capacity fixture for isolated layout/coordination tests.
            // No bandwidth test runs here; real measurement gating is covered by the runtime suite.
            var smokeStore = new SettingsStore(ProfilePath);
            var smokeSettings = smokeStore.Load() with
            {
                CapacityMeasuredAtUtc = DateTimeOffset.UnixEpoch,
                AutomaticSpeedTestAttempted = true
            };
            if (args.Contains("--features-only")) smokeSettings = smokeSettings with { DriverEnabled = false };
            smokeStore.Save(smokeSettings);
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
        mutex = StartupObjects.CreateInstance(name);
        var created = mutex is not null;
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
                catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or IOException or InvalidOperationException or UnauthorizedAccessException) { ExitCode = 3; }
            }
            Cleanup();
            return Task.FromResult(false);
        }
        if (!created)
        {
            try { using var signal = EventWaitHandle.OpenExisting(@"Local\DYNEX.Show." + suffix); signal.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException("DYNEX läuft bereits mit anderen Windows-Berechtigungen. Beende die bestehende DYNEX-Instanz über ihr Tray-Symbol und starte DYNEX anschließend ohne Administratorrechte.");
            }
            mutex?.Dispose(); mutex = null;
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
        return StartupObjects.MutexExists(@"Local\DYNEX.Update");
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
            if (args.Contains("--features-only"))
            {
                await FeatureUiSmokeChecks.RunAsync(window, runtime, directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: isolated Feature-UI-Smoke; rendered device manager, connection test, network profiles and usage history. No limiter UI, driver or production data.\n");
                return;
            }
            if (args.Contains("--network-only"))
            {
                await NetworkUiSmokeChecks.RunAsync(window, runtime, directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: focused network UI smoke; no physical two-PC test.\n");
                return;
            }
            if (args.Contains("--process-picker-only"))
            {
                await VerifyProcessPickerAsync(window, runtime, directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: focused process-picker UI smoke, isolated settings, search, add/remove, save/reload and 900/1440 rendering. Other UI sections were not tested in this run.\n");
                return;
            }
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
            window.ShowSettings("general"); window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Capture(window, Path.Combine(directory, "settings-1000.png"));
            await VerifyPagesAsync(window, runtime, directory);
            await VerifyFeedbackPreferencesAsync(window, runtime);
            VerifyDevicePaging(window, runtime);
            await VerifyNetworkHelpAsync(window, runtime, directory);
            await VerifyStatusRefreshAsync(window, runtime, directory);
            VerifyProtectionIdle(window, runtime, directory);
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
        window.ShowSettings("updates"); window.UpdateLayout();
        if (((System.Windows.Controls.TextBlock)window.FindName("InstalledVersionText")).Text != $"Installierte Version: {DynexRuntime.AppVersion}")
            throw new InvalidOperationException("Die Update-Seite zeigt nicht die tatsächlich laufende Version.");
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
    private static async Task VerifyPagesAsync(MainWindow window, DynexRuntime runtime, string directory)
    {
        var originalSettings = runtime.Settings;
        var configuredProcesses = (System.Windows.Controls.ListBox)window.FindName("ConfiguredProcessesList");
        var configuredNames = (System.Collections.ObjectModel.ObservableCollection<string>)configuredProcesses.ItemsSource;
        var originalNames = configuredNames.ToArray();
        configuredNames.Add("Pending-Unsaved-Game");
        var sizes = new[] { (900, 650), (1000, 720), (1366, 768), (1440, 940), (1920, 650), (1920, 1080), (2560, 1440) };
        var sections = new Dictionary<string, string[]>
        {
            ["general"] = ["CapacityBox", "SpeedTestButton", "AutoStartCheck", "DriverCheck", "SetupHint", "CapacityMeasurementText"],
            ["games"] = ["ModeCombo", "EarlyGamesBox", "ProcessSearchBox", "RunningProcessesBox", "AddProcessButton", "ConfiguredProcessesList"],
            ["network"] = ["AutoConnectCheck", "PairingBox", "PeersBox", "RouterBox", "ExternalBox", "NetworkHelpText", "WindowsNetworkButton"],
            ["updates"] = ["InstalledVersionText", "AutoUpdateCheck", "UpdateStatusText", "CheckUpdatesButton"],
            ["appearance"] = ["UiSoundsCheck", "UiAnimationsCheck"]
        };
        foreach (var (width, height) in sizes)
        {
            window.Width = width; window.Height = height;
            window.ShowOverview(); window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            foreach (var removedControl in new[] { "ManualLimitBox", "ManualUploadBox", "UploadLimitCheck", "ProfileCombo", "PriorityCombo", "ApplicationRulesButton", "ScheduleSettingsSection" })
                if (window.FindName(removedControl) is not null)
                    throw new Exception($"Entfernte Begrenzungsoberfläche ist noch erreichbar: {removedControl}.");
            if (((System.Windows.Controls.DataGrid)window.FindName("DeviceGrid")).Columns.Any(c => string.Equals(c.Header?.ToString(), "LIMIT", StringComparison.OrdinalIgnoreCase)) ||
                ((System.Windows.Controls.DataGrid)window.FindName("OverviewDeviceGrid")).Columns.Any(c => string.Equals(c.Header?.ToString(), "LIMIT", StringComparison.OrdinalIgnoreCase)))
                throw new Exception("Geräteübersicht enthält noch eine Limitspalte.");
            AssertPageFits(window, "OverviewPage", ["DownText", "UpText", "PriorityOverviewText", "DriverText", "HistoryPlot", "RouterPing", "ExternalPing", "OverviewDeviceGrid", "OverviewDeviceCount"]);
            // The compact dashboard intentionally replaces this extra summary with
            // the always-visible device table; check the summary when it is shown.
            if (((FrameworkElement)window.FindName("DashboardHomePanel")).IsVisible)
            {
                AssertVisibleControl(window, "OverviewPage", "DashboardPeersText");
                AssertVisibleControl(window, "OverviewPage", "DashboardDevicesButton");
            }
            foreach (var name in new[] { "RouterStats", "ExternalStats", "RouterTarget", "ExternalTarget" })
                AssertVisibleControl(window, "PingPanel", name);
            foreach (var name in new[] { "QosStatusText", "QosDetailText" })
                AssertVisibleControl(window, "OverviewPage", name);
            var chart = (FrameworkElement)window.FindName("ChartPanel");
            var plot = (FrameworkElement)window.FindName("HistoryPlot");
            var comparison = (FrameworkElement)window.FindName("HistoryCompare");
            var plotBounds = plot.TransformToAncestor(chart).TransformBounds(new Rect(plot.RenderSize));
            var comparisonTop = comparison.TransformToAncestor(chart).Transform(new System.Windows.Point()).Y;
            if (plotBounds.Bottom > comparisonTop + 1 ||
                System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(plot).Height < plot.ActualHeight - 1)
            {
                var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(plot);
                throw new Exception($"Der untere Rand des Pingdiagramms wird bei {width}x{height} abgeschnitten (plotBottom={plotBounds.Bottom:0.##}, comparisonTop={comparisonTop:0.##}, slotHeight={slot.Height:0.##}, actualHeight={plot.ActualHeight:0.##}, chartHeight={chart.ActualHeight:0.##}).");
            }
            if (!window.IsOverviewVisible || ((FrameworkElement)window.FindName("SettingsPage")).IsVisible)
                throw new Exception("Die Übersicht ist nicht sauber von den Einstellungen getrennt.");
            if (((FrameworkElement)window.FindName("HistoryPlot")).ActualHeight < 50)
                throw new Exception("Der Pingverlauf wird beim Verkleinern unlesbar.");
            Capture(window, Path.Combine(directory, $"overview-{width}x{height}.png"));
            window.ShowDevices(); window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            AssertPageFits(window, "DevicesPage", ["DeviceGrid", "DeviceCount", "NextDevicesButton", "DevicePriorityText"]);
            var devicesPage = (FrameworkElement)window.FindName("DevicesPage");
            var devicesHeader = (FrameworkElement)window.FindName("DevicesPageHeader");
            var devicesCard = (FrameworkElement)window.FindName("DevicesPageCard");
            var headerSlot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(devicesHeader);
            var cardSlot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(devicesCard);
            if (headerSlot.Bottom > cardSlot.Top + 1)
                throw new Exception($"Die Kopfzeile der Geräteansicht überlappt bei {width}x{height} den Inhalt.");
            if (((System.Windows.Controls.TextBlock)window.FindName("DevicePriorityText")).TextWrapping != System.Windows.TextWrapping.Wrap ||
                ((System.Windows.Controls.TextBlock)window.FindName("CoordinationText")).TextWrapping != System.Windows.TextWrapping.Wrap)
                throw new Exception("Die Gerätehinweise müssen bei schmalen Fenstern umbrechen können.");
            if (((FrameworkElement)window.FindName("OverviewPage")).IsVisible || ((FrameworkElement)window.FindName("SettingsPage")).IsVisible)
                throw new Exception("Die Geräteansicht überlagert eine andere Seite.");
            Capture(window, Path.Combine(directory, $"devices-{width}x{height}.png"));
            foreach (var (section, controls) in sections)
            {
                window.ShowSettings(section); window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Capture(window, Path.Combine(directory, $"settings-{section}-{width}x{height}.png"));
                AssertPageFits(window, "SettingsPage", controls);
                if (window.IsOverviewVisible || ((FrameworkElement)window.FindName("OverviewPage")).IsVisible)
                    throw new Exception("Beim Öffnen der Einstellungen bleibt die Übersicht sichtbar.");
                AssertVisibleControl(window, "SettingsPage", "SettingsSaveButton");
            }
        }
        if (!configuredNames.Contains("Pending-Unsaved-Game") || runtime.Settings != originalSettings)
            throw new Exception("Seitenwechsel verliert ungespeicherte Eingaben oder speichert ohne Aktion.");
        configuredNames.Clear();
        foreach (var name in originalNames) configuredNames.Add(name);
        await VerifyToolTipsAsync(window, directory);
        window.ShowOverview(); window.UpdateLayout();
        await File.WriteAllTextAsync(Path.Combine(directory, "pages-result.txt"),
            "PASS: overview, dedicated PC view and all five settings pages fit at 900x650, 1000x720, 1366x768, 1440x940, 1920x650, 1920x1080, 2560x1440; no page scrolling; controls reachable; limiting controls and schedule page are absent; navigation preserves unsaved fields and settings.\n");
    }
    private static async Task VerifyToolTipsAsync(MainWindow window, string directory)
    {
        window.ShowSettings("general");
        window.UpdateLayout();
        double shortHeight = 0;
        foreach (var name in new[] { "NotificationsCheck", "SpeedTestHint" })
        {
            var owner = (FrameworkElement)window.FindName(name);
            var tip = new System.Windows.Controls.ToolTip { Content = owner.ToolTip, PlacementTarget = owner };
            try
            {
                tip.IsOpen = true;
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                tip.UpdateLayout();
                var text = FindVisualChild<System.Windows.Controls.TextBlock>(tip);
                var border = FindVisualChild<System.Windows.Controls.Border>(tip);
                if (text is null || string.IsNullOrWhiteSpace(text.Text) || text.Text != owner.ToolTip as string ||
                    text.Foreground is not SolidColorBrush foreground || border?.Background is not SolidColorBrush background ||
                    foreground.Color.R - background.Color.R < 120 || tip.ActualWidth > 361 || tip.ActualWidth < 40)
                    throw new InvalidOperationException("Der Hinweistext ist nicht lesbar oder der Hinweisbalken ist zu breit.");
                if (name == "NotificationsCheck") shortHeight = tip.ActualHeight;
                else if (text.TextWrapping != TextWrapping.Wrap || tip.ActualHeight <= shortHeight)
                    throw new InvalidOperationException("Lange Hinweistexte werden nicht mehrzeilig angezeigt.");
                Capture(tip, Path.Combine(directory, $"tooltip-{name}.png"));
            }
            finally { tip.IsOpen = false; }
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "tooltips-result.txt"),
            "PASS: short and long settings tooltips render readable text on a dark background; width bounded; long text wraps.\n");
    }
    private static async Task VerifyFeedbackPreferencesAsync(MainWindow window, DynexRuntime runtime)
    {
        window.ShowSettings("appearance");
        var sound = (System.Windows.Controls.CheckBox)window.FindName("UiSoundsCheck");
        var motion = (System.Windows.Controls.CheckBox)window.FindName("UiAnimationsCheck");
        var original = runtime.Settings;
        sound.IsChecked = !original.UiSounds; motion.IsChecked = !original.UiAnimations;
        if (runtime.Settings != original) throw new Exception("Feedback-Vorschau speichert ohne Speichern-Klick.");
        window.ShowOverview();
        ((System.Windows.Controls.Button)window.FindName("SettingsNavButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (!((FrameworkElement)window.FindName("AppearanceSettingsSection")).IsVisible || sound.IsChecked != !original.UiSounds)
            throw new Exception("Der Zahnradknopf verliert den letzten Bereich oder ungespeicherte Einstellungen.");
        ((System.Windows.Controls.Button)window.FindName("SettingsSaveButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (runtime.Settings.UiSounds == original.UiSounds && DateTime.UtcNow < deadline) await Task.Delay(20);
        var saved = new SettingsStore(ProfilePath).Load();
        if (saved.UiSounds != !original.UiSounds || saved.UiAnimations != !original.UiAnimations)
            throw new Exception("Sounds und Animationen werden nicht dauerhaft gespeichert.");
        var feedback = (UiFeedback)typeof(MainWindow).GetField("_feedback", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        var page = (FrameworkElement)window.FindName("SettingsPage");
        var transform = page.RenderTransform;
        feedback.AnimationsEnabled = true;
        feedback.Transition(page); feedback.Transition(page);
        feedback.AnimationsEnabled = false;
        if (!ReferenceEquals(transform, page.RenderTransform) || page.Opacity != 1)
            throw new Exception("Schnelle Übergänge oder das Abschalten der Bewegung hinterlassen eine verschobene Ansicht.");
        sound.IsChecked = original.UiSounds; motion.IsChecked = original.UiAnimations;
        await runtime.SaveSettingsAsync(original);
        window.ShowOverview();
    }
    private static void AssertVisibleControl(MainWindow window, string pageName, string controlName)
    {
        var page = (FrameworkElement)window.FindName(pageName);
        var element = window.FindName(controlName) as FrameworkElement
            ?? throw new Exception("Steuerelement fehlt: " + controlName);
        if (!element.IsVisible || element.ActualWidth < 1 || element.ActualHeight < 1)
            throw new Exception("Steuerelement ist nicht sichtbar: " + controlName);
        var bounds = element.TransformToAncestor(page).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        if (bounds.Left < -1 || bounds.Top < -1 || bounds.Right > page.ActualWidth + 1 || bounds.Bottom > page.ActualHeight + 1)
            throw new Exception($"{controlName} passt nicht in {pageName} bei {window.Width}x{window.Height}: {bounds}, Seite {page.ActualWidth}x{page.ActualHeight}.");
    }
    private static async Task VerifyNetworkHelpAsync(MainWindow window, DynexRuntime runtime, string directory)
    {
        var snapshot = runtime.Snapshot ?? throw new Exception("Snapshot für Netzwerkhinweis fehlt.");
        var render = typeof(MainWindow).GetMethod("RenderSnapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        const string problem = "Automatische PC-Verbindung nicht verfügbar. Ethernet: öffentliches Windows-Netzwerk. DYNEX verbindet PCs nur in einem privaten Windows-Netzwerk.";
        try
        {
            window.Width = 900; window.Height = 650; window.ShowOverview();
            var advice = new Dynex.Core.PeerNetworkDiagnostics("Automatisch", [], []).Assess();
            render.Invoke(window, [snapshot with { NetworkProblem = problem, NetworkStatus = problem, Connection = advice }]);
            window.UpdateLayout();
            var help = (System.Windows.Controls.Button)window.FindName("NetworkHelpButton");
            if (!help.IsVisible || !((System.Windows.Controls.TextBlock)window.FindName("CoordinationText")).Text.Contains("öffentliches"))
                throw new Exception("Netzwerkproblem ist nicht sichtbar und erreichbar.");
            AssertVisibleControl(window, "OverviewPage", "NetworkHelpButton");
            Capture(window, Path.Combine(directory, "network-problem-overview-900.png"));
            help.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            window.UpdateLayout();
            var checkButton = (System.Windows.Controls.Button)window.FindName("CheckConnectionButton");
            var checkStatus = (System.Windows.Controls.TextBlock)window.FindName("NetworkCheckStatusText");
            if (checkButton.IsEnabled || help.IsEnabled || !checkStatus.IsVisible || !checkStatus.Text.Contains("werden geprüft"))
                throw new Exception("Verbindungsprüfung zeigt keinen laufenden Zustand oder erlaubt doppelte Klicks.");
            if (!((FrameworkElement)window.FindName("NetworkSettingsSection")).IsVisible ||
                ((System.Windows.Controls.TextBlock)window.FindName("NetworkHelpText")).Text != $"{problem} Nächster Schritt: {advice.NextStep}")
                throw new Exception("Netzwerkhilfe öffnet nicht den vollständigen Fehlergrund.");
            AssertPageFits(window, "SettingsPage", ["NetworkHelpText", "WindowsNetworkButton", "CheckConnectionButton", "ExternalBox", "PairingBox"]);
            Capture(window, Path.Combine(directory, "network-problem-settings-900.png"));
            var diagnostic = new Dynex.Core.PeerNetworkDiagnostics("Automatisch", ["192.168.1.2"], [],
                SentDatagrams: 8, ReceivedDatagrams: 4, OtherModeDatagrams: 4);
            render.Invoke(window, [snapshot with { NetworkProblem = null, NetworkDiagnostics = diagnostic, Connection = null }]);
            window.UpdateLayout();
            var diagnosticText = (System.Windows.Controls.TextBlock)window.FindName("NetworkHelpText");
            if (diagnosticText.Text != diagnostic.Hint || !diagnosticText.ToolTip.ToString()!.Contains("Gesendet: 8"))
                throw new Exception("Netzwerkhilfe verliert den beobachteten Moduskonflikt oder Transportzähler.");
            AssertPageFits(window, "SettingsPage", ["NetworkHelpText", "WindowsNetworkButton", "CheckConnectionButton", "ExternalBox", "PairingBox"]);
            Capture(window, Path.Combine(directory, "network-mode-diagnostics-fixture-900.png"));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!checkButton.IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(20);
            if (!checkButton.IsEnabled || !help.IsEnabled || !checkStatus.Text.StartsWith("Geprüft um ") ||
                !checkStatus.Text.Contains("Isolierter Test"))
                throw new Exception("Verbindungsprüfung liefert kein sichtbares Ergebnis: " + checkStatus.Text);
            var completedText = checkStatus.Text;
            render.Invoke(window, [runtime.Snapshot!]);
            if (checkStatus.Text != completedText)
                throw new Exception("Die nächste Messung überschreibt das Ergebnis der Verbindungsprüfung.");
            window.UpdateLayout();
            AssertPageFits(window, "SettingsPage", ["NetworkCheckStatusText", "NetworkHelpText", "CheckConnectionButton", "ExternalBox"]);
            AssertVisibleControl(window, "NetworkSettingsSection", "ExternalBox");
            Capture(window, Path.Combine(directory, "network-check-complete-900.png"));
        }
        finally { render.Invoke(window, [snapshot]); window.ShowOverview(); }
    }

    private static async Task VerifyStatusRefreshAsync(MainWindow window, DynexRuntime runtime, string directory)
    {
        window.Width = 900; window.Height = 650; window.ShowSettings("network");
        var router = (System.Windows.Controls.TextBox)window.FindName("RouterBox");
        var savedText = router.Text;
        var settings = runtime.Settings;
        var before = runtime.Snapshot!.History.Last().Time;
        var button = (System.Windows.Controls.Button)window.FindName("RefreshStatusButton");
        var status = (System.Windows.Controls.TextBlock)window.FindName("StatusRefreshText");
        try
        {
            router.Text = "192.168.99.1";
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (button.IsEnabled || !status.IsVisible || !status.Text.Contains("werden abgefragt"))
                throw new Exception("Statusaktualisierung zeigt keinen laufenden Zustand.");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!button.IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(20);
            if (!button.IsEnabled || !status.Text.StartsWith("Status aktualisiert um ") ||
                runtime.Snapshot!.History.Last().Time <= before)
                throw new Exception("Statusaktualisierung wartet nicht auf eine frische Messung: " + status.Text);
            if (runtime.Settings != settings || router.Text != "192.168.99.1" ||
                new SettingsStore(ProfilePath).Load() != settings)
                throw new Exception("Statusaktualisierung verändert Einstellungen oder ungespeicherte Eingaben.");
            window.UpdateLayout();
            AssertVisibleControl(window, "SettingsPage", "StatusRefreshText");
            Capture(window, Path.Combine(directory, "status-refresh-complete-900.png"));
            window.ShowSettings("analysis"); window.UpdateLayout();
            if (((System.Windows.Controls.TextBlock)window.FindName("RouterGuideTitle")).Text != "Internetrouter nicht erkannt")
                throw new Exception("Ohne bestätigten Router zeigt der Assistent einen Gerätenamen an.");
            Capture(window, Path.Combine(directory, "router-unconfirmed-900.png"));
            await File.WriteAllTextAsync(Path.Combine(directory, "status-actions-result.txt"),
                "PASS: real refresh and connection button clicks show busy/completed states; fresh measurement; unsaved fields and saved settings preserved; diagnostic result survives live updates; installed version visible.\n");
        }
        finally { router.Text = savedText; window.ShowOverview(); }
    }

    private static void VerifyProtectionIdle(MainWindow window, DynexRuntime runtime, string directory)
    {
        var snapshot = runtime.Snapshot ?? throw new Exception("Snapshot für Filterstatus fehlt.");
        var render = typeof(MainWindow).GetMethod("RenderSnapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        string Text(string name) => ((System.Windows.Controls.TextBlock)window.FindName(name)).Text;
        try
        {
            window.Width = 900; window.Height = 650; window.ShowOverview();
            var idle = snapshot with
            {
                State = "Bereit für deine nächste Runde", Detail = "Kein Spiel im Fokus. Der Netzwerkfilter ist inaktiv.",
                IsGaming = false, Game = "", DriverActive = false, QosActive = false,
                DriverState = "Netzwerkfilter inaktiv. Kein Spiel im Fokus.", LimitMbps = null,
                GamePriorityPackets = 0, VoicePriorityPackets = 0, ProtectionIdle = true,
                SharedDownloadBudgetMbps = null, GamingCount = 0
            };
            render.Invoke(window, [idle]); window.UpdateLayout();
            if (Text("QosStatusText") != "Netzwerkfilter inaktiv" || Text("ProtectionStatusText") != "NETZWERKSCHUTZ · INAKTIV")
                throw new Exception("Bestätigter Filter-Leerlauf erscheint als Fehler oder aktive Priorisierung.");
            AssertPageFits(window, "OverviewPage", ["QosStatusText", "QosDetailText", "DriverDetail"]);
            Capture(window, Path.Combine(directory, "filter-idle-fixture-900.png"));
            render.Invoke(window, [idle with { ProtectionIdle = false, DriverState = "Freigabe nicht bestätigt: Testfehler" }]);
            if (Text("QosStatusText") == "Netzwerkfilter inaktiv" || Text("ProtectionStatusText") == "NETZWERKSCHUTZ · INAKTIV")
                throw new Exception("Unbestätigte Freigabe wird fälschlich als Filter-Leerlauf angezeigt.");
            render.Invoke(window, [idle with { DriverActive = false, QosActive = true, ProtectionIdle = false, GamePriorityPackets = 12, VoicePriorityPackets = 4 }]);
            if (Text("PriorityOverviewText") != "PRIORITÄT AKTIV" || Text("QosStatusText") != "Priorisierung aktiv" ||
                !Text("QosDetailText").Contains("12") || !Text("QosDetailText").Contains("4"))
                throw new Exception("Prioritätsstatus wird nicht anhand der bestätigten QoS-Markierungen und Paket-Zähler angezeigt.");
        }
        finally { render.Invoke(window, [snapshot]); window.ShowOverview(); }
    }

    private static void VerifyDevicePaging(MainWindow window, DynexRuntime runtime)
    {
        var snapshot = runtime.Snapshot ?? throw new Exception("Snapshot für Geräteseiten fehlt.");
        var render = typeof(MainWindow).GetMethod("RenderSnapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var rows = Enumerable.Range(1, 13).Select(i => new DeviceView($"TEST-PC-{i:00}", $"192.168.1.{i}", false, "", 12.5, "Testdaten", i, i / 2d)).ToArray();
        window.Width = 900; window.Height = 650; window.ShowDevices(); window.UpdateLayout();
        var grid = (System.Windows.Controls.DataGrid)window.FindName("DeviceGrid");
        var next = (System.Windows.Controls.Button)window.FindName("NextDevicesButton");
        var previous = (System.Windows.Controls.Button)window.FindName("PreviousDevicesButton");
        try
        {
            // Keep the synthetic render and clicks synchronous on the dispatcher;
            // queued live updates cannot replace the fixture between assertions.
            render.Invoke(window, [snapshot with { Devices = rows, ParticipantCount = rows.Length }]);
            window.UpdateLayout();
            if (grid.Items.Count < 3) throw new Exception("Im kleinsten Fenster sind weniger als drei PCs sichtbar.");
            var seen = new HashSet<string>();
            for (var page = 0; page < 13; page++)
            {
                foreach (var item in grid.Items.OfType<DeviceView>()) seen.Add(item.Name);
                AssertPageFits(window, "DevicesPage", ["DeviceGrid", "NextDevicesButton", "PreviousDevicesButton"]);
                if (!next.IsEnabled) break;
                next.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                window.UpdateLayout();
            }
            if (seen.Count != rows.Length || !previous.IsEnabled)
                throw new Exception("Nicht alle PCs sind über die Geräteseiten erreichbar.");
            var pageRows = grid.Items.OfType<DeviceView>().Select(d => d.Name).ToArray();
            render.Invoke(window, [snapshot with { Devices = rows, ParticipantCount = rows.Length }]);
            window.UpdateLayout();
            if (!grid.Items.OfType<DeviceView>().Select(d => d.Name).SequenceEqual(pageRows))
                throw new Exception("Ein Messwert-Update setzt die gewählte Geräteseite zurück.");
        }
        finally { render.Invoke(window, [snapshot]); window.UpdateLayout(); }
    }
    private static void AssertPageFits(MainWindow window, string pageName, string[] controls)
    {
        var page = (FrameworkElement)window.FindName(pageName);
        foreach (var name in controls) AssertVisibleControl(window, pageName, name);
        void CheckScroll(DependencyObject node)
        {
            if (node is FrameworkElement hidden && !hidden.IsVisible) return;
            if (node is System.Windows.Controls.ScrollViewer scroll &&
                (scroll.ScrollableWidth > 1 || scroll.ScrollableHeight > 1))
                throw new Exception($"{pageName} benötigt Scrollen bei {window.Width}x{window.Height}: {scroll.ScrollableWidth}x{scroll.ScrollableHeight}.");
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) CheckScroll(VisualTreeHelper.GetChild(node, i));
        }
        CheckScroll(page);
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
                $"Netzwerkkomponente verfügbar: {snapshot.DriverAvailable}\nKomponentenstatus: {snapshot.DriverState}\n" +
                $"Priorisierung aktiv: {snapshot.QosActive}; Spiel-/Sprachpakete: {snapshot.GamePriorityPackets}/{snapshot.VoicePriorityPackets}\n" +
                $"Netzwerkfilter ohne Gaming bestätigt inaktiv: {snapshot.ProtectionIdle}\n" +
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
        await runtime.SaveSettingsAsync(runtime.Settings with { DeviceName = "changed", BackgroundPercent = 95,
            ReservePerGamerMbps = 9, Adaptive = true, ManualLimitMbps = 1, ManualUploadLimitMbps = 2,
            UploadLimitEnabled = true, NightFree = true, AutoConnectLan = true });
        if (runtime.Settings.DeviceName != Environment.MachineName || runtime.Settings.Adaptive ||
            runtime.Settings.ReservePerGamerMbps != 0 || runtime.Settings.ManualLimitMbps is not null ||
            runtime.Settings.ManualUploadLimitMbps is not null || runtime.Settings.UploadLimitEnabled || runtime.Settings.NightFree)
            throw new Exception("Windows-PC-Name ging verloren oder alte Limitwerte wurden nicht neutralisiert.");
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
        await runtime.SaveSettingsAsync(runtime.Settings with { Mode = GamingMode.Off });
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
        window.ShowDevices(); window.UpdateLayout();
        // Real encrypted transport and bindings, synthetic rates. Only the isolated smoke profile changes.
        await runtime.SaveSettingsAsync(runtime.Settings with { AutoConnectLan = false, Mode = GamingMode.Off });
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
        peer.Publish(true, 120.5, game: "Testspiel im Fokus", uploadMbps: 12.25);
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
        await WaitFor(() => Remote() is { DownloadMbps: 120.5, UploadMbps: 12.25 });
        var budgetLabel = (System.Windows.Controls.TextBlock)window.FindName("DevicePriorityText");
        var measuredCapacity = (System.Windows.Controls.TextBlock)window.FindName("CapacityMeasurementText");
        var coordinationLabel = (System.Windows.Controls.TextBlock)window.FindName("CoordinationText");
        if (!(runtime.Snapshot!.QosActive
                ? budgetLabel.Text.Contains("bestätigt QoS-Markierungen", StringComparison.OrdinalIgnoreCase)
                : budgetLabel.Text.Contains("für QoS", StringComparison.OrdinalIgnoreCase) && !budgetLabel.Text.Contains("bestätigt", StringComparison.OrdinalIgnoreCase)) ||
            !coordinationLabel.Text.Contains("Router-QoS oder SQM", StringComparison.OrdinalIgnoreCase) ||
            measuredCapacity.Text.Contains("Budget", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Priorisierungsstatus oder Routerhinweis fehlen in der Oberfläche.");
        var grid = (System.Windows.Controls.DataGrid)window.FindName("DeviceGrid");
        string Cell(string property)
        {
            var row = grid.Items.OfType<DeviceView>().Single(d => d.Name == peerName);
            var column = grid.Columns.OfType<System.Windows.Controls.DataGridBoundColumn>().Single(c =>
                (c.Binding as System.Windows.Data.Binding)?.Path.Path == property);
            return (column.GetCellContent(row) as System.Windows.Controls.TextBlock)?.Text ?? throw new Exception("Durchsatzzelle fehlt.");
        }
        foreach (var width in new[] { 900, 1240, 1440, 1920, 2560 })
        {
            window.Width = width; window.Height = 940;
            window.ShowDevices(); window.UpdateLayout();
            grid.BringIntoView();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            if (grid.Columns.Sum(c => c.ActualWidth) > grid.ActualWidth + 1)
                throw new Exception("Gerätetabelle ist breiter als das Fenster.");
            if (Cell(nameof(DeviceView.DownloadMbps)) != $"{120.5:0.0} Mbps" || Cell(nameof(DeviceView.UploadMbps)) != $"{12.25:0.0} Mbps")
                throw new Exception("Download und Upload sind nicht korrekt gebunden.");
            Capture(window, Path.Combine(directory, $"peer-traffic-fixture-{width}.png"));
        }
        window.Width = 900; window.Height = 650; window.ShowDevices(); window.UpdateLayout();
        var wrappedCoordination = (System.Windows.Controls.TextBlock)window.FindName("CoordinationText");
        if (wrappedCoordination.ActualHeight <= wrappedCoordination.FontSize * 1.5)
            throw new Exception("Der Hinweis zur netzwerkweiten Routerpriorisierung wird im Mindestfenster nicht umgebrochen.");
        var devicesHeader = (FrameworkElement)window.FindName("DevicesPageHeader");
        var devicesCard = (FrameworkElement)window.FindName("DevicesPageCard");
        if (System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(devicesHeader).Bottom >
            System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(devicesCard).Top + 1)
            throw new Exception("Die Kopfzeile der Geräteansicht überlappt im Mindestfenster den Inhalt.");
        window.UpdateLayout();
        Capture(window, Path.Combine(directory, "devices-traffic-fixture-900x650.png"));
        var diagnostic = await runtime.ExportDiagnosticsAsync();
        using (var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(diagnostic)))
        {
            var remote = json.RootElement.GetProperty("Snapshot").GetProperty("Devices").EnumerateArray().Single(d => d.GetProperty("Name").GetString() == peerName);
            if (remote.GetProperty("DownloadMbps").GetDouble() != 120.5 || remote.GetProperty("UploadMbps").GetDouble() != 12.25)
                throw new Exception("Diagnoseexport verliert Peer-Durchsatz.");
        }
        peer.Publish(false, 0, uploadMbps: 0);
        await WaitFor(() => Remote() is { DownloadMbps: 0, UploadMbps: 0 });
        if (Cell(nameof(DeviceView.DownloadMbps)) != $"{0:0.0} Mbps" || Cell(nameof(DeviceView.UploadMbps)) != $"{0:0.0} Mbps") throw new Exception("Gemessener Stillstand erscheint nicht als Null.");
        peer.Publish(false);
        await WaitFor(() => Remote() is { DownloadMbps: null, UploadMbps: null });
        if (Cell(nameof(DeviceView.DownloadMbps)) != "—" || Cell(nameof(DeviceView.UploadMbps)) != "—") throw new Exception("Fehlende Messwerte erscheinen nicht als unbekannt.");
        await peer.StopAsync();
        await WaitFor(() => Remote() is null, 14);
    }
    private static async Task VerifyProcessPickerAsync(MainWindow window, DynexRuntime runtime, string directory)
    {
        var picker = (System.Windows.Controls.ComboBox)window.FindName("RunningProcessesBox");
        var refresh = (System.Windows.Controls.Button)window.FindName("RefreshProcessesButton");
        var add = (System.Windows.Controls.Button)window.FindName("AddProcessButton");
        var search = (System.Windows.Controls.TextBox)window.FindName("ProcessSearchBox");
        var configured = (System.Windows.Controls.ListBox)window.FindName("ConfiguredProcessesList");
        var configuredNames = (System.Collections.ObjectModel.ObservableCollection<string>)configured.ItemsSource;
        var emptyState = (System.Windows.Controls.TextBlock)window.FindName("NoConfiguredProcessesText");
        var initiallySaved = new SettingsStore(ProfilePath).Load();
        var initiallyConfigured = SettingsStore.Split(initiallySaved.GameProcesses);
        if (!configuredNames.SequenceEqual(initiallyConfigured))
            throw new Exception("Die gespeicherten Programme werden beim Fensterstart nicht in die Liste geladen.");

        window.ShowSettings("games");
        foreach (var width in new[] { 900, 1440 })
        {
            window.Width = width; window.Height = 940; window.UpdateLayout();
            window.ShowSettings("games"); window.UpdateLayout();
            picker.IsDropDownOpen = true; // Exercise the real open -> refresh event, not only the helper.
            var deadline = Stopwatch.StartNew();
            while (!refresh.IsEnabled)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(8)) throw new Exception("Prozessauswahl lädt nicht fertig.");
                await Task.Delay(25);
            }
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            if (!picker.Items.Cast<string>().SequenceEqual(Games.GameDetector.FilterSelectableProcessNames(picker.Items.Cast<string>())))
                throw new Exception("Prozessauswahl ändert vorhandene Eingaben oder liefert ungültige Namen.");
            if (add.IsEnabled) throw new Exception("Hinzufügen ist ohne Auswahl aktiv.");
            if (picker.SelectedItem is not null) throw new Exception("Die Prozessliste wählt beim Laden automatisch ein Programm aus.");
            Capture(window, Path.Combine(directory, $"process-picker-{width}.png"));
            var popup = (System.Windows.Controls.Primitives.Popup)picker.Template.FindName("PART_Popup", picker);
            if (!popup.IsOpen || popup.Child is not FrameworkElement popupContent) throw new Exception("Prozess-Dropdown öffnet nicht.");
            Capture(popupContent, Path.Combine(directory, $"process-picker-list-{width}.png"));
            picker.IsDropDownOpen = false;
        }

        await window.RefreshRunningProcessesAsync();
        var sourceField = typeof(MainWindow).GetField("_runningProcessNames", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new Exception("Ungefilterte Prozessquelle fehlt.");
        var fullSource = ((IReadOnlyList<string>)sourceField.GetValue(window)!).ToArray();
        var sample = fullSource.FirstOrDefault(name => name.Length >= 3 && name.Any(char.IsLetter));
        if (sample is null) throw new Exception("Keine laufenden Prozessnamen für den Such-Smoke-Test verfügbar.");
        search.Clear(); picker.SelectedItem = null;
        var take = Math.Min(3, sample.Length - 2);
        var start = Math.Clamp(sample.Length / 2 - take / 2, 1, sample.Length - take - 1);
        var fragment = sample.Substring(start, take);
        search.Text = fragment;
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var expectedMatches = fullSource.Where(name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (expectedMatches.Length == 0 || !picker.Items.Cast<string>().SequenceEqual(expectedMatches) || add.IsEnabled)
            throw new Exception("Die Suche nach einem mittleren Teilstring filtert die Laufzeitliste nicht oder aktiviert Hinzufügen ohne Auswahl.");
        var caseVariant = new string(fragment.Select(character => char.IsLetter(character)
            ? char.IsUpper(character) ? char.ToLowerInvariant(character) : char.ToUpperInvariant(character)
            : character).ToArray());
        search.Text = caseVariant;
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (!picker.Items.Cast<string>().SequenceEqual(expectedMatches))
            throw new Exception("Die Prozesssuche unterscheidet Groß- und Kleinschreibung.");
        picker.SelectedItem = sample;
        if (!add.IsEnabled) throw new Exception("Ein ausgewählter Suchtreffer kann nicht hinzugefügt werden.");
        search.Text = "Dynex-No-Matching-Process-" + Guid.NewGuid().ToString("N");
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (picker.Items.Count != 0 || add.IsEnabled || picker.SelectedItem is not null)
            throw new Exception("Bei keinem Suchtreffer bleibt die Auswahl leer und Hinzufügen deaktiviert.");
        search.Text = fragment;
        await window.RefreshRunningProcessesAsync(); // Refresh replaces the source but must retain the active query.
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var refreshedSource = ((IReadOnlyList<string>)sourceField.GetValue(window)!).ToArray();
        var refreshedMatches = refreshedSource.Where(name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!picker.Items.Cast<string>().SequenceEqual(refreshedMatches) || search.Text != fragment || add.IsEnabled)
            throw new Exception("Aktualisieren verliert den Suchfilter oder wählt ein Ergebnis automatisch aus.");
        search.Clear();
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (!picker.Items.Cast<string>().SequenceEqual(refreshedSource) || picker.SelectedItem is not null || add.IsEnabled)
            throw new Exception("Leeren der Suche stellt die vollständige Liste nicht ohne Vorauswahl wieder her.");

        // Deterministic selection fixtures; no process is started.
        configuredNames.Clear();
        configuredNames.Add("cs2.EXE");
        configuredNames.Add("Existing-Game");
        picker.ItemsSource = new[] { "CS2.exe", "DynexPickerTest.exe", "DYNEXPICKERTEST.EXE" };
        picker.SelectedIndex = 0;
        add.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (!configuredNames.SequenceEqual(new[] { "cs2.EXE", "Existing-Game" }))
            throw new Exception("Auswahl legt ein .exe-/Großschreibungsduplikat an.");
        picker.SelectedIndex = 1;
        if (!add.IsEnabled) throw new Exception("Hinzufügen bleibt trotz Auswahl gesperrt.");
        add.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        picker.SelectedIndex = 2;
        add.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var expected = new[] { "cs2.EXE", "Existing-Game", "DynexPickerTest" };
        if (!configuredNames.SequenceEqual(expected)) throw new Exception("Hinzufügen überschreibt vorhandene Namen oder erkennt Duplikate nicht case-insensitive.");
        window.ShowOverview(); window.ShowSettings("games"); window.UpdateLayout();
        if (!configuredNames.SequenceEqual(expected) || runtime.Settings.GameProcesses != initiallySaved.GameProcesses)
            throw new Exception("Seitenwechsel verändert oder speichert die ungespeicherte Programmliste.");

        var save = (System.Windows.Controls.Button)window.FindName("SettingsSaveButton");
        async Task SaveAndWait(string expectedProcesses)
        {
            save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var saveDeadline = Stopwatch.StartNew();
            while ((runtime.Settings.GameProcesses != expectedProcesses || new SettingsStore(ProfilePath).Load().GameProcesses != expectedProcesses ||
                    ((System.Windows.Controls.TextBlock)window.FindName("ErrorText")).Text != "Einstellungen gespeichert.") &&
                   saveDeadline.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(25);
            if (new SettingsStore(ProfilePath).Load().GameProcesses != expectedProcesses || runtime.Settings.GameProcesses != expectedProcesses)
                throw new Exception("Der Speichern-Knopf auf der Spieleseite übernimmt die Programme nicht korrekt.");
        }
        await SaveAndWait(string.Join("; ", expected));
        var loadSettings = typeof(MainWindow).GetMethod("LoadSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new Exception("Der bestehende Fenster-Ladepfad für Einstellungen fehlt.");
        var savedConfigured = new SettingsStore(ProfilePath).Load();
        loadSettings.Invoke(window, [savedConfigured]);
        if (!configuredNames.SequenceEqual(expected) || emptyState.Visibility == Visibility.Visible)
            throw new Exception("Erneutes Laden stellt die gespeicherte Programmliste nicht wieder her.");
        search.Text = fragment;
        foreach (var width in new[] { 900, 1440 })
        {
            window.Width = width; window.Height = width == 900 ? 650 : 940;
            window.ShowSettings("games"); window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            foreach (var control in new[] { "ProcessSearchBox", "RunningProcessesBox", "AddProcessButton", "ConfiguredProcessesList", "SettingsSaveButton" })
                AssertVisibleControl(window, "SettingsPage", control);
            Capture((FrameworkElement)window.FindName("SettingsPage"), Path.Combine(directory, $"process-configured-{width}.png"));
        }
        search.Clear();

        configured.ScrollIntoView("DynexPickerTest"); configured.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var item = configured.ItemContainerGenerator.ContainerFromItem("DynexPickerTest") as System.Windows.Controls.ListBoxItem
            ?? throw new Exception("Hinzugefügtes Programm besitzt keinen sichtbaren Listeneintrag.");
        var remove = FindVisualChild<System.Windows.Controls.Button>(item)
            ?? throw new Exception("Entfernen-Schaltfläche fehlt im Programmeintrag.");
        if (!Equals(remove.Tag, "DynexPickerTest") || !Equals(remove.Content, "Entfernen"))
            throw new Exception("Entfernen-Schaltfläche enthält nicht den Programmnamen oder die erwartete Beschriftung.");
        remove.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (configuredNames.Contains("DynexPickerTest")) throw new Exception("Entfernen entfernt das Programm nicht aus der Liste.");
        foreach (var name in configuredNames.ToArray())
        {
            configured.ScrollIntoView(name); configured.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var row = configured.ItemContainerGenerator.ContainerFromItem(name) as System.Windows.Controls.ListBoxItem
                ?? throw new Exception("Programmeintrag fehlt vor dem Entfernen.");
            var button = FindVisualChild<System.Windows.Controls.Button>(row)
                ?? throw new Exception("Entfernen-Schaltfläche fehlt.");
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        }
        window.UpdateLayout();
        if (configuredNames.Count != 0 || emptyState.Visibility != Visibility.Visible)
            throw new Exception("Nach dem Entfernen aller Programme wird der Leerzustand nicht angezeigt.");
        await SaveAndWait("");
        var saved = new SettingsStore(ProfilePath).Load();
        if (saved.GameProcesses != "" || runtime.Settings.GameProcesses != "") throw new Exception("Leere Programmliste wurde nicht gespeichert.");
        configuredNames.Add("Reload-Fixture");
        loadSettings.Invoke(window, [saved]);
        if (configuredNames.Count != 0 || emptyState.Visibility != Visibility.Visible)
            throw new Exception("Erneutes Laden stellt den gespeicherten Leerzustand nicht wieder her.");
        await File.WriteAllTextAsync(Path.Combine(directory, "process-picker-result.txt"),
            "PASS: live source search by substring/mixed case/no hit/clear; refresh preserves filter; no auto-selection; duplicate protection for .exe and case variants; remove controls and empty state; explicit save and settings reload. No game process launched.\n");
    }
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }
    private static void Capture(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
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
