[CmdletBinding()]
param(
    # There is no upstream release-API digest for this asset. Obtain this SHA256
    # through an independently trusted, reviewed distribution/build channel.
    # Never use the hash of the download in this same run as its trust anchor.
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$ExpectedSha256,
    [string]$Destination = (Join-Path $PSScriptRoot '..\src\Dynex.Shaper\native')
)

$ErrorActionPreference = 'Stop'
$archiveUrl = 'https://github.com/basil00/WinDivert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip'
$sourceUrl = 'https://github.com/basil00/WinDivert/tree/v2.2.2'
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('Dynex-WinDivert-' + [Guid]::NewGuid().ToString('N'))
$temporaryResolved = [IO.Path]::GetFullPath($temporaryDirectory)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
if (-not $temporaryResolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Temporärer Ordner liegt außerhalb des erwarteten Verzeichnisses.'
}
$null = New-Item -ItemType Directory -Path $temporaryDirectory
try {
    $archive = Join-Path $temporaryDirectory 'WinDivert-2.2.2-A.zip'
    Write-Host 'Lade offizielles WinDivert 2.2.2-A; kein Treiber wird gestartet.'
    Invoke-WebRequest -Uri $archiveUrl -OutFile $archive
    $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($actualHash -ne $ExpectedSha256) {
        throw "SHA256 stimmt nicht mit dem vorab festgelegten Trustanker überein. Erwartet $ExpectedSha256, erhalten $actualHash."
    }

    # Read only the exact expected entries; do not extract executable archive paths.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $files = @{
            'WinDivert-2.2.2-A/x64/WinDivert.dll' = 'WinDivert.dll'
            'WinDivert-2.2.2-A/x64/WinDivert64.sys' = 'WinDivert64.sys'
            'WinDivert-2.2.2-A/LICENSE' = 'LICENSE-WinDivert.txt'
        }
        foreach ($entryName in $files.Keys) {
            $entry = $zip.GetEntry($entryName)
            if ($null -eq $entry -or $entry.Length -gt 16MB) { throw "Archivinhalt fehlt oder ist unerwartet: $entryName" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $temporaryDirectory $files[$entryName]), $false)
        }
    }
    finally { $zip.Dispose() }

    $driver = Join-Path $temporaryDirectory 'WinDivert64.sys'
    $signature = Get-AuthenticodeSignature -LiteralPath $driver
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
        throw "Windows bestätigt die offizielle Treibersignatur nicht: $($signature.Status). Kein Treiber wurde übernommen."
    }
    # The upstream DLL may be unsigned. If a signature exists, it must validate.
    $dllSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $temporaryDirectory 'WinDivert.dll')
    if ($dllSignature.Status -notin @('Valid', 'NotSigned')) {
        throw "Ungültige DLL-Signatur: $($dllSignature.Status)."
    }

    $destinationResolved = [IO.Path]::GetFullPath($Destination)
    $null = New-Item -ItemType Directory -Path $destinationResolved -Force
    foreach ($name in $files.Values) {
        Copy-Item -LiteralPath (Join-Path $temporaryDirectory $name) -Destination (Join-Path $destinationResolved $name)
    }
    @"
WinDivert 2.2.2-A, Basil and contributors
Official binary: $archiveUrl
Corresponding source: $sourceUrl
Archive SHA256 trust anchor supplied before download: $ExpectedSha256
Driver signer: $($signature.SignerCertificate.Subject)
Driver signer thumbprint: $($signature.SignerCertificate.Thumbprint)
License: LGPL-3.0-or-later option; complete upstream LICENSE-WinDivert.txt enclosed.
No source changes. Dynamically loaded; compatible library replacement is permitted.
Distributors must fulfill source-distribution obligations before redistribution.
"@ | Set-Content -LiteralPath (Join-Path $destinationResolved 'WIN-DIVERT-NOTICE.txt') -Encoding utf8
    Write-Host "WinDivert geprüft und gespeichert: $destinationResolved"
}
catch {
    throw "WinDivert konnte nicht bereitgestellt werden. Schutz bleibt nicht bereit. Keine Sicherheitsausnahmen hinzufügen. Ursache: $($_.Exception.Message)"
}
finally {
    # Only remove the fresh GUID directory after an absolute containment check.
    if ($temporaryResolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $temporaryResolved)) {
        Remove-Item -LiteralPath $temporaryResolved -Recurse -Force
    }
}
