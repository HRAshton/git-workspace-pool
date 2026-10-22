param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [ValidatePattern('^GitWorkspacePool-win-x64-[A-Za-z0-9.-]+$')]
    [string] $PackageBaseName = 'GitWorkspacePool-win-x64-preview',

    [string] $PackageVersion = ''
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
if ($PackageVersion) {
    if ($PackageVersion -notmatch '^[0-9]+[.][0-9]+[.][0-9]+(-[A-Za-z0-9.-]+)?$') {
        throw 'Invalid package version.'
    }
    $publishArgs += "-p:Version=$PackageVersion"
}
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

if ($PackageVersion) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/RC1_RELEASE_NOTES.md') -Destination $package
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/RC1_ACCEPTANCE.md') -Destination $package
}

$zipName = "$PackageBaseName.zip"
$zip = Join-Path $output $zipName
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip -CompressionLevel Optimal
$sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $output "$zipName.sha256") -Value "$sha  $zipName" -Encoding ascii
Write-Host "Published unsigned, self-contained Windows package: $zip"
Write-Host "SHA-256: $sha"
