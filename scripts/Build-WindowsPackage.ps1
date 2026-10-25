param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [ValidatePattern('^GitWorkspacePool-win-x64-[A-Za-z0-9.-]+$')]
    [string] $PackageBaseName = 'GitWorkspacePool-win-x64-preview',

    [string] $PackageVersion = '',

    [string] $ReleaseDocsPrefix = ''
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
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Backup-PerUserState.ps1') -Destination $package
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-PackageIntegrity.ps1') -Destination $package
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-RC2Prerequisites.ps1') -Destination $package
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/RECOVERY.md') -Destination $package
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/ATTESTATION.md') -Destination $package
Copy-Item -LiteralPath (Join-Path $projectRoot 'userscript/git-branch-workspace.user.js') -Destination $package

# Preserve relative links in the bundled guides as well as flat compatibility copies.
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $package
Copy-Item -LiteralPath (Join-Path $projectRoot 'ROADMAP.md') -Destination $package
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $package -Recurse

$sourceCommit = (& git -C $projectRoot rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[a-f0-9]{40,64}$') { throw 'Cannot identify package source commit.' }
$sourceTree = (& git -C $projectRoot rev-parse 'HEAD^{tree}' | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceTree -notmatch '^[a-f0-9]{40,64}$') { throw 'Cannot identify package source tree.' }
$sourceChanges = & git -C $projectRoot status --porcelain --untracked-files=normal
if ($LASTEXITCODE -ne 0 -or $sourceChanges) { throw 'Package provenance requires a clean committed source checkout. Put output outside the repository.' }
$sdk = (& dotnet --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot capture SDK version.' }
$files = @(Get-ChildItem -LiteralPath $package -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path = [System.IO.Path]::GetRelativePath($package, $_.FullName).Replace('\', '/')
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
[ordered]@{
    schemaVersion = 1
    sourceCommit = $sourceCommit
    sourceTree = $sourceTree
    sdk = $sdk
    packageVersion = if ($PackageVersion) { $PackageVersion } else { 'preview' }
    workflowRun = $env:GITHUB_RUN_ID
    signing = 'unsigned'
    files = $files
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $package 'PACKAGE_MANIFEST.json') -Encoding utf8
& (Join-Path $package 'Test-PackageIntegrity.ps1') -PackageDirectory $package

$zipName = "$PackageBaseName.zip"
$zip = Join-Path $output $zipName
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip -CompressionLevel Optimal
$sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $output "$zipName.sha256") -Value "$sha  $zipName" -Encoding ascii
Write-Host "Published unsigned, self-contained Windows package: $zip"
Write-Host "SHA-256: $sha"
