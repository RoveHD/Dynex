[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$releaseTag = 'v3.6.2'
$assetName = 'Windows.Packet.Filter.3.6.2.1.x64.msi'
$assetUrl = "https://github.com/wiresock/ndisapi/releases/download/$releaseTag/$assetName"
$apiUrl = "https://api.github.com/repos/wiresock/ndisapi/releases/tags/$releaseTag"
$pinnedSha256 = '9c388c0b7f189f7fa98720bae2caecf7d64f30910838b80b438ecf8956b8502c'
$maxBytes = 20MB
$expectedBytes = 819200

$destinationPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\dependencies\windows-packet-filter'))
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\') + '\'
$ownedRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\dependencies\windows-packet-filter')).TrimEnd('\')
if (-not $destinationPath.Equals($ownedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Interner Zielpfad stimmt nicht mit dem vorgesehenen Provenance-Ordner überein.' }

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$temporaryDirectory = Join-Path $tempRoot ('Dynex-PacketFilter-' + [Guid]::NewGuid().ToString('N'))
$temporaryResolved = [IO.Path]::GetFullPath($temporaryDirectory)
if (-not $temporaryResolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Temporärer Ordner liegt außerhalb des erwarteten Verzeichnisses.'
}
$null = New-Item -ItemType Directory -Path $temporaryResolved

try {
    Write-Host "Prüfe GitHub-Release-API $apiUrl"
    $release = Invoke-RestMethod -Uri $apiUrl -Headers @{ 'User-Agent' = 'DYNEX-packet-filter-provenance' } -MaximumRedirection 3
    if ($release.tag_name -ne $releaseTag) { throw "Unerwartetes Release-Tag: $($release.tag_name)" }
    $asset = @($release.assets | Where-Object name -eq $assetName)
    if ($asset.Count -ne 1) { throw "Release enthält nicht genau ein Asset $assetName." }
    if ($asset[0].browser_download_url -ne $assetUrl) { throw 'Die Downloadadresse entspricht nicht der festgelegten offiziellen Asset-URL.' }
    if ($asset[0].size -ne $expectedBytes -or $asset[0].size -gt $maxBytes) { throw "Unerwartete MSI-Größe laut Release-API: $($asset[0].size)" }
    if ($asset[0].digest -ne "sha256:$pinnedSha256") { throw "GitHub-API-Digest stimmt nicht mit dem geprüften Pin überein: $($asset[0].digest)" }

    $msiPath = Join-Path $temporaryResolved $assetName
    Write-Host "Lade das offizielle MSI; der Installer und Treiber werden nicht ausgeführt."
    Invoke-WebRequest -Uri $assetUrl -OutFile $msiPath -MaximumRedirection 3
    $file = Get-Item -LiteralPath $msiPath
    if ($file.Length -gt $maxBytes -or $file.Length -ne $expectedBytes) { throw "Unerwartete Downloadgröße: $($file.Length) Bytes" }
    $actualHash = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $pinnedSha256) { throw "SHA256 mismatch: erwartet $pinnedSha256, erhalten $actualHash" }

    $signature = Get-AuthenticodeSignature -LiteralPath $msiPath
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
        throw "Windows bestätigt die MSI-Authenticode-Signatur nicht: $($signature.Status). MSI wird nicht gespeichert."
    }

    $null = New-Item -ItemType Directory -Path $destinationPath -Force
    $finalMsi = Join-Path $destinationPath $assetName
    if (Test-Path -LiteralPath $finalMsi) { throw "Zieldatei existiert bereits; sie wird nicht überschrieben: $finalMsi" }
    Copy-Item -LiteralPath $msiPath -Destination $finalMsi
    $savedHash = (Get-FileHash -LiteralPath $finalMsi -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($savedHash -ne $pinnedSha256) { throw 'Hashabweichung nach dem Kopieren; gespeicherte Datei bleibt ungeprüft.' }

    @"
Windows Packet Filter x64 MSI provenance
Release tag: $releaseTag
Release API: $apiUrl
Asset: $assetName
Official vendor download page: https://www.ntkernel.com/windows-packet-filter/download/
Asset URL: $assetUrl
GitHub Release API asset digest: $($asset[0].digest)
Pinned SHA-256: $pinnedSha256
Downloaded bytes: $($file.Length)
Authenticode status: $($signature.Status)
Authenticode signer: $($signature.SignerCertificate.Subject)
Authenticode thumbprint: $($signature.SignerCertificate.Thumbprint)
Fetched UTC: $([DateTimeOffset]::UtcNow.ToString('O'))

This fetch operation downloads the MSI and verifies its hash and Authenticode signature. It does not call msiexec or extract/execute package content. Any later read-only inspection/extraction must be recorded separately in INSPECTION.txt. Driver activation and system configuration were not attempted.
Personal/evaluation use is free under vendor terms. Commercial redistribution requires the vendor's commercial license; do not bundle this MSI or its contents into DYNEX without that license.
"@ | Set-Content -LiteralPath (Join-Path $destinationPath 'PROVENANCE.txt') -Encoding utf8
    Write-Host "MSI gespeichert und geprüft: $finalMsi"
}
catch {
    throw "Windows Packet Filter konnte nicht sicher bereitgestellt werden. Keine Sicherheitsausnahmen wurden geändert. Ursache: $($_.Exception.Message)"
}
finally {
    if ($temporaryResolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $temporaryResolved)) {
        Remove-Item -LiteralPath $temporaryResolved -Recurse -Force
    }
}
