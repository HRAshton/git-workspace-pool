[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory,

    [Parameter(Mandatory = $true)]
    [string] $TestRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script = Join-Path $PackageDirectory 'Backup-PerUserState.ps1'
if (-not (Test-Path -LiteralPath $script -PathType Leaf)) {
    throw 'RC2 package does not contain the per-user backup helper.'
}

$root = [System.IO.Path]::GetFullPath($TestRoot)
if (Test-Path -LiteralPath $root) {
    throw 'Backup test location already exists.'
}

$state = Join-Path $root 'live/state'
$settings = Join-Path $root 'live/settings'
$backup = Join-Path $root 'preserved-backup'
New-Item -ItemType Directory -Path $state -Force | Out-Null
New-Item -ItemType Directory -Path $settings -Force | Out-Null

[System.IO.File]::WriteAllBytes((Join-Path $state 'sessions.db'), [byte[]]@(0, 255, 1, 42))
[System.IO.File]::WriteAllBytes((Join-Path $state 'sessions.db-wal'), [byte[]]@(10, 11))
[System.IO.File]::WriteAllBytes((Join-Path $state 'sessions.db-shm'), [byte[]]@(12, 13))
Set-Content -LiteralPath (Join-Path $settings 'settings.json') -Value '{"repo":"keep"}' -NoNewline

& $script -BackupDirectory $backup -StateRoot $state -SettingsRoot $settings
$manifestPath = Join-Path $backup 'BACKUP_MANIFEST.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'Backup did not create its verification manifest.'
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.result -ne 'verified' -or @($manifest.files).Count -ne 4) {
    throw 'Backup manifest does not list the expected closed SQLite state and settings.'
}

foreach ($entry in $manifest.files) {
    $copy = Join-Path $backup $entry.path.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $copy -PathType Leaf) -or
        (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) {
        throw 'Backup checksum does not match its saved contents.'
    }
}

Set-Content -LiteralPath (Join-Path $settings 'settings.json') -Value '{"repo":"later edit"}' -NoNewline
if ((Get-Content -LiteralPath (Join-Path $backup 'settings/settings.json') -Raw) -ne '{"repo":"keep"}') {
    throw 'Previously verified backup was mutated by a live settings edit.'
}

$refused = $false
try {
    & $script -BackupDirectory $backup -StateRoot $state -SettingsRoot $settings
} catch {
    $refused = $true
}
if (-not $refused) {
    throw 'Backup helper allowed overwriting existing evidence.'
}

$refused = $false
try {
    & $script -BackupDirectory (Join-Path $state 'nested-backup') -StateRoot $state -SettingsRoot $settings
} catch {
    $refused = $true
}
if (-not $refused) {
    throw 'Backup helper allowed destination inside live state.'
}

Write-Host 'PASS: closed SQLite journal/sidecars/settings backup, hash verification, immutability and safe refusal.'
