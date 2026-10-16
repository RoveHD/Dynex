[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dependencyRoot = Join-Path $repositoryRoot 'artifacts\android-dependencies'
$archivePath = Join-Path $dependencyRoot 'hev-socks5-tunnel.aar'
$expectedSha256 = 'FBBF014C1C4C6D1E0922DB88542ABD9DC726600473BDFDC8039ED157B4BA02B0'
$archiveUrl = 'https://github.com/heiher/hev-socks5-tunnel/releases/download/2.19.0/hev-socks5-tunnel.aar'
$nativeRoot = Join-Path $dependencyRoot 'native'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('Dynex-Android-Prepare-' + [Guid]::NewGuid().ToString('N'))
$temporaryResolved = [IO.Path]::GetFullPath($temporaryDirectory)

if (-not $temporaryResolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Temporärer Ordner liegt außerhalb des Temp-Verzeichnisses.'
}

$null = New-Item -ItemType Directory -Path $dependencyRoot -Force
$downloadedArchive = $false
if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
    $existingHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if ($existingHash -ne $expectedSha256) {
        throw "Vorhandenes AAR hat einen unerwarteten SHA-256-Hash ($existingHash). Es wird nicht verwendet oder überschrieben. Datei bitte prüfen/entfernen und erneut ausführen."
    }
    Write-Host 'Verwende vorhandenes, hashgeprüftes hev-socks5-tunnel 2.19.0 AAR.'
}
else {
    $null = New-Item -ItemType Directory -Path $temporaryResolved
    $downloadPath = Join-Path $temporaryResolved 'hev-socks5-tunnel.aar'
    try {
        Write-Host "Lade offizielles Upstream-AAR: $archiveUrl"
        Invoke-WebRequest -Uri $archiveUrl -OutFile $downloadPath
        $downloadHash = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash
        if ($downloadHash -ne $expectedSha256) {
            throw "AAR-SHA256 stimmt nicht. Erwartet $expectedSha256, erhalten $downloadHash."
        }
        Copy-Item -LiteralPath $downloadPath -Destination $archivePath
        $downloadedArchive = $true
    }
    finally {
        if ($temporaryResolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $temporaryResolved)) {
            Remove-Item -LiteralPath $temporaryResolved -Recurse -Force
        }
    }
}

$verifiedHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
if ($verifiedHash -ne $expectedSha256) { throw 'AAR-Hashprüfung nach Bereitstellung fehlgeschlagen.' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$null = New-Item -ItemType Directory -Path $temporaryResolved
$extracted = @()
try {
    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $expectedEntries = @{
            'jni/arm64-v8a/libhev-socks5-tunnel.so' = 'arm64-v8a'
            'jni/x86_64/libhev-socks5-tunnel.so' = 'x86_64'
        }
        foreach ($entryName in $expectedEntries.Keys) {
            $entry = $zip.GetEntry($entryName)
            if ($null -eq $entry -or $entry.Length -lt 4096 -or $entry.Length -gt 32MB) {
                throw "Erwartete native Bibliothek fehlt oder hat eine unplausible Größe: $entryName"
            }
            $targetDirectory = Join-Path $temporaryResolved $expectedEntries[$entryName]
            $null = New-Item -ItemType Directory -Path $targetDirectory -Force
            $target = Join-Path $targetDirectory 'libhev-socks5-tunnel.so'
            $archiveInputStream = $entry.Open()
            $nativeOutputStream = [IO.File]::Create($target)
            try { $archiveInputStream.CopyTo($nativeOutputStream) }
            finally { $nativeOutputStream.Dispose(); $archiveInputStream.Dispose() }
            $extracted += [pscustomobject]@{ Abi = $expectedEntries[$entryName]; Path = $target }
        }
    }
    finally { $zip.Dispose() }

    foreach ($item in $extracted) {
        $destinationDirectory = Join-Path $nativeRoot $item.Abi
        $null = New-Item -ItemType Directory -Path $destinationDirectory -Force
        Copy-Item -LiteralPath $item.Path -Destination (Join-Path $destinationDirectory 'libhev-socks5-tunnel.so') -Force
        $size = (Get-Item -LiteralPath (Join-Path $destinationDirectory 'libhev-socks5-tunnel.so')).Length
        if ($size -lt 4096) { throw "Native Bibliothek nach dem Kopieren unvollständig: $($item.Abi)" }
    }
}
finally {
    if ($temporaryResolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $temporaryResolved)) {
        Remove-Item -LiteralPath $temporaryResolved -Recurse -Force
    }
}

$origin = if ($downloadedArchive) { 'neu heruntergeladen' } else { 'vorhanden und verifiziert' }
Write-Host "Android-Native-Abhängigkeit bereit ($origin); SHA-256 $verifiedHash."
Write-Host "Extrahiert: $nativeRoot\arm64-v8a und $nativeRoot\x86_64"
