param([Parameter(Mandatory = $true)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
    $env:GITHUB_RUN_ID -notmatch '^\d+$' -or -not $env:GITHUB_WORKSPACE) {
    throw 'This system-service integration test is restricted to disposable GitHub-hosted Windows runners. Never run it on a local PC.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The hosted runner must already be elevated. This test never requests UAC.'
    }
} finally { $identity.Dispose() }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $repository.Equals([IO.Path]::GetFullPath($env:GITHUB_WORKSPACE), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The test repository must be the GitHub workspace.'
}
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$artifactRoot = Join-Path $repository 'artifacts'
if (-not $publish.StartsWith(([IO.Path]::GetFullPath($artifactRoot).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Only the freshly built repository package may supply the service payload.'
}
$manifest = Join-Path $publish '.helper-manifest.json'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf) -or -not (Test-Path -LiteralPath (Join-Path $publish 'shaper/Dynex.Shaper.exe') -PathType Leaf)) {
    throw 'Build the complete Windows package before the service integration test.'
}
$fixture = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/DYNEX'))
$protectedParent = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'DYNEX'))
$receipt = 'HKLM:\SOFTWARE\DYNEX\Prioritization'
$serviceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\DYNEX.Prioritization'
if ((Test-Path -LiteralPath $fixture) -or (Test-Path -LiteralPath $protectedParent) -or (Test-Path -LiteralPath $receipt) -or
    (Test-Path -LiteralPath $serviceKey) -or (Get-Service -Name 'DYNEX.Prioritization' -ErrorAction SilentlyContinue)) {
    throw 'A preexisting DYNEX app/service/receipt/protected directory was found. Nothing was changed.'
}
$runDirectory = Join-Path $artifactRoot ('service-lifecycle-smoke/' + [Guid]::NewGuid().ToString('N'))
$buildDirectory = Join-Path $runDirectory 'fixture-build'
New-Item -ItemType Directory -Path $runDirectory | Out-Null
$report = Join-Path $runDirectory 'result.json'
$stdout = Join-Path $runDirectory 'stdout.log'
$stderr = Join-Path $runDirectory 'stderr.log'
$project = Join-Path $PSScriptRoot 'Dynex.Service.Tests/Dynex.Service.Tests.csproj'
& dotnet publish $project -c Release -r win-x64 --self-contained false -o $buildDirectory "-p:PrioritizationPayloadManifest=$manifest"
if ($LASTEXITCODE -ne 0) { throw 'The real service lifecycle fixture did not build.' }
$fixtureCreated = $false
try {
    if (Test-Path -LiteralPath $fixture) { throw 'Fixture path appeared during build; refusing to overwrite it.' }
    New-Item -ItemType Directory -Path $fixture | Out-Null
    $fixtureCreated = $true
    [IO.File]::WriteAllText((Join-Path $fixture '.github-service-fixture'), $env:GITHUB_RUN_ID)
    Get-ChildItem -LiteralPath $buildDirectory | Copy-Item -Destination $fixture -Recurse
    $testHost = Join-Path $fixture 'Dynex.Service.Tests.exe'
    $application = Join-Path $fixture 'DYNEX.exe'
    Copy-Item -LiteralPath $testHost -Destination $application -Force
    Copy-Item -LiteralPath (Join-Path $publish 'shaper') -Destination (Join-Path $fixture 'shaper') -Recurse
    $test = Start-Process -FilePath $application -ArgumentList @('--github-service-smoke', ('"' + $report + '"')) -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout }
    if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr }
    if ($test.ExitCode -ne 0) { throw "Real SYSTEM service lifecycle failed with exit code $($test.ExitCode). See $runDirectory" }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not $result.success -or -not $result.removed) { throw 'The service lifecycle did not report successful removal.' }
    Write-Host "Service lifecycle report: $report"
} finally {
    if ($fixtureCreated -and (Test-Path -LiteralPath $fixture)) {
        $expectedFixture = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/DYNEX'))
        $item = Get-Item -LiteralPath $fixture
        if (-not $item.FullName.Equals($expectedFixture, [StringComparison]::OrdinalIgnoreCase) -or
            (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or
            [IO.File]::ReadAllText((Join-Path $fixture '.github-service-fixture')) -ne $env:GITHUB_RUN_ID) {
            throw 'Fixture cleanup ownership check failed; directory was preserved.'
        }
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
    # Production removal preserves the shared parent. Remove only this empty test-created parent.
    if (Test-Path -LiteralPath $protectedParent) {
        $item = Get-Item -LiteralPath $protectedParent
        $expectedParent = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'DYNEX'))
        if ($item.FullName.Equals($expectedParent, [StringComparison]::OrdinalIgnoreCase) -and
            (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) -and
            -not (Get-ChildItem -LiteralPath $protectedParent -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $protectedParent
        }
    }
}
