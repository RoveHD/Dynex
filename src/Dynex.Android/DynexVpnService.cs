using System.Net;
using System.Net.Sockets;
using Dynex.Core;
using Dynex.Mobile;
using global::Android.App;
using global::Android.Content;
using global::Android.Net;
using global::Android.OS;
using OperationCanceledException = System.OperationCanceledException;

namespace Dynex.Android;

[Service(Name = "de.dynex.mobile.DynexVpnService", Exported = false, Permission = "android.permission.BIND_VPN_SERVICE")]
public sealed class DynexVpnService : VpnService
{
    private const string ChannelId = "dynex-connection";
    private const int NotificationId = 741;
    private static readonly SemaphoreSlim RuntimeGate = new(1, 1);
    private readonly SemaphoreSlim changed = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private Task? runner;
    private WifiNetworkMonitor? network;
    private AutoLanPeerService? peers;
    private NativeTunnelSession? tunnel;
    private LocalSocksProxy? proxy;
    private volatile bool paused;
    private int refreshRequested, revoked, configureAdGuard;
    private bool adGuardRequested;
    private string? peerKey, proxyKey, lastNetwork, fault, lastNotification;
    private readonly ProxyTrafficMonitor trafficMonitor = new();

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var action = intent?.Action;
        if (action == "Stop")
        {
            TryDisableAdGuard();
            MobileSettingsStore.SetEnabled(this, false);
            lifetime.Cancel(); StopSelf();
            return StartCommandResult.NotSticky;
        }
        if (action is "Start" or "AdGuardConnect") MobileSettingsStore.SetEnabled(this, true);
        if (!MobileSettingsStore.IsEnabled(this)) { StopSelf(); return StartCommandResult.NotSticky; }
        if (action == "Pause") paused = intent!.GetBooleanExtra("Pause", false);
        if (action is "Start" or "Refresh" or "SettingsChanged" or "AdGuardConnect") Interlocked.Exchange(ref refreshRequested, 1);
        if (action is "Start" or "AdGuardConnect" || runner is null)
            Interlocked.Exchange(ref configureAdGuard, 1);
        ShowNotification("DYNEX startet");
        runner ??= Task.Run(() => RunAsync(lifetime.Token));
        Wake();
        return StartCommandResult.Sticky;
    }

    public override void OnRevoke()
    {
        // Android can revoke our authorization when another VPN takes over. Do not
        // request it again from the background or stop a separate AdGuard proxy.
        VpnCompatibility.SetOwnVpn(false);
        Interlocked.Exchange(ref revoked, 1);
        Wake();
    }

    public override void OnDestroy()
    {
        lifetime.Cancel();
        Wake();
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }

    private void Wake()
    {
        try { changed.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task RunAsync(CancellationToken token)
    {
        string? terminalError = null;
        var entered = false;
        try
        {
            await RuntimeGate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            var settings = MobileSettingsStore.Load(this);
            var identity = MobileSettingsStore.LoadIdentity(this);
            var deviceId = AutoLanIdentity.GetDeviceId(identity);
            network = new WifiNetworkMonitor(this, settings.UseAdGuardProxy);
            network.Changed += Wake;
            var previousProxyMode = settings.UseAdGuardProxy;
            var adGuardVpnInstalled = AdGuardIntegration.IsVpnInstalled(this);
            while (!token.IsCancellationRequested)
            {
                settings = MobileSettingsStore.Load(this);
                if (settings.UseAdGuardProxy && !previousProxyMode) Interlocked.Exchange(ref configureAdGuard, 1);
                previousProxyMode = settings.UseAdGuardProxy;
                network.SetProxyMode(settings.UseAdGuardProxy);
                var networkState = network.Snapshot();
                var wifi = networkState.Wifi;
                var transport = networkState.Transport;
                var refresh = Interlocked.Exchange(ref refreshRequested, 0) != 0;
                var networkChanged = lastNetwork != transport?.Fingerprint;
                if (networkChanged || refresh) { fault = null; lastNetwork = transport?.Fingerprint; }
                if (Interlocked.Exchange(ref revoked, 0) != 0 && !settings.UseAdGuardProxy)
                    fault = "VPN-Freigabe entzogen · DYNEX erneut starten, sobald das andere VPN beendet ist";

                // Forget authenticated peer membership immediately when leaving Wi-Fi.
                var nextPeerKey = settings.AutoConnect && wifi is not null
                    ? wifi.Fingerprint + "|" + settings.DeviceName + "|" + settings.SharePriority : null;
                if (peerKey != nextPeerKey)
                {
                    await StopPeersAsync().ConfigureAwait(false);
                    if (nextPeerKey is not null)
                    {
                        peers = new AutoLanPeerService(new DynexSettings
                        {
                            DeviceId = deviceId, DeviceName = settings.DeviceName, CapacityMeasured = false,
                            ShareWeight = settings.SharePriority,
                            AppVersion = typeof(DynexVpnService).Assembly.GetName().Version?.ToString(3),
                            HeartbeatInterval = TimeSpan.FromSeconds(4)
                        }, identity, () => network.Current?.Scope ?? AutoLanNetworkSnapshot.Empty, lowPowerDiscovery: true);
                        peers.PeersChanged += Wake;
                        await peers.StartAsync(token).ConfigureAwait(false);
                        peerKey = nextPeerKey;
                    }
                }
                if (refresh && wifi is not null) peers?.RequestDiscovery();
                var devices = peers?.Snapshot() ?? Array.Empty<PeerAdvertisement>();
                var plan = MobileLimitPolicy.Plan(deviceId, devices, settings.AutoConnect, paused,
                    settings.GamingDownloadLimitMbps, settings.SharePriority);
                var cap = wifi is not null ? plan.DownloadLimitMbps : null;
                var conflict = !settings.UseAdGuardProxy && networkState.OtherVpnActive;
                ProxySnapshot? traffic = null;
                try
                {
                    if (settings.UseAdGuardProxy)
                    {
                        await StopTunnelAsync().ConfigureAwait(false);
                        // Keep only a loopback listener on cellular/offline. No discovery or
                        // polling is kept alive there; outbound sockets are demand-driven.
                        var key = transport?.Fingerprint ?? "offline";
                        if (fault is null && (proxyKey != key || proxy is null))
                        {
                            await StopProxyAsync().ConfigureAwait(false);
                            var credentials = MobileSettingsStore.LoadProxySettings(this);
                            var bound = transport?.Network;
                            proxy = new LocalSocksProxy(socket =>
                            {
                                if (bound is null) throw new IOException("Kein physisches Netz verfügbar.");
                                WifiNetworkMonitor.BindSocket(bound, socket);
                            }, resolveHost: (host, ct) => bound is null
                                ? Task.FromException<IPAddress[]>(new IOException("Kein physisches Netz verfügbar."))
                                : WifiNetworkMonitor.ResolveAsync(bound, host, ct),
                                listenPort: credentials.Port, username: credentials.Username, password: credentials.Password);
                            proxy.DownloadObserved += Wake;
                            await proxy.StartAsync(token).ConfigureAwait(false);
                            trafficMonitor.Reset();
                            trafficMonitor.Observe(proxy.Snapshot(), cap);
                            proxyKey = key;
                        }
                        proxy?.SetDownloadLimit(cap is { } value ? (long)(value * 125000) : null);
                        if (proxy is not null && Interlocked.Exchange(ref configureAdGuard, 0) != 0 &&
                            AdGuardIntegration.HasSavedPassword(this))
                        {
                            // The listener must exist before AdGuard is asked to use it.
                            // Sending a broadcast is not an acknowledgement from AdGuard.
                            proxy.ObserveNextDownload();
                            AdGuardIntegration.ConfigureProxy(this, MobileSettingsStore.LoadProxySettings(this));
                            adGuardRequested = true;
                        }
                        traffic = proxy?.Snapshot();
                    }
                    else
                    {
                        if (adGuardRequested) TryDisableAdGuard();
                        await StopProxyAsync().ConfigureAwait(false);
                        if (networkChanged || cap is null || conflict || fault is not null)
                            await StopTunnelAsync().ConfigureAwait(false);
                        if (tunnel is not null && !tunnel.IsRunning)
                        {
                            fault = "Der lokale VPN-Tunnel wurde beendet · Verbindung aktualisieren";
                            await StopTunnelAsync().ConfigureAwait(false);
                        }
                        if (wifi is not null && cap is { } rate && !conflict && fault is null)
                        {
                            if (tunnel is null)
                            {
                                // Prepare is never called while another active VPN is present.
                                if (VpnCompatibility.HasOtherActiveVpn(this)) conflict = true;
                                else if (Prepare(this) is not null) fault = "VPN-Freigabe fehlt · in DYNEX auf Start tippen";
                                else
                                {
                                    VpnCompatibility.SetOwnVpn(true); network.SetOwnVpn(true);
                                    tunnel = await NativeTunnelSession.StartAsync(this, wifi, (long)(rate * 125000), Wake, token).ConfigureAwait(false);
                                    trafficMonitor.Reset();
                                }
                            }
                            else tunnel.SetDownloadLimit((long)(rate * 125000));
                            traffic = tunnel?.Snapshot();
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    fault = "Begrenzung nicht aktiv (" + ex.GetType().Name + ") · Verbindung aktualisieren";
                    LogFailure("Traffic service", ex);
                    await StopTunnelAsync().ConfigureAwait(false);
                    await StopProxyAsync().ConfigureAwait(false);
                    traffic = null;
                }

                var evidence = trafficMonitor.Observe(wifi is not null ? traffic : null, cap);
                var download = evidence.DownloadMbps;
                var upload = evidence.UploadMbps;
                var applied = evidence.LimitApplied && (settings.UseAdGuardProxy ? proxy is not null : tunnel?.IsRunning == true);
                peers?.Publish(false, downloadMbps: download, uploadMbps: upload,
                    appliedLimitMbps: applied ? cap : null, shaperActive: applied);
                var status = fault ?? (conflict ? "Anderes VPN aktiv · DYNEX pausiert" : settings.UseAdGuardProxy
                    ? transport is null ? "Proxy bereit · DYNEX in AdGuard ausnehmen oder Netzverbindung prüfen"
                        : wifi is null ? "Mobilfunk/anderes Netz · unbegrenzte Proxy-Durchleitung"
                        : !evidence.ForwardingObserved ? AdGuardSetupDiagnostics.WaitingStatus(traffic, adGuardRequested)
                        : cap.HasValue && !applied ? "Limit geplant · noch kein öffentlicher Download über DYNEX bestätigt"
                        : plan.Reason
                    : wifi is null ? "Warte auf WLAN · kein VPN und keine Begrenzung"
                        : devices.Count == 0 ? "WLAN aktiv · suche DYNEX-PCs" : plan.Reason);
                if (settings.UseAdGuardProxy && wifi is not null && !evidence.ForwardingObserved && adGuardVpnInstalled)
                    status += " · mögliche AdGuard-VPN-Integration prüfen";
                if (peers?.NetworkStatus is { } peerError) status += " · " + peerError;
                MobileState.Publish(new(status, devices.Count > 0, peers is not null, plan.GamingActive,
                    paused, cap.HasValue, applied, cap, download, upload,
                    devices.Select(p => new MobileDeviceSnapshot(p.DeviceName, p.IsGaming ? "Gaming aktiv" : "Verbunden",
                        p.DownloadMbps, p.UploadMbps, p.ShaperActive ? p.AppliedLimitMbps : null)).ToArray(),
                    wifi is not null, conflict, settings.UseAdGuardProxy, true,
                    settings.UseAdGuardProxy && adGuardRequested, settings.UseAdGuardProxy && evidence.ForwardingObserved,
                    transport is not null, plan.SharedCapacityMbps, plan.SharedUploadCapacityMbps, adGuardVpnInstalled));
                ShowNotification(status);
                // On cellular, the proxy only wakes for actual traffic; the controller
                // sleeps until Android reports a network change or the user acts.
                if (peers is null) await changed.WaitAsync(token).ConfigureAwait(false);
                else await changed.WaitAsync(TimeSpan.FromSeconds(4), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogFailure("Runtime", ex);
            terminalError = "DYNEX konnte nicht starten (" + ex.GetType().Name + ")";
        }
        finally
        {
            if (entered)
            {
                try
                {
                    if (adGuardRequested) TryDisableAdGuard();
                    await CleanupAsync(StopTunnelAsync).ConfigureAwait(false);
                    await CleanupAsync(StopProxyAsync).ConfigureAwait(false);
                    await CleanupAsync(StopPeersAsync).ConfigureAwait(false);
                    if (network is not null) { network.Changed -= Wake; network.Dispose(); network = null; }
                    VpnCompatibility.SetOwnVpn(false);
                    MobileState.Publish(MobileSnapshot.Initial with { ConnectionStatusText = terminalError ?? "DYNEX gestoppt" });
                }
                catch (Exception ex) { LogFailure("Shutdown", ex); }
                finally { VpnCompatibility.SetOwnVpn(false); RuntimeGate.Release(); }
            }
            if (terminalError is not null) StopSelf();
        }
    }

    private static async Task CleanupAsync(Func<Task> cleanup)
    {
        try { await cleanup().ConfigureAwait(false); }
        catch (Exception error) { LogFailure("Cleanup", error); }
    }

    private void TryDisableAdGuard()
    {
        try
        {
            if (!adGuardRequested && !MobileSettingsStore.Load(this).UseAdGuardProxy) return;
            if (AdGuardIntegration.HasSavedPassword(this)) AdGuardIntegration.DisableProxy(this);
            adGuardRequested = false;
        }
        catch (Exception error) { LogFailure("AdGuard disconnect request", error); }
    }

    private async Task StopPeersAsync()
    {
        var old = peers; peers = null; peerKey = null;
        if (old is not null) { old.PeersChanged -= Wake; await old.DisposeAsync().ConfigureAwait(false); }
    }

    private async Task StopProxyAsync()
    {
        var old = proxy; proxy = null; proxyKey = null;
        if (old is null) return;
        old.DownloadObserved -= Wake;
        await old.DisposeAsync().ConfigureAwait(false);
        trafficMonitor.Reset();
    }

    private async Task StopTunnelAsync()
    {
        var old = tunnel; tunnel = null;
        if (old is null && !VpnCompatibility.OwnVpnActive) return;
        try { if (old is not null) await old.DisposeAsync().ConfigureAwait(false); }
        finally { VpnCompatibility.SetOwnVpn(false); network?.SetOwnVpn(false); trafficMonitor.Reset(); }
    }

    private void ShowNotification(string text)
    {
        if (lifetime.IsCancellationRequested || text == lastNotification) return;
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        using var channel = new NotificationChannel(ChannelId, "DYNEX-Verbindung", NotificationImportance.Low);
        channel.SetShowBadge(false); manager.CreateNotificationChannel(channel);
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var notification = new Notification.Builder(this, ChannelId)
            .SetSmallIcon(Resource.Drawable.ic_dynex).SetContentTitle("DYNEX")
            .SetContentText(text).SetContentIntent(open).SetOngoing(true).SetOnlyAlertOnce(true)
            .SetCategory(Notification.CategoryService).Build();
        StartForeground(NotificationId, notification);
        lastNotification = text;
    }

    private static void LogFailure(string area, Exception ex) => global::Android.Util.Log.Error("DYNEX", area + ": " + ex.GetType().Name);
}
