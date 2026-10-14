$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$qa = Join-Path $taskRoot ('artifacts/ping-investigation-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $qa | Out-Null
$app = Join-Path $env:LOCALAPPDATA 'Programs/DYNEX/DYNEX.exe'
$settings = Join-Path $env:LOCALAPPDATA 'DYNEX/settings.json'
$settingsHash = (Get-FileHash -LiteralPath $settings).Hash
$wasRunning = @(Get-Process DYNEX -ErrorAction SilentlyContinue).Count -gt 0
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
public class DynexPingProbe {
    public string Target; public string Status; public long? Rtt; public double ElapsedMs;
    public static async Task<DynexPingProbe> Send(string target) {
        var clock = Stopwatch.StartNew();
        using(var ping = new Ping()) {
            try {
                var reply = await ping.SendPingAsync(target, 900);
                return new DynexPingProbe { Target=target, Status=reply.Status.ToString(), Rtt=reply.Status==IPStatus.Success?(long?)reply.RoundtripTime:null, ElapsedMs=clock.Elapsed.TotalMilliseconds };
            } catch(Exception ex) { return new DynexPingProbe { Target=target, Status=ex.GetType().Name+": "+ex.Message, ElapsedMs=clock.Elapsed.TotalMilliseconds }; }
        }
    }
    public static Task<DynexPingProbe[]> Pair() { return Task.WhenAll(Send("192.168.1.1"),Send("1.1.1.1")); }
}
'@
function Measure-Phase([string]$name) {
    $rows = @()
    $nic = [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object Name -eq 'Ethernet 3' | Select-Object -First 1
    $before = if ($nic) { $nic.GetIPStatistics() } else { $null }
    $helperBefore = Get-Process Dynex.Shaper -ErrorAction SilentlyContinue | Select-Object Id,CPU
    $phaseClock = [Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt 20; $i++) {
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $task = [DynexPingProbe]::Pair()
        $probes = $task.GetAwaiter().GetResult()
        foreach ($probe in $probes) {
            $rows += [pscustomobject]@{ Phase=$name; Index=$i; Utc=[DateTimeOffset]::UtcNow.ToString('o'); Target=$probe.Target; Status=$probe.Status; Rtt=$probe.Rtt; ElapsedMs=$probe.ElapsedMs }
        }
        $remaining = 1000 - $clock.ElapsedMilliseconds
        if ($remaining -gt 0) { Start-Sleep -Milliseconds $remaining }
    }
    $after = if ($nic) { $nic.GetIPStatistics() } else { $null }
    $helperAfter = Get-Process Dynex.Shaper -ErrorAction SilentlyContinue | Select-Object Id,CPU
    $rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $qa "$name.json")
    $summary = [pscustomobject]@{
        Phase=$name; Seconds=$phaseClock.Elapsed.TotalSeconds
        DownloadMbps=$(if ($before) { ($after.BytesReceived-$before.BytesReceived)*8/$phaseClock.Elapsed.TotalSeconds/1000000 } else { $null })
        UploadMbps=$(if ($before) { ($after.BytesSent-$before.BytesSent)*8/$phaseClock.Elapsed.TotalSeconds/1000000 } else { $null })
        HelperBefore=$helperBefore; HelperAfter=$helperAfter
        Targets=@($rows | Group-Object Target | ForEach-Object { [pscustomobject]@{ Target=$_.Name; Sent=$_.Count; Success=@($_.Group | Where-Object Status -eq Success).Count; AverageMs=($_.Group | Where-Object Status -eq Success | Measure-Object Rtt -Average).Average } })
    }
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $qa "$name-summary.json")
    $summary | ConvertTo-Json -Depth 5 -Compress | Write-Output
}
Write-Output "Output: $qa"
try {
    Measure-Phase 'filter-active-before'
    $stopper = Start-Process -FilePath $app -ArgumentList '--shutdown' -WindowStyle Hidden -PassThru
    if (-not $stopper.WaitForExit(22000) -or $stopper.ExitCode -ne 0) { throw 'DYNEX did not confirm shutdown.' }
    if (Get-Process Dynex.Shaper -ErrorAction SilentlyContinue) { throw 'Helper remains active; refusing to call this a filter-off measurement.' }
    Measure-Phase 'filter-stopped'
}
finally {
    if ($wasRunning -and -not (Get-Process DYNEX -ErrorAction SilentlyContinue)) {
        Start-Process -FilePath $app -ArgumentList @('--verify-live', (Join-Path $qa 'restarted-live')) -WindowStyle Hidden | Out-Null
    }
    if ((Get-FileHash -LiteralPath $settings).Hash -ne $settingsHash) { Write-Warning 'Settings changed during observation; no settings were written by this script.' }
}
Measure-Phase 'filter-restarted'
