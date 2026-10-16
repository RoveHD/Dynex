[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$AndroidSdkDirectory = '.tools\android-sdk',
    [string]$JavaSdkDirectory = '.tools\jdk',
    [string]$OutputDirectory = 'artifacts\android',
    [string]$SigningKeyStore,
    [string]$SigningKeyAlias,
    [string]$SigningStorePasswordReference,
    [string]$SigningKeyPasswordReference
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repositoryRoot 'src\Dynex.Android\Dynex.Android.csproj'
$androidProjectXml = [xml](Get-Content -LiteralPath $project -Raw)
$androidVersion = [string](@($androidProjectXml.Project.PropertyGroup.Version | Where-Object { $_ })[0])
if ($androidVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Ungültige Android-Projektversion: $androidVersion" }
$sdkPath = if ([IO.Path]::IsPathRooted($AndroidSdkDirectory)) { [IO.Path]::GetFullPath($AndroidSdkDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repositoryRoot $AndroidSdkDirectory)) }
$jdkPath = if ([IO.Path]::IsPathRooted($JavaSdkDirectory)) { [IO.Path]::GetFullPath($JavaSdkDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repositoryRoot $JavaSdkDirectory)) }
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory)) }

$hasAnySigningInput = @($SigningKeyStore, $SigningKeyAlias, $SigningStorePasswordReference, $SigningKeyPasswordReference) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1
$useReleaseSigning = $null -ne $hasAnySigningInput
if ($useReleaseSigning) {
    if ($Configuration -ne 'Release') { throw 'Keystore-Signierung ist nur für Configuration Release zulässig.' }
    if ([string]::IsNullOrWhiteSpace($SigningKeyStore) -or
        [string]::IsNullOrWhiteSpace($SigningKeyAlias) -or
        [string]::IsNullOrWhiteSpace($SigningStorePasswordReference) -or
        [string]::IsNullOrWhiteSpace($SigningKeyPasswordReference)) {
        throw 'Für Release-Signierung müssen Keystore, Alias sowie Store- und Key-Passwortreferenz gemeinsam angegeben werden.'
    }
    if ($SigningKeyStore.Contains(';') -or $SigningKeyAlias.Contains(';') -or
        $SigningKeyStore.Contains("`n") -or $SigningKeyStore.Contains("`r") -or
        $SigningKeyAlias.Contains("`n") -or $SigningKeyAlias.Contains("`r")) {
        throw 'Keystore-Pfad und Alias dürfen keine Semikolons oder Zeilenumbrüche enthalten.'
    }

    $SigningKeyStore = if ([IO.Path]::IsPathRooted($SigningKeyStore)) { [IO.Path]::GetFullPath($SigningKeyStore) } else { [IO.Path]::GetFullPath((Join-Path $repositoryRoot $SigningKeyStore)) }
    if (-not (Test-Path -LiteralPath $SigningKeyStore -PathType Leaf)) { throw "Keystore nicht gefunden: $SigningKeyStore" }

    $resolvedPasswordReferences = @()
    foreach ($reference in @($SigningStorePasswordReference, $SigningKeyPasswordReference)) {
        if ($reference -match '^env:([A-Za-z_][A-Za-z0-9_]*)$') {
            $environmentValue = [Environment]::GetEnvironmentVariable($Matches[1])
            if ($null -eq $environmentValue) { throw "Die referenzierte Umgebungsvariable '$($Matches[1])' ist nicht gesetzt." }
            $environmentValue = $null
            $resolvedPasswordReferences += $reference
        }
        elseif ($reference -match '^file:(.+)$') {
            $passwordFile = $Matches[1]
            if ($passwordFile.Contains(';') -or $passwordFile.Contains("`n") -or $passwordFile.Contains("`r")) {
                throw 'Passwortdateipfad darf keine Semikolons oder Zeilenumbrüche enthalten.'
            }
            $passwordFile = if ([IO.Path]::IsPathRooted($passwordFile)) { [IO.Path]::GetFullPath($passwordFile) } else { [IO.Path]::GetFullPath((Join-Path $repositoryRoot $passwordFile)) }
            if (-not (Test-Path -LiteralPath $passwordFile -PathType Leaf)) { throw "Passwortdatei aus Referenz nicht gefunden: $passwordFile" }
            $resolvedPasswordReferences += "file:$passwordFile"
        }
        else {
            throw 'Passwortparameter müssen MSBuild-Referenzen im Format env:NAME oder file:PFAD sein; Passwortwerte sind nicht zulässig.'
        }
    }
    $SigningStorePasswordReference = $resolvedPasswordReferences[0]
    $SigningKeyPasswordReference = $resolvedPasswordReferences[1]
    if ($SigningStorePasswordReference.StartsWith('file:') -and
        $SigningStorePasswordReference.Equals($SigningKeyPasswordReference, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Für Store- und Key-Passwort zwei getrennte Dateien verwenden (auch bei gleichem Passwort) oder env:NAME. apksigner liest dieselbe Datei sonst zweimal nacheinander.'
    }
}

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Android-Projekt fehlt: $project" }
if (-not (Test-Path -LiteralPath $sdkPath -PathType Container)) { throw "Android SDK nicht gefunden: $sdkPath" }
if (-not (Test-Path -LiteralPath (Join-Path $jdkPath 'bin\java.exe') -PathType Leaf)) { throw "JDK 17 nicht gefunden: $jdkPath" }

Write-Host "Baue DYNEX Android ($Configuration) mit lokalem SDK/JDK; MSBuild läuft seriell (-m:1)."
$buildArguments = @(
    $project,
    '--configuration', $Configuration,
    '-m:1',
    "-p:AndroidSdkDirectory=$sdkPath",
    "-p:JavaSdkDirectory=$jdkPath",
    '-p:AndroidPackageFormats=apk',
    '-p:EmbedAssembliesIntoApk=true'
)
if ($useReleaseSigning) {
    $buildArguments += @(
        '-p:AndroidKeyStore=true',
        "-p:AndroidSigningKeyStore=$SigningKeyStore",
        "-p:AndroidSigningKeyAlias=$SigningKeyAlias",
        "-p:AndroidSigningStorePass=$SigningStorePasswordReference",
        "-p:AndroidSigningKeyPass=$SigningKeyPasswordReference"
    )
}
& dotnet build @buildArguments
if ($LASTEXITCODE -ne 0) { throw "dotnet build fehlgeschlagen (Exitcode $LASTEXITCODE)." }

$configurationOutput = Join-Path $repositoryRoot "src\Dynex.Android\bin\$Configuration\net9.0-android"
$signedApk = Get-ChildItem -LiteralPath $configurationOutput -Filter '*-Signed.apk' -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($null -eq $signedApk) { throw "Build war erfolgreich, aber keine signierte Test-APK (*-Signed.apk) gefunden: $configurationOutput" }

$null = New-Item -ItemType Directory -Path $outputPath -Force
$artifactName = if ($useReleaseSigning) { "DYNEX-Android-$androidVersion.apk" } else { "DYNEX-Android-$androidVersion-$Configuration-TESTSIGNATURE.apk" }
$destination = Join-Path $outputPath $artifactName
Copy-Item -LiteralPath $signedApk.FullName -Destination $destination -Force
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
Write-Host "APK kopiert: $destination"
Write-Host "SHA-256: $hash"
if ($useReleaseSigning) {
    Set-Content -LiteralPath "$destination.sha256" -Value "$hash  $([IO.Path]::GetFileName($destination))" -Encoding ascii
    Write-Host "SHA-256-Datei: $destination.sha256"
    Write-Warning 'APK mit dem angegebenen Keystore signiert. Keine Veröffentlichung oder Store-Freigabe wurde durchgeführt.'
}
else {
    Write-Warning 'Dies ist ein lokales Testartefakt. Die Signatur ist keine Store-/Produktionssignatur.'
}
