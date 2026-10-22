param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,
    [Parameter(Mandatory = $true)]
    [string] $TestRoot,

    [string[]] $RequiredDocs = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$checksum = $PackagePath + '.sha256'
if (-not (Test-Path -LiteralPath $checksum -PathType Leaf)) {
    throw 'The release preview checksum file is missing.'
}
$expectedHash = ((Get-Content -LiteralPath $checksum -Raw).Trim() -split '\s+')[0]
$actualHash = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash
if (-not $expectedHash.Equals($actualHash, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The preview ZIP SHA-256 does not match its packaged checksum.'
}

$root = [System.IO.Path]::GetFullPath($TestRoot)
if (Test-Path -LiteralPath $root) {
    throw "The test root already exists; refusing to overwrite it: $root"
}

New-Item -ItemType Directory -Path $root | Out-Null
$extracted = Join-Path $root 'extracted'
$install = Join-Path $root 'installed'
New-Item -ItemType Directory -Path $extracted | Out-Null
Expand-Archive -LiteralPath $PackagePath -DestinationPath $extracted

# A candidate archive must contain the documented user safety procedures;
# validate the actual ZIP contents, not only repository file existence.
foreach ($document in $RequiredDocs) {
    if ([System.IO.Path]::GetFileName($document) -ne $document) {
        throw "Unsafe RC documentation name: $document"
    }
    $documentPath = Join-Path $extracted $document
    if (-not (Test-Path -LiteralPath $documentPath -PathType Leaf) -or
        (Get-Item -LiteralPath $documentPath).Length -eq 0) {
        throw "RC package missing required recovery documentation: $document"
    }
}

$installer = Join-Path $extracted 'Install-PerUser.ps1'
if (-not (Test-Path -LiteralPath $installer)) {
    throw 'The preview archive is missing its per-user installer.'
}

# Preserve unrelated data and historical releases across initial install
# and a second idempotent install; do not register HKCU during CI.
$old = Join-Path $install 'versions/previous'
New-Item -ItemType Directory -Path $old -Force | Out-Null
$oldFile = Join-Path $old 'user.keep'
Set-Content -LiteralPath $oldFile -Value 'keep previous data' -NoNewline
$unrelated = Join-Path $install 'user.keep'
Set-Content -LiteralPath $unrelated -Value 'keep root data' -NoNewline

& $installer -PackageDirectory $extracted -InstallRoot $install -SkipProtocolRegistration
& $installer -PackageDirectory $extracted -InstallRoot $install -SkipProtocolRegistration

if ((Get-Content -LiteralPath $oldFile -Raw) -ne 'keep previous data' -or
    (Get-Content -LiteralPath $unrelated -Raw) -ne 'keep root data') {
    throw 'Installation mutated an existing release or unrelated data.'
}

$exes = @(Get-ChildItem -LiteralPath (Join-Path $install 'versions') -Filter 'GitWorkspacePool.Tray.exe' -Recurse -File)
if ($exes.Count -ne 1) {
    throw "Expected exactly one installed application executable; found $($exes.Count)."
}

# A tampered existing version must fail closed: never replace a user-owned
# directory, even if its leaf name equals the expected executable hash.
$collisionRoot = Join-Path $root 'occupied-version-test'
$releaseId = ((Get-FileHash -LiteralPath $exes[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()).Substring(0, 20)
$blocked = Join-Path $collisionRoot ('versions/' + $releaseId)
New-Item -ItemType Directory -Path $blocked -Force | Out-Null
$blockedExe = Join-Path $blocked 'GitWorkspacePool.Tray.exe'
[System.IO.File]::WriteAllBytes($blockedExe, [byte[]]@(0, 1, 2, 3, 4))
$blockedHash = (Get-FileHash -LiteralPath $blockedExe -Algorithm SHA256).Hash

$refusedCollision = $false
try {
    & $installer -PackageDirectory $extracted -InstallRoot $collisionRoot -SkipProtocolRegistration
} catch {
    $refusedCollision = $true
}
if (-not $refusedCollision -or
    (Get-FileHash -LiteralPath $blockedExe -Algorithm SHA256).Hash -ne $blockedHash) {
    throw 'Installer overwrote or accepted an occupied release directory.'
}

$report = Join-Path $root 'verify-smoke.txt'
$probeArgs = '--smoke-test "' + $report + '"'
$probe = Start-Process -FilePath $exes[0].FullName -ArgumentList $probeArgs -PassThru
if (-not $probe.WaitForExit(60000)) {
    $probe.Kill()
    throw 'Installed application smoke probe timed out.'
}
if ($probe.ExitCode -ne 0 -or
    -not (Test-Path -LiteralPath $report -PathType Leaf) -or
    (Get-Content -LiteralPath $report -Raw).Trim() -ne 'GWP_PORTABLE_SMOKE_V1') {
    throw 'Installed application could not launch in no-UI smoke mode.'
}

Write-Host 'PASS: self-contained application launch, per-user install, idempotent upgrade and data preservation.'
