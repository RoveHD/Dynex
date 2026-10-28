using Dynex.Desktop;

internal static class UninstallChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var app = @"C:\Users\one\AppData\Local\Programs\DYNEX\DYNEX.exe";
        var second = @"C:\Users\two\AppData\Local\Programs\DYNEX\DYNEX.exe";
        var system = new FakeSystem();
        check(await InstallerSystem.InstallAsync(system, app, "verified.msi") == 0 && system.Installs == 1 &&
              system.Ownership is { FilterOrigin: SetupOwnership.Owned } && system.JournalBeforeInstall,
            "first setup journals absence before MSI and records exact successful filter ownership");
        check(system.ServiceEnsures == 1, "first setup provisions broker inside the existing elevated setup transaction");
        await InstallerSystem.InstallAsync(system, app.ToUpperInvariant(), null);
        check(system.Installs == 1 && system.Ownership!.Applications.Length == 1,
            "updates and path casing retain original ownership without duplicate references");
        await InstallerSystem.InstallAsync(system, second, null);
        var result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode == 0 && system.Removes == 0 && system.Ownership!.Applications.SequenceEqual([second]) && result.Message.Contains("andere registrierte"),
            "first uninstall removes its own firewall and reference but retains shared filter for second installation");
        result = await InstallerSystem.RemoveAsync(system, second, false);
        check(result.ExitCode == 0 && system.Removes == 1 && system.Ownership is null && !system.FilterPresent,
            "last registered installation removes the exact DYNEX-owned filter and receipt");
        check(system.BrokerRemovedBeforeFilter, "uninstall confirms broker/worker shutdown before removing kernel filter");
        result = await InstallerSystem.RemoveAsync(system, second, false);
        check(result.ExitCode == 0 && system.Removes == 1, "repeated removal after success is idempotent");

        system = Owned(app);
        system.ServiceRemoveError = new IOException("release not confirmed");
        result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode != 0 && system.Removes == 0 && system.FirewallRemoves == 0 && system.Ownership!.Applications.Contains(app),
            "unconfirmed broker shutdown preserves filter, firewall and ownership for recovery");
        system = Owned(app);
        system.ServiceEnsureError = new IOException("broker setup failed");
        var brokerSetupFailed = false;
        try { await InstallerSystem.InstallAsync(system, app, null); }
        catch (IOException error) { brokerSetupFailed = error.Message == "broker setup failed"; }
        check(brokerSetupFailed && system.FilterPresent && system.Ownership!.Applications.Contains(app),
            "failed broker provisioning is not successful setup and retains ownership journal");

        system = new() { FilterPresent = true, KnownProductPresent = true };
        await InstallerSystem.InstallAsync(system, app, null);
        check(system.Ownership?.FilterOrigin == SetupOwnership.Preexisting && system.Installs == 0,
            "existing independent filter is registered as preexisting and never adopted by setup");
        await InstallerSystem.InstallAsync(system, app, null);
        result = await InstallerSystem.RemoveAsync(system, app, true);
        check(result.ExitCode == 0 && system.Removes == 0 && system.FilterPresent && system.Ownership is null && result.Message.Contains("vor DYNEX"),
            "uninstall preserves confirmed preexisting filter even when legacy-removal switch is supplied");

        system = new() { FilterPresent = true, KnownProductPresent = true };
        await InstallerSystem.InstallAsync(system, app, null, legacyInstallation: true);
        check(system.Ownership?.FilterOrigin == SetupOwnership.Legacy && system.Installs == 0,
            "upgrade of old DYNEX with unknown filter history retains legacy uncertainty instead of declaring preexisting");
        await InstallerSystem.InstallAsync(system, app, null);
        result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode == 5201 && system.Removes == 0, "future updates preserve legacy origin until explicit uninstall decision");

        system = new() { FilterPresent = true, KnownProductPresent = true };
        result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode == 5201 && system.Removes == 0 && system.FirewallRemoves == 0 && system.Ownership is null,
            "legacy filter without receipt requires explicit decision before any removal");
        result = await InstallerSystem.RemoveAsync(system, app, true);
        check(result.ExitCode == 0 && system.Removes == 1, "confirmed legacy package is removed using the same exact-product path");
        system = new() { FilterPresent = true, KnownProductPresent = false };
        result = await InstallerSystem.RemoveAsync(system, app, true);
        check(result.ExitCode == 5202 && system.Removes == 0, "legacy confirmation never permits an unknown driver/product identity");

        system = Owned(app);
        system.RemoveCode = 1602;
        result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode == 1602 && system.Ownership?.Applications.Contains(app) == true && system.FirewallRemoves == 0,
            "cancelled MSI preserves receipt and firewall so uninstall can retry");
        system.RemoveCode = 1603;
        result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode == 1603 && system.Ownership?.Applications.Contains(app) == true,
            "failed MSI preserves actionable exit code and ownership");
        system.RemoveError = new System.ComponentModel.Win32Exception(1223);
        result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode == 1602 && system.Ownership is not null, "cancelled Windows authorization cannot become uninstall success");

        system = Owned(app);
        system.LeaveFilter = true;
        result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode != 0 && system.Ownership is not null && system.FirewallRemoves == 0,
            "MSI success without confirmed driver and product removal is rejected");
        system = Owned(app);
        system.RemoveCode = 3010;
        system.LeaveFilter = true;
        result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode == 3010 && system.Ownership is { RemovalPending: true, Applications.Length: 0 } && system.FirewallRemoves == 1,
            "MSI reboot requirement survives in protected receipt without forcing restart");
        system.KnownProductPresent = false;
        result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode == 3010 && system.Ownership?.RemovalPending == true,
            "driver still pending after MSI deregistration remains an explicit reboot result");
        system.FilterPresent = false;
        result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode == 3010 && system.Ownership?.RemovalPending == true,
            "same-boot product and service absence cannot erase pending kernel unload witness");
        system.BootIdentity = "boot-two";
        result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode == 0 && system.Ownership is null, "post-reboot absent driver clears pending removal receipt");

        system = new() { Ownership = new(SetupOwnership.Owned, [], true, "boot-one"), BootIdentity = "boot-two" };
        await InstallerSystem.InstallAsync(system, app, "verified.msi");
        check(system.Installs == 1 && system.Ownership is { FilterOrigin: SetupOwnership.Owned, RemovalPending: false } &&
              system.Ownership.Applications.SequenceEqual([app]),
            "direct reinstall after confirmed reboot and absent filter clears old pending receipt and establishes fresh ownership");
        system = new() { Ownership = new(SetupOwnership.Owned, [], true, "boot-one"), BootIdentity = "boot-one" };
        var sameBootBlocked = false;
        try { await InstallerSystem.InstallAsync(system, app, "verified.msi"); } catch (IOException) { sameBootBlocked = true; }
        check(sameBootBlocked && system.Installs == 0 && system.Ownership.RemovalPending,
            "direct reinstall stays blocked in removal boot despite absent registry entries");
        system = new() { Ownership = new(SetupOwnership.Owned, [], true, "boot-one"), BootIdentity = "boot-two", FilterPresent = true, KnownProductPresent = true };
        var survivedRebootBlocked = false;
        try { await InstallerSystem.InstallAsync(system, app, "verified.msi"); } catch (IOException) { survivedRebootBlocked = true; }
        check(survivedRebootBlocked && system.Installs == 0, "reboot alone cannot reactivate a filter whose removal did not finish");
        result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode == 0 && system.Removes == 1 && system.Ownership is null,
            "cleanup can retry exact-product removal after reboot when the package survived");
        system = Owned(app);
        system.BootIdentity = null;
        system.RemoveCode = 3010;
        result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode == 3010 && system.Ownership is { RemovalPending: true, RemovalBootIdentity: null },
            "unavailable boot identity preserves explicit deferred removal instead of assuming restart");
        result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode == 3010 && result.Message.Contains("WMI") && system.Ownership is not null,
            "persistent unavailable boot witness explains conservative recovery and never reports success");
        system.BootIdentity = "boot-two";
        result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode == 3010 && system.Ownership?.RemovalBootIdentity == "boot-two",
            "restored WMI establishes a boot witness before allowing later restart verification");
        system.BootIdentity = "boot-three";
        result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode == 0 && system.Ownership is null,
            "restart after restored boot witness provides a recoverable completion path");

        system = Owned(app);
        system.FirewallRemoveError = new IOException("modified firewall rule");
        result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode != 0 && result.Message.Contains("modified firewall rule") && system.Ownership is not null,
            "modified firewall rule is an incomplete uninstall rather than false success");
        system.FirewallRemoveError = null;
        result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode == 0 && system.Ownership is null, "retry finishes firewall cleanup after successful earlier filter removal");

        system = new() { InstallCode = 1602 };
        var cancelled = false;
        try { await InstallerSystem.InstallAsync(system, app, "verified.msi"); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && system.Ownership?.FilterOrigin == SetupOwnership.Pending,
            "interrupted installation leaves pending journal instead of claiming ownership");
        check(system.FirewallEnsures == 1, "LAN firewall is installed even when packet-filter MSI is cancelled");
        system.FilterPresent = system.KnownProductPresent = true;
        result = await InstallerSystem.RemoveAsync(system, app, false);
        check(result.ExitCode == 5201 && system.Removes == 0, "pending journal with surviving driver requires confirmation, never assumes ownership");

        system = new() { ConcurrentInstall = true };
        await InstallerSystem.InstallAsync(system, app, "verified.msi");
        check(system.Ownership?.FilterOrigin == SetupOwnership.Preexisting,
            "filter appearing before our MSI call is not adopted as DYNEX-created");
        system = new() { InstallCode = 3010 };
        check(await InstallerSystem.InstallAsync(system, app, "verified.msi") == 3010 && system.Ownership?.FilterOrigin == SetupOwnership.Owned,
            "setup reboot result retains confirmed filter ownership and independent LAN setup");
        system = new() { FirewallEnsureError = new IOException("blocked") };
        try { await InstallerSystem.InstallAsync(system, app, "verified.msi"); } catch (IOException) { }
        check(system.Ownership?.FilterOrigin == SetupOwnership.Pending && system.Installs == 0,
            "firewall policy error prevents starting MSI and preserves the setup journal");
        system = Owned(app);
        await InstallerSystem.InstallAsync(system, app, null, legacyInstallation: true);
        check(system.Ownership?.FilterOrigin == SetupOwnership.Owned, "upgrade flag never downgrades confirmed DYNEX filter ownership");
        system = new() { Ownership = new(SetupOwnership.Owned, [], true), FilterPresent = true, KnownProductPresent = false };
        var pendingInstallBlocked = false;
        try { await InstallerSystem.InstallAsync(system, app, "verified.msi"); } catch (IOException) { pendingInstallBlocked = true; }
        check(pendingInstallBlocked && system.Installs == 0, "setup cannot reactivate a filter pending removal and restart");
        system = new() { Ownership = new("unsupported", [app]), FilterPresent = true, KnownProductPresent = true };
        result = await InstallerSystem.RemoveAsync(system, app, true);
        check(result.ExitCode != 0 && system.Removes == 0, "invalid receipt cannot authorize filter removal");
        system = new() { Ownership = new(SetupOwnership.Owned, [@"C:\Other\foreign.exe"]), FilterPresent = true, KnownProductPresent = true };
        result = await InstallerSystem.RemoveAsync(system, app, true);
        check(result.ExitCode != 0 && system.Removes == 0, "receipt with foreign executable identity is rejected");

        check(await InstallerPrerequisites.ExecuteAsync(["--uninstall-system", "arbitrary"]) == 2 &&
              await InstallerPrerequisites.ExecuteAsync(["--cleanup-removed-installation", "arbitrary"]) == 2 &&
              await InstallerPrerequisites.ExecuteAsync(["--uninstall-machine", "--confirm-legacy-filter-removal", "extra"]) == 2 &&
              await InstallerPrerequisites.ExecuteAsync(["--cleanup-machine"]) == 2 &&
              await InstallerPrerequisites.ExecuteAsync(["--remove-user-data", "arbitrary"]) == 2,
            "removal entry points reject arbitrary arguments before elevation and system mutations");
        var installs = 0;
        var prereq = await InstallerPrerequisites.RunAsync(() => true, () => true, () => { installs++; return Task.FromResult(0); }, () => false);
        check(prereq.ExitCode == 0 && installs == 1, "upgrade of existing setup still enters elevated registration when receipt is missing");
        check(WindowsInstallerSystem.ParseBootIdentity("20261014120000.000000+120") == WindowsInstallerSystem.ParseBootIdentity("20261014100000.000000+000"),
            "documented WMI boot timestamp is normalized across time-zone offsets");
        check(WindowsInstallerSystem.ParseBootIdentity("20261014******.000000+120") is null &&
              WindowsInstallerSystem.ParseBootIdentity("garbage") is null && WindowsInstallerSystem.ParseBootIdentity("20261014120000.000000+999") is null,
            "partial malformed or impossible WMI boot timestamps do not become restart evidence");
        check(InstallerPrerequisites.MatchesInstallationProcess(app, Path.Combine(Path.GetDirectoryName(app)!, "shaper", "Dynex.Shaper.exe"), false) &&
              !InstallerPrerequisites.MatchesInstallationProcess(app, app, false) &&
              !InstallerPrerequisites.MatchesInstallationProcess(app, Path.Combine(Path.GetDirectoryName(second)!, "shaper", "Dynex.Shaper.exe"), false),
            "machine uninstall detects exact helper while allowing its wrapper and preserving unrelated installations");
        check(InstallerPrerequisites.MatchesInstallationProcess(app, app.ToUpperInvariant()) &&
              !InstallerPrerequisites.MatchesInstallationProcess(app, app + ".other"),
            "unprivileged uninstall preflight matches only exact running installation identity");
        check(InstallerPrerequisites.MatchesCleanupApplication(app, @"C:\Users\one\AppData\Local") &&
              !InstallerPrerequisites.MatchesCleanupApplication(app, @"D:\Redirected\Local") &&
              !InstallerPrerequisites.MatchesCleanupApplication(app, @"C:\Users\two\AppData\Local") &&
              !InstallerPrerequisites.MatchesCleanupApplication(app, ""),
            "standalone cleanup rejects redirected or foreign LocalAppData before system and user mutations");
    }

    private static FakeSystem Owned(string application) => new()
    { Ownership = new(SetupOwnership.Owned, [application]), FilterPresent = true, KnownProductPresent = true };

    private sealed class FakeSystem : IInstallerSystem
    {
        public bool FilterPresent { get; set; }
        public bool KnownProductPresent { get; set; }
        public string? BootIdentity { get; set; } = "boot-one";
        public SetupOwnership? Ownership { get; set; }
        internal int Installs, Removes, FirewallRemoves, FirewallEnsures, InstallCode, RemoveCode, ServiceEnsures, ServiceRemoves;
        internal bool BrokerRemovedBeforeFilter;
        internal bool JournalBeforeInstall, ConcurrentInstall, LeaveFilter;
        internal Exception? RemoveError, FirewallRemoveError, FirewallEnsureError, ServiceRemoveError, ServiceEnsureError;
        public SetupOwnership? ReadOwnership() => Ownership;
        public void WriteOwnership(SetupOwnership? ownership) => Ownership = ownership;
        public Task<PacketFilterSetup.InstallResult> InstallFilterAsync(string package)
        {
            Installs++;
            JournalBeforeInstall = Ownership?.FilterOrigin == SetupOwnership.Pending && !FilterPresent && !KnownProductPresent;
            if (InstallCode is 0 or 3010) FilterPresent = KnownProductPresent = true;
            return Task.FromResult(new PacketFilterSetup.InstallResult(InstallCode, !ConcurrentInstall));
        }
        public Task<int> RemoveFilterAsync()
        {
            Removes++;
            BrokerRemovedBeforeFilter = ServiceRemoves > 0;
            if (RemoveError is not null) throw RemoveError;
            if (RemoveCode is 0 or 3010 or 1605 && !LeaveFilter) FilterPresent = KnownProductPresent = false;
            return Task.FromResult(RemoveCode);
        }
        public void EnsureFirewall(string application) { if (FirewallEnsureError is not null) throw FirewallEnsureError; FirewallEnsures++; }
        public void RemoveFirewall(string application) { if (FirewallRemoveError is not null) throw FirewallRemoveError; FirewallRemoves++; }
        public Task EnsurePrioritizationAsync(string application) { if (ServiceEnsureError is not null) throw ServiceEnsureError; ServiceEnsures++; return Task.CompletedTask; }
        public Task RemovePrioritizationAsync(string application) { if (ServiceRemoveError is not null) throw ServiceRemoveError; ServiceRemoves++; return Task.CompletedTask; }
    }
}
