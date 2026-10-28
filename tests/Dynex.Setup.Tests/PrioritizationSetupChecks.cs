using System.Security.Cryptography;
using System.Text.Json;
using Dynex.Desktop;

internal static class PrioritizationSetupChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var calls = 0;
        var ready = false;
        var result = await InstallerPrerequisites.RunAsync(() => true, () => true,
            () => { calls++; ready = true; return Task.FromResult(0); }, () => true,
            prioritizationConfigured: () => ready);
        check(result.ExitCode == 0 && calls == 1, "existing filter/firewall still provisions missing privileged broker exactly once");
        result = await InstallerPrerequisites.RunAsync(() => true, () => true,
            () => { calls++; return Task.FromResult(0); }, () => true, prioritizationConfigured: () => ready);
        check(result.ExitCode == 0 && calls == 1, "configured broker skips all elevation and setup work on later launches");
        result = await InstallerPrerequisites.RunAsync(() => true, () => true,
            () => Task.FromResult(0), () => true, prioritizationConfigured: () => false);
        check(result.ExitCode != 0 && result.Message.Contains("nicht vollständig"), "process exit success cannot hide absent privileged broker");

        var executable = Path.Combine(PrioritizationServiceSetup.Root, new string('A', 64), "Dynex.Shaper.exe");
        var command = PrioritizationServiceSetup.ServiceCommand(executable);
        check(PrioritizationServiceSetup.IsManagedCommand(command), "SCM command is quoted and bound to a protected hash directory");
        check(!PrioritizationServiceSetup.IsManagedCommand(command + " --unexpected") &&
            !PrioritizationServiceSetup.IsManagedCommand(executable + " --service") &&
            !PrioritizationServiceSetup.IsManagedCommand(PrioritizationServiceSetup.ServiceCommand(Path.Combine(Path.GetTempPath(), "Dynex.Shaper.exe"))) &&
            !PrioritizationServiceSetup.IsManagedCommand(command.Replace(new string('A', 64), "..")),
            "SCM command rejects user-writable/unquoted paths, traversal and arbitrary arguments");
        PrioritizationServiceSetup.CheckCleanStop(0);
        var unconfirmed = false;
        try { PrioritizationServiceSetup.CheckCleanStop(1064); }
        catch (IOException error) { unconfirmed = error.Message.Contains("Freigabe") && error.Message.Contains("1064"); }
        check(unconfirmed, "stopped broker with failed worker cleanup blocks unsafe filter removal or payload upgrade");

        var root = Path.Combine(Path.GetTempPath(), "DYNEX-service-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source");
            var destination = Path.Combine(root, "protected-copy-fixture");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            File.WriteAllBytes(Path.Combine(source, "Dynex.Shaper.exe"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(source, "Dynex.Shaper.dll"), [4, 5, 6]);
            File.WriteAllText(Path.Combine(source, "Dynex.Shaper.runtimeconfig.json"), "{\"runtimeOptions\":{}}");
            var entries = Directory.GetFiles(source).Order().Select(path => new PrioritizationPayload.Entry(Path.GetFileName(path),
                new FileInfo(path).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new PrioritizationPayload.Manifest(1, entries));
            var payload = new PrioritizationPayload(bytes);
            payload.CopyVerified(source, destination);
            payload.VerifyDirectory(destination);
            check(File.ReadAllBytes(Path.Combine(destination, "Dynex.Shaper.dll")).SequenceEqual(new byte[] { 4, 5, 6 }) &&
                File.ReadAllBytes(Path.Combine(destination, PrioritizationPayload.ReceiptName)).SequenceEqual(bytes),
                "verified installer copy preserves exact engine bytes and immutable manifest receipt");
            File.WriteAllBytes(Path.Combine(destination, "Dynex.Shaper.dll"), [4, 5, 7]);
            check(Rejects(() => payload.VerifyDirectory(destination)), "modified protected DLL cannot pass payload validation");
            File.WriteAllBytes(Path.Combine(source, "Dynex.Shaper.dll"), [0, 0, 0]);
            var rejected = Path.Combine(root, "rejected-copy");
            Directory.CreateDirectory(rejected);
            check(Rejects(() => payload.CopyVerified(source, rejected)) && !Directory.EnumerateFileSystemEntries(rejected).Any(),
                "mutable source substitution is rejected before any executable is copied");
            File.WriteAllBytes(Path.Combine(source, "Dynex.Shaper.dll"), [4, 5, 6]);
            File.WriteAllText(Path.Combine(source, "foreign.dll"), "unexpected loader input");
            check(Rejects(() => payload.CopyVerified(source, rejected)), "unmanifested DLL/config additions cannot enter privileged service payload");
            check(Rejects(() => new PrioritizationPayload(JsonSerializer.SerializeToUtf8Bytes(new PrioritizationPayload.Manifest(2, entries)))) &&
                Rejects(() => new PrioritizationPayload(JsonSerializer.SerializeToUtf8Bytes(new PrioritizationPayload.Manifest(1, [.. entries, entries[0]])))),
                "unrecognized manifest schema and duplicate paths are rejected");
            check(new[] { "../evil.dll", "/evil.dll", "C:/evil.dll", "a\\evil.dll", "a//evil.dll", "a/../evil.dll", "bad.dll ", "payload-manifest.json" }
                .All(name => !PrioritizationPayload.ValidName(name)), "payload path validation rejects traversal, rooted names, streams and receipt replacement");
            check(PrioritizationPayload.ValidName("runtimes/win-x64/native/host.dll") &&
                !PrioritizationPayload.ValidHash(new string('g', 64)), "normal nested runtime files remain supported with strict SHA256 validation");
        }
        finally
        {
            // The fixture is a fresh absolute GUID directory under the system temp directory.
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe fixture cleanup.");
            Directory.Delete(root, true);
        }
    }

    private static bool Rejects(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
    }
}
