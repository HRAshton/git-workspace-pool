# Manual, opt-in benchmark. Runs only against disposable real-Git E2E fixtures
# and (optionally) copies manifests of a trusted standalone npm project.
# Never deletes source files or existing benchmark outputs.
[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [ValidateRange(2, 20)]
    [int] $Iterations = 5,
    [string] $NpmProjectPath,
    [switch] $SkipGitBenchmark,
    [switch] $SkipBuild,
    [switch] $ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($ValidateOnly) {
    Write-Host 'Warm reuse benchmark script parsed successfully. No work was performed.'
    return
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    throw 'Supply -OutputDirectory pointing at a NEW, empty-location name.'
}
if ($SkipGitBenchmark -and [string]::IsNullOrWhiteSpace($NpmProjectPath)) {
    throw 'At least the Git fixture or a trusted npm project must be measured.'
}

$output = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    throw "Benchmark refuses to overwrite an existing output directory: $output"
}

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not [string]::IsNullOrWhiteSpace($NpmProjectPath)) {
    $npmSource = [System.IO.Path]::GetFullPath($NpmProjectPath)
    if (-not (Test-Path -LiteralPath $npmSource -PathType Container)) {
        throw "The trusted npm project does not exist: $npmSource"
    }
    $prefix = $npmSource.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if ($output.Equals($npmSource, [StringComparison]::OrdinalIgnoreCase) -or
        $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Keep benchmark output OUTSIDE the trusted npm project.'
    }
    foreach ($name in @('package.json', 'package-lock.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $npmSource $name) -PathType Leaf)) {
            throw "Missing $name in the trusted npm project."
        }
    }

    $manifest = Get-Content -LiteralPath (Join-Path $npmSource 'package.json') -Raw |
        ConvertFrom-Json -AsHashtable
    if ($manifest.ContainsKey('workspaces')) {
        throw 'npm workspaces are not supported by this isolated benchmark.'
    }
    $lockText = Get-Content -LiteralPath (Join-Path $npmSource 'package-lock.json') -Raw
    if ($lockText -match '"(?:resolved|version)"\s*:\s*"(?:file:|link:|git\+|git:)') {
        throw 'Local file/link/Git npm dependencies are unsupported by this isolated benchmark.'
    }
}

function Get-Stats([double[]] $measurements) {
    if ($measurements.Count -eq 0) {
        return $null
    }
    [double[]] $ordered = @($measurements | Sort-Object)
    $n = $ordered.Count
    # Nearest-rank percentiles; with five observations p95 is the maximum.
    $p50 = [Math]::Ceiling($n * 0.50) - 1
    $p95 = [Math]::Ceiling($n * 0.95) - 1
    return [ordered]@{
        count = $n
        minMs = [Math]::Round($ordered[0], 3)
        p50Ms = [Math]::Round($ordered[$p50], 3)
        p95Ms = [Math]::Round($ordered[$p95], 3)
        maxMs = [Math]::Round($ordered[$n - 1], 3)
    }
}

# All following writes are confined to a newly created directory.
New-Item -ItemType Directory -Path $output | Out-Null
$report = [ordered]@{
    schemaVersion = 1
    createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    machine = [Environment]::MachineName
    iterations = $Iterations
    caveats = @(
        'Timings are environment-specific and not representative until run on reference developer hardware.',
        'Git measurements exercise actual Git/SQLite and verified npm artifact copy, but use a fake editor.',
        'The npm baseline compares isolated warm-cache npm ci --offline --ignore-scripts with ordinary file copy, NOT the production verified copy adapter.',
        'No test measures browser click to visible VS Code readiness, package lifecycle scripts, build time or full installed-app latency.',
        'Artifact size is logical bytes, not NTFS/ReFS allocated physical bytes; no automatic cleanup is performed.'
    )
}

