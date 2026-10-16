using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Dynex.Mobile;
using global::Android.Net;
using global::Android.OS;

namespace Dynex.Android;

/// <summary>Owns one process-global hev instance, its borrowed TUN FD and its loopback SOCKS proxy.</summary>
internal sealed class NativeTunnelSession : IAsyncDisposable
{
    private static readonly SemaphoreSlim EngineGate = new(1, 1);
    private static bool engineFailed;
    private readonly object sync = new();
    private readonly Action onStopped;
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LocalSocksProxy? proxy;
    private ParcelFileDescriptor? tun;
    private byte[] config = [];
    private Task<int>? native;
    private Task quit = Task.CompletedTask;
    private CancellationTokenRegistration cancellation;
    private bool stopRequested, invoked, exited, quitRequested;
    private int running;

    private NativeTunnelSession(Action onStopped) => this.onStopped = onStopped;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public bool IsRunning => Volatile.Read(ref running) != 0 && !Completion.IsCompleted;
    public ProxySnapshot Snapshot() => proxy?.Snapshot() ?? default;
    public void SetDownloadLimit(long downloadBytesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(downloadBytesPerSecond);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(stopRequested || exited, this);
            proxy!.SetDownloadLimit(downloadBytesPerSecond);
        }
    }

    public static async Task<NativeTunnelSession> StartAsync(VpnService service, WifiConnection connection,
        long downloadBytesPerSecond, Action onStopped, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service); ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(onStopped);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(downloadBytesPerSecond);
        if (!OperatingSystem.IsAndroid()) throw new PlatformNotSupportedException("Der Tunnel benötigt Android.");
        await EngineGate.WaitAsync(ct).ConfigureAwait(false);
        var session = new NativeTunnelSession(onStopped);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (engineFailed) throw new IOException("Der native Tunnel konnte nicht sicher fortgesetzt werden. DYNEX bitte neu starten.");
            // Resolve the packaged ABI before creating any VPN interface. Stats
            // are safe before initialization, but do not establish tunnel readiness.
            NativeStats(out _, out _, out _, out _);
            session.proxy = new LocalSocksProxy(socket =>
            {
                try
                {
                    if (!service.Protect(socket.Handle.ToInt32())) throw new IOException("VPN-Socket-Schutz wurde abgewiesen.");
                    WifiNetworkMonitor.BindSocket(connection.Network, socket);
                }
                catch (Java.Lang.Exception error) { throw new IOException("WLAN-Socket konnte nicht geschützt werden.", error); }
            }, resolveHost: async (host, token) =>
            {
                try
                {
                    return await WifiNetworkMonitor.ResolveAsync(connection.Network, host, token).ConfigureAwait(false);
                }
                catch (Java.Lang.Exception error) { throw new IOException("WLAN-DNS-Auflösung fehlgeschlagen.", error); }
            });
            session.proxy.SetDownloadLimit(downloadBytesPerSecond);
            await session.proxy.StartAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            using (var builder = new VpnService.Builder(service))
            {
                builder.SetSession("DYNEX · Downloadbegrenzung");
                builder.SetMtu(1500);
                builder.SetBlocking(false);
                builder.SetUnderlyingNetworks([connection.Network]);
                builder.AddAddress("198.18.0.1", 32);
                builder.AddAddress("fd00::1", 128);
                // AutoLAN and native SOCKS traffic from our own package must
                // remain outside the VPN; Protect is still mandatory above.
                builder.AddDisallowedApplication(service.PackageName!);
                foreach (var route in PublicInternetRoutes.All) builder.AddRoute(route.Address, route.PrefixLength);
                foreach (var dns in connection.DnsServers)
                    if (IPAddress.TryParse(dns.Split('%')[0], out var address) && !address.IsIPv6LinkLocal &&
                        !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any)) builder.AddDnsServer(address.ToString());
                session.tun = builder.Establish() ?? throw new IOException("Android hat keine VPN-Schnittstelle freigegeben.");
            }
            session.config = Encoding.UTF8.GetBytes($$"""
                tunnel:
                  mtu: 1500
                  ipv4: 198.18.0.1
                  ipv6: 'fd00::1'
                socks5:
                  address: 127.0.0.1
                  port: {{session.proxy.Port}}
                  udp: 'udp'
                  username: '{{session.proxy.Username}}'
                  password: '{{session.proxy.Password}}'
                misc:
                  max-session-count: 128
                  connect-timeout: 10000
                  tcp-read-write-timeout: 300000
                  udp-read-write-timeout: 60000
                  log-file: /dev/null
                  log-level: error
                """);
            session.native = Task.Factory.StartNew(session.RunNative, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            session.cancellation = ct.UnsafeRegister(static state => ((NativeTunnelSession)state!).RequestStop(), session);
            session.Completion = session.CompleteAsync();
            await session.entered.Task.WaitAsync(ct).ConfigureAwait(false);
            if (session.Completion.IsCompleted) await session.Completion.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return session;
        }
        catch
        {
            if (session.native is not null)
            {
                session.RequestStop();
                await session.Completion.ConfigureAwait(false);
            }
            else
            {
                try
                {
                    session.CloseTun();
                    if (session.proxy is not null) await session.proxy.DisposeAsync().ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(session.config); EngineGate.Release(); }
            }
            throw;
        }
    }

    private int RunNative()
    {
        lock (sync)
        {
            if (stopRequested) { entered.TrySetCanceled(); return 0; }
            invoked = true;
            Volatile.Write(ref running, 1);
            entered.TrySetResult();
        }
        try { return NativeMain(config, checked((uint)config.Length), tun!.Fd); }
        finally
        {
            lock (sync) { exited = true; Volatile.Write(ref running, 0); }
            CryptographicOperations.ZeroMemory(config);
        }
    }

    private void RequestStop()
    {
        lock (sync)
        {
            stopRequested = true;
            if (!invoked || exited || quitRequested) return;
            quitRequested = true;
            quit = Task.Run(() =>
            {
                lock (sync)
                    if (!exited) NativeQuit();
            });
        }
    }

    private async Task CompleteAsync()
    {
        Exception? failure = null;
        try
        {
            var result = await native!.ConfigureAwait(false);
            lock (sync)
                if (result != 0 || !stopRequested)
                {
                    engineFailed = true;
                    failure = new IOException($"Der lokale VPN-Tunnel wurde beendet (Code {result}). DYNEX bitte neu starten.");
                }
            await quit.ConfigureAwait(false);
        }
        catch (Exception error) { engineFailed = true; failure = error; }
        finally
        {
            // Externally supplied FDs are borrowed by hev2.19.0. Close only
            // after main has returned; never detach/close a descriptor in flight.
            cancellation.Dispose();
            try { CloseTun(); }
            catch (Exception error) { failure ??= error; }
            try { if (proxy is not null) await proxy.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
            finally
            {
                CryptographicOperations.ZeroMemory(config);
                EngineGate.Release();
                try { onStopped(); } catch { }
            }
        }
        if (failure is not null) throw failure;
    }

    private void CloseTun()
    {
        var descriptor = Interlocked.Exchange(ref tun, null);
        if (descriptor is null) return;
        try { descriptor.Close(); }
        finally { descriptor.Dispose(); }
    }

    public ValueTask DisposeAsync()
    {
        RequestStop();
        return new ValueTask(Completion);
    }

    [DllImport("hev-socks5-tunnel", EntryPoint = "hev_socks5_tunnel_main_from_str", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int NativeMain(byte[] configuration, uint length, int tunFd);
    [DllImport("hev-socks5-tunnel", EntryPoint = "hev_socks5_tunnel_quit", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void NativeQuit();
    [DllImport("hev-socks5-tunnel", EntryPoint = "hev_socks5_tunnel_stats", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void NativeStats(out nuint txPackets, out nuint txBytes, out nuint rxPackets, out nuint rxBytes);
}
