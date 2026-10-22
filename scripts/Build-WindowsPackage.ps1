param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    throw "Output directory already exists; select a new location to avoid overwriting artifacts: $output"
}

$package = Join-Path $output 'package'
$app = Join-Path $package 'app'
New-Item -ItemType Directory -Path $app -Force | Out-Null

$project = Join-Path $projectRoot 'src/GitWorkspacePool.Tray/GitWorkspacePool.Tray.csproj'
$publishArgs = @(
    'publish', $project, '--configuration', 'Release',
    '--runtime', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:PublishTrimmed=false',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:ContinuousIntegrationBuild=true', '-p:DebugSymbols=false',
    '-p:DebugType=none', '--output', $app
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "Self-contained Windows publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $app 'GitWorkspacePool.Tray.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw 'Published application executable was not produced.'
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-PerUser.ps1') -Destination $package
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Unregister-PerUser.ps1') -Destination $package
Copy-Item -LiteralPath (Join-Path $projectRoot 'userscript/git-workspace-pool.user.js') -Destination $package

$zip = Join-Path $output 'GitWorkspacePool-win-x64-preview.zip'
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip -CompressionLevel Optimal
$sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $output 'GitWorkspacePool-win-x64-preview.zip.sha256') -Value "$sha  GitWorkspacePool-win-x64-preview.zip" -Encoding ascii
Write-Host "Published unsigned, self-contained preview: $zip"
Write-Host "SHA-256: $sha"