if (-not $SkipGitBenchmark) {
    $dotnet = Get-Command dotnet -ErrorAction Stop
    $testProject = Join-Path $projectRoot 'tests/GitWorkspacePool.Tests/GitWorkspacePool.Tests.csproj'
    if (-not $SkipBuild) {
        & $dotnet.Source restore $testProject
        if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
        & $dotnet.Source build $testProject --configuration Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
    }

    $samples = @()
    $oldResultFile = [Environment]::GetEnvironmentVariable('GWP_BENCHMARK_FILE')
    try {
        for ($i = 1; $i -le $Iterations; $i++) {
            $resultFile = Join-Path $output ('git-sample-{0:D2}.json' -f $i)
            $logFile = Join-Path $output ('git-sample-{0:D2}.log' -f $i)
            $env:GWP_BENCHMARK_FILE = $resultFile
            $testArgs = @(
                'test', $testProject, '--configuration', 'Release',
                '--no-build', '--no-restore', '--filter',
                'FullyQualifiedName~WarmNpm_BenchmarkColdCheckoutVsVerifiedArtifactCopy',
                '--logger', 'console;verbosity=detailed'
            )
            & $dotnet.Source @testArgs *> $logFile
            if ($LASTEXITCODE -ne 0) {
                throw "Git/SQLite benchmark sample $i failed. See $logFile"
            }
            if (-not (Test-Path -LiteralPath $resultFile -PathType Leaf)) {
                throw "Git/SQLite benchmark sample $i did not publish its JSON result. See $logFile"
            }
            $sample = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json -AsHashtable
            if ($sample.artifactFiles -ne 1024 -or $sample.artifactLogicalBytes -ne 2097152) {
                throw "Sample $i produced an unexpected artifact count or size."
            }
            $samples += $sample
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable('GWP_BENCHMARK_FILE', $oldResultFile)
    }

    $report.gitRealFixture = [ordered]@{
        samples = $samples
        coldCheckout = (Get-Stats @($samples | ForEach-Object { [double] $_.coldCheckoutMs }))
        releasedSlotCheckout = (Get-Stats @($samples | ForEach-Object { [double] $_.releasedSlotCheckoutMs }))
        verifiedArtifactCopy = (Get-Stats @($samples | ForEach-Object { [double] $_.verifiedArtifactCopyMs }))
        checkoutAndCopy = (Get-Stats @($samples | ForEach-Object {
            [double] $_.releasedSlotCheckoutMs + [double] $_.verifiedArtifactCopyMs
        }))
        artifactLogicalBytes = 2097152
        artifactFiles = 1024
    }
}

if (-not [string]::IsNullOrWhiteSpace($NpmProjectPath)) {
    $npmCommand = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if ($null -eq $npmCommand) {
        $npmCommand = Get-Command npm -ErrorAction Stop
    }
    $npm = $npmCommand.Source
    $npmRoot = Join-Path $output 'npm'
    $cache = Join-Path $npmRoot 'cache'
    $donor = Join-Path $npmRoot 'donor'
    New-Item -ItemType Directory -Path $donor -Force | Out-Null
    foreach ($name in @('package.json', 'package-lock.json')) {
        Copy-Item -LiteralPath (Join-Path $npmSource $name) -Destination $donor
    }

    function Invoke-IsolatedNpmCi([string] $directory, [string] $log, [bool] $offline) {
        $npmArgs = @('ci', '--ignore-scripts', '--no-audit', '--no-fund', '--cache', $cache)
        if ($offline) { $npmArgs += '--offline' }
        Push-Location -LiteralPath $directory
        try {
            $watch = [System.Diagnostics.Stopwatch]::StartNew()
            & $npm @npmArgs *> $log
            $exitCode = $LASTEXITCODE
            $watch.Stop()
            if ($exitCode -ne 0) {
                throw "Isolated npm ci failed with exit $exitCode. See $log"
            }
            return $watch.Elapsed.TotalMilliseconds
        }
        finally {
            Pop-Location
        }
    }

    # Only the first disposable install may use the registry to fill the
    # isolated cache. Subsequent comparisons are offline and cache-warm.
    $warmupMs = Invoke-IsolatedNpmCi $donor (Join-Path $npmRoot 'donor-warmup.log') $false
    $modules = Join-Path $donor 'node_modules'
    if (-not (Test-Path -LiteralPath $modules -PathType Container)) {
        throw 'npm ci did not produce node_modules; choose a standalone project with dependencies.'
    }

    $fileCount = 0
    [long] $logicalBytes = 0
    foreach ($entry in Get-ChildItem -LiteralPath $modules -Recurse -Force) {
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Symlinks/junctions in npm dependency tree are not benchmarked; data was not copied.'
        }
        if (-not $entry.PSIsContainer) {
            $fileCount++
            $logicalBytes += $entry.Length
        }
        if ($fileCount -gt 20000 -or $logicalBytes -gt 536870912) {
            throw 'npm dependency tree exceeds 20,000 files or 512 MiB; benchmark retains its disposable evidence.'
        }
    }
    if ($fileCount -eq 0) { throw 'npm dependency tree had no regular files.' }

    $trials = @()
    for ($i = 1; $i -le $Iterations; $i++) {
        $trialRoot = Join-Path $npmRoot ('trial-{0:D2}' -f $i)
        $install = Join-Path $trialRoot 'npm-ci'
        $copy = Join-Path $trialRoot 'file-copy'
        New-Item -ItemType Directory -Path $install, $copy -Force | Out-Null
        foreach ($name in @('package.json', 'package-lock.json')) {
            Copy-Item -LiteralPath (Join-Path $donor $name) -Destination $install
            Copy-Item -LiteralPath (Join-Path $donor $name) -Destination $copy
        }

        # Alternate the order to reduce systematic warm-cache/order effects.
        $copyMs = 0.0
        $installMs = 0.0
        if (($i % 2) -eq 1) {
            $installMs = Invoke-IsolatedNpmCi $install (Join-Path $trialRoot 'npm-ci.log') $true
        }
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        Copy-Item -LiteralPath $modules -Destination (Join-Path $copy 'node_modules') -Recurse
        $clock.Stop()
        $copyMs = $clock.Elapsed.TotalMilliseconds
        if (($i % 2) -eq 0) {
            $installMs = Invoke-IsolatedNpmCi $install (Join-Path $trialRoot 'npm-ci.log') $true
        }

        # File count and logical bytes confirm the copy did not skip payloads.
        $copiedFiles = @(Get-ChildItem -LiteralPath (Join-Path $copy 'node_modules') -File -Recurse -Force)
        [long] $copiedBytes = 0
        foreach ($f in $copiedFiles) { $copiedBytes += $f.Length }
        if ($copiedFiles.Count -ne $fileCount -or $copiedBytes -ne $logicalBytes) {
            throw "Trial $i copied an incomplete dependency tree."
        }
        $trials += [ordered]@{
            npmCiOfflineMs = $installMs
            rawFileCopyMs = $copyMs
        }
    }
    $report.npmStandaloneProject = [ordered]@{
        projectName = $manifest.name
        npmVersion = (& $npm --version)
        npmWarmupMs = $warmupMs
        artifactFiles = $fileCount
        artifactLogicalBytes = $logicalBytes
        samples = $trials
        warmedOfflineNpmCi = (Get-Stats @($trials | ForEach-Object { [double] $_.npmCiOfflineMs }))
        rawFileCopy = (Get-Stats @($trials | ForEach-Object { [double] $_.rawFileCopyMs }))
    }
}

$reportFile = Join-Path $output 'benchmark-results.json'
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportFile -Encoding utf8
Write-Host "Benchmark completed: $reportFile"
Write-Host 'No benchmark data was automatically deleted. Review and remove the new output folder manually when finished.'
