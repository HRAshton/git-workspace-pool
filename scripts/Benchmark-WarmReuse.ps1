[CmdletBinding()]
param([Parameter(Mandatory=$true)][string] $OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$env:GWP_BENCHMARK_REPORT = Join-Path ([System.IO.Path]::GetFullPath($OutputDirectory)) 'synthetic-warm-benchmark.json'
try {
    dotnet test (Join-Path $PSScriptRoot '../tests/GitWorkspacePool.Tests/GitWorkspacePool.Tests.csproj') -c Release --filter FullyQualifiedName~SyntheticLargeTreeReportsFreshWarmAndReopenDistributions
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic benchmark failed.' }
} finally { Remove-Item Env:GWP_BENCHMARK_REPORT }
