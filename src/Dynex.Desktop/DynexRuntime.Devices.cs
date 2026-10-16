using Dynex.Core;

namespace Dynex.Desktop;

public sealed partial class DynexRuntime
{
    private DeviceRegistry? deviceRegistry;
    public string? DeviceRegistryError { get; private set; }
    public IReadOnlyList<DeviceRegistryEntry> Devices => deviceRegistry?.Snapshot() ?? [];
    private void InitializeDevices()
    {
        try { deviceRegistry = new(store.DirectoryPath); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { DeviceRegistryError = "Geräteliste kann nicht sicher gelesen werden. Netzwerk bleibt gesperrt; Datei prüfen und DYNEX neu starten: " + System.IO.Path.Combine(store.DirectoryPath, "devices.json") + ". " + error.Message; }
    }
    private void ConfigureDevicePolicy(IPeerService service)
    {
        service.SetNetworkAccessEnabled(deviceRegistry is not null);
        service.SetExcludedDevices(deviceRegistry?.ExcludedIds ?? []);
    }
    private void ObserveDevices(IReadOnlyList<PeerAdvertisement> authenticatedSnapshot)
    {
        if (deviceRegistry is null) return;
        try { deviceRegistry.Observe(authenticatedSnapshot); DeviceRegistryError = null; }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { DeviceRegistryError = "Geräteliste speichern: " + error.Message; }
    }
    private void FlushDevices()
    {
        if (deviceRegistry is null) return;
        try { deviceRegistry.Flush(); DeviceRegistryError = null; }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { DeviceRegistryError = "Geräteliste speichern: " + error.Message; }
    }
    public async Task SetDevicePreferencesAsync(Guid id, string? alias, DeviceDisposition disposition)
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (deviceRegistry is null) throw new InvalidOperationException("Geräteliste ist nicht initialisiert.");
            deviceRegistry.SetPreferences(id, alias, disposition);
            if (peers is { } service) ConfigureDevicePolicy(service);
        }
        finally { lifecycle.Release(); }
    }
    public async Task<PeerConnectionTestResult> TestPeerConnectionAsync(Guid id, CancellationToken token = default)
    {
        var service = peers;
        if (service is null) return PeerConnectionTestResult.Unavailable(id, "Lokaler Peer-Dienst ist nicht gestartet.");
        if (deviceRegistry is null) return PeerConnectionTestResult.Unavailable(id, DeviceRegistryError ?? "Geräteliste nicht verfügbar.");
        ConnectionTestStage firewall;
        try
        {
            var local = await CheckNetworkAsync(token).ConfigureAwait(false);
            firewall = new("Lokale Firewall (Konfiguration)", local.FirewallConfigured == true ? ConnectionTestState.Passed : ConnectionTestState.Unknown,
                local.Summary + " Die Regelprüfung allein beweist keinen erfolgreichen UDP-Pfad.", Required: false);
        }
        catch (OperationCanceledException)
        {
            var cancelled = PeerConnectionTestResult.Unavailable(id, "Test während der lokalen Diagnose abgebrochen.");
            return cancelled with { Stages = cancelled.Stages.Select(s => s with { State = ConnectionTestState.Cancelled }).ToArray() };
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { firewall = new("Lokale Firewall (Konfiguration)", ConnectionTestState.Unknown, "Lokale Diagnose nicht vollständig: " + error.Message, Required: false); }
        var diagnostics = service.GetNetworkDiagnostics();
        var scope = new ConnectionTestStage("Lokale Erkennung / privates Windows-Netz",
            isolated ? ConnectionTestState.Unknown : Settings.AutoConnectLan && diagnostics.LocalAddresses.Count > 0 ? ConnectionTestState.Passed : ConnectionTestState.Unknown,
            isolated ? "Isolierter Loopback-Test: Windows-Netzprofil wird nicht geprüft." :
            $"Modus: {diagnostics.Mode}. Freigegebene lokale Adressen: {string.Join(", ", diagnostics.LocalAddresses)}. " +
            (Settings.AutoConnectLan ? service.NetworkStatus ?? "AutoLAN verwendet ausschließlich bestätigte private Windows-Netzprofile." : "Privatgruppenmodus: Windows-Netzkategorie hier nicht separat bestätigt."), Required: false);
        var known = Devices.FirstOrDefault(d => d.DeviceId == id)?.Device;
        PeerConnectionTestResult result;
        if (known?.Address is { } address && System.Net.IPAddress.TryParse(address, out var ip) && known.RemotePort is { } port)
            result = await service.TestConnectionAsync(id, new System.Net.IPEndPoint(ip, port), TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        else result = await service.TestConnectionAsync(id, TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        var roundtrip = result.Stages.Any(s => s.Name == "Anfrage und Antwort" && s.State == ConnectionTestState.Passed);
        var remoteFirewall = new ConnectionTestStage("UDP-Pfad / Firewall der Gegenstelle", roundtrip ? ConnectionTestState.Passed : ConnectionTestState.Unknown,
            roundtrip ? "Authentifizierte UDP-Antwort belegt einen nutzbaren Pfad für diesen Test. Die Firewallkonfiguration der Gegenstelle wurde nicht ausgelesen." :
            "Ohne passende Antwort ist die Firewall der Gegenstelle nicht prüfbar. Ebenso möglich: DYNEX aus, ältere Version oder Paketverlust; Ursache unbekannt.", Required: false);
        return result with { Stages = [firewall, scope, .. result.Stages, remoteFirewall] };
    }
}
