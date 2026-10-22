[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $BackupDirectory,

    [string] $StateRoot = (Join-Path $env:LOCALAPPDATA 'GitWorkspacePool'),

    [string] $SettingsRoot = (Join-Path $env:APPDATA 'GitWorkspacePool')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw 'Per-user backup is supported on Windows only.'
}

# The journal must be closed before copying its DB/WAL/SHM files.
if (Get-Process -Name 'GitWorkspacePool.Tray' -ErrorAction SilentlyContinue) {
    throw 'Exit Git Workspace Pool tray before backing up its SQLite state.'
}

$backup = [System.IO.Path]::GetFullPath($BackupDirectory)
$state = [System.IO.Path]::GetFullPath($StateRoot)
$settings = [System.IO.Path]::GetFullPath($SettingsRoot)
$comparison = [System.StringComparison]::OrdinalIgnoreCase

function Test-Within([string] $parent, [string] $child) {
    $prefix = $parent.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    return $child.Equals($parent, $comparison) -or $child.StartsWith($prefix, $comparison)
}

if ((Test-Within $state $backup) -or (Test-Within $settings $backup) -or
    (Test-Within $backup $state) -or (Test-Within $backup $settings)) {
    throw 'Keep the backup outside the live state and settings directory trees.'
}
if (Test-Path -LiteralPath $backup) {
    throw "Backup destination already exists; refusing to overwrite: $backup"
}

function Assert-NoReparse([string] $path) {
    $walk = [System.IO.Path]::GetFullPath($path)
    while ($null -ne $walk) {
        if (Test-Path -LiteralPath $walk) {
            $item = Get-Item -LiteralPath $walk -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Cannot back up through a symbolic link or junction: $walk"
            }
        }
        $parent = [System.IO.Directory]::GetParent($walk)
        if ($null -eq $parent) { break }
        $walk = $parent.FullName
    }
}

Assert-NoReparse $backup
Assert-NoReparse $state
Assert-NoReparse $settings

$items = @(
    @{ Source = (Join-Path $state 'sessions.db'); Relative = 'state/sessions.db' },
    @{ Source = (Join-Path $state 'sessions.db-wal'); Relative = 'state/sessions.db-wal' },
    @{ Source = (Join-Path $state 'sessions.db-shm'); Relative = 'state/sessions.db-shm' },
    @{ Source = (Join-Path $settings 'settings.json'); Relative = 'settings/settings.json' }
)
$existing = @($items | Where-Object { Test-Path -LiteralPath $_.Source -PathType Leaf })
if ($existing.Count -eq 0) {
    throw 'No journal or settings found; nothing to back up.'
}
foreach ($item in $existing) {
    Assert-NoReparse $item.Source
}

# Do not follow links, delete evidence or overwrite any previous backup.
New-Item -ItemType Directory -Path $backup | Out-Null
$verified = @()
foreach ($item in $existing) {
    $sourceHash = (Get-FileHash -LiteralPath $item.Source -Algorithm SHA256).Hash.ToLowerInvariant()
    $relative = $item.Relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $destination = Join-Path $backup $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $item.Source -Destination $destination -ErrorAction Stop

    $copiedHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($copiedHash -ne $sourceHash) {
        throw "Backup verification failed; partial backup retained: $backup"
    }
    $verified += [pscustomobject]@{
        path = $item.Relative
        bytes = (Get-Item -LiteralPath $destination).Length
        sha256 = $copiedHash
    }
}

# Catch journal mutations occurring after the first file was copied.
foreach ($item in $verified) {
    $original = $items | Where-Object { $_.Relative -eq $item.path } | Select-Object -First 1
    if (-not (Test-Path -LiteralPath $original.Source -PathType Leaf) -or
        (Get-FileHash -LiteralPath $original.Source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.sha256) {
        throw "User state changed during backup; backup retained but not verified: $backup"
    }
}

$manifest = [ordered]@{
    schemaVersion = 1
    createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    result = 'verified'
    note = 'Settings and SQLite journal only. Worktrees, snapshots, and parked folders require separate backups.'
    files = $verified
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $backup 'BACKUP_MANIFEST.json') -Encoding utf8
Write-Host "Verified immutable backup at: $backup"
Write-Host 'Back up managed worktrees, parked directories and snapshots separately before upgrade.'
