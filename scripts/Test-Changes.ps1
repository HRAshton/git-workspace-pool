# Run the Windows PR gates locally before a milestone push.
[CmdletBinding()]
param(
    [switch] $IncludeSlow,
    [switch] $IncludePackage,
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) {
    throw 'Run this validation on Windows with PowerShell 7; it includes Windows process and installer gates.'
}
if ($IncludePackage -and [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    throw 'Supply a new -OutputDirectory for the RC2 package and disposable smoke fixtures.'
}
if ($OutputDirectory) {
    $OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $OutputDirectory) {
        throw 'Validation refuses an existing output directory.'
    }
}

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tests = 'tests/GitWorkspacePool.Tests/GitWorkspacePool.Tests.csproj'

function Invoke-Checked {
    param([string] $Command, [string[]] $Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE"
    }
}

Push-Location $root
try {
    Invoke-Checked git @('diff', '--check')
    Invoke-Checked node @('--check', 'userscript/git-workspace-pool.user.js')
    # Parse every script, including packaging and negative test fixtures, before a build.
    foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
        $tokens = $null
        $parseErrors = $null
        $null = [System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref] $tokens, [ref] $parseErrors)
        if ($parseErrors.Count -gt 0) {
            throw "PowerShell parse failed for $($script.Name): $($parseErrors.Message -join '; ')"
        }
    }
    Invoke-Checked dotnet @('restore', 'GitWorkspacePool.slnx')
    Invoke-Checked dotnet @('build', 'GitWorkspacePool.slnx', '--configuration', 'Release', '--no-restore', '-p:ContinuousIntegrationBuild=true')
    $filters = @(
        'Category!=FastE2E&Category!=SlowE2E',
        'Category=FastE2E',
        'FullyQualifiedName~Release_RealProcessKillAtBothDurableBoundariesNeverLosesParkedUserWork|FullyQualifiedName~ReadySlot_RealProcessKillAtEachBoundaryPreservesBothOwners'
    )
    if ($IncludeSlow) {
        # SlowE2E already contains the focused handoff kill tests.
        $filters = @($filters[0], $filters[1], 'Category=SlowE2E')
    }
    foreach ($filter in $filters) {
        Invoke-Checked dotnet @('test', $tests, '--configuration', 'Release', '--no-build', '--no-restore', '--filter', $filter)
    }
    if ($IncludePackage) {
        $output = [System.IO.Path]::GetFullPath($OutputDirectory)
        $packageRoot = Join-Path $output 'artifacts'
        & "$PSScriptRoot/Test-RC2Prerequisites-Fixture.ps1" -TestRoot (Join-Path $output 'prerequisites')
        & "$PSScriptRoot/Build-WindowsPackage.ps1" -OutputDirectory $packageRoot -PackageBaseName 'GitWorkspacePool-win-x64-0.1.0-rc.2' -PackageVersion '0.1.0-rc.2' -ReleaseDocsPrefix 'RC2'
        & "$PSScriptRoot/Test-WindowsPackage.ps1" -PackagePath (Join-Path $packageRoot 'GitWorkspacePool-win-x64-0.1.0-rc.2.zip') -TestRoot (Join-Path $output 'install') -RequiredDocs @('RC2_RELEASE_NOTES.md', 'RC2_ACCEPTANCE.md', 'RECOVERY.md', 'Test-RC2Prerequisites.ps1')
        & "$PSScriptRoot/Test-Backup-PerUserState.ps1" -PackageDirectory (Join-Path $packageRoot 'package') -TestRoot (Join-Path $output 'backup')
    }
    Write-Host 'Requested local validation passed. Installed browser/editor acceptance remains separate.'
}
finally {
    Pop-Location
}
