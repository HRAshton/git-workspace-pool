param(
    [string] $InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\GitWorkspacePool')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath($InstallRoot)
$prefix = '"' + (Join-Path $root 'versions') + [System.IO.Path]::DirectorySeparatorChar
$suffix = [System.IO.Path]::DirectorySeparatorChar + 'GitWorkspacePool.Tray.exe" "%1"'

$registry = [Microsoft.Win32.Registry]::CurrentUser
$key = $registry.OpenSubKey('Software\Classes\prworkspace\shell\open\command', $false)
if ($null -eq $key) {
    Write-Host 'No current-user prworkspace: registration exists.'
    return
}
$current = [string] $key.GetValue('')
$key.Close()

# Never unregister a protocol owned by another executable or another root.
if (-not $current.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) -or
    -not $current.EndsWith($suffix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The protocol registration points outside this installer root; nothing was removed.'
}

$registry.DeleteSubKeyTree('Software\Classes\prworkspace', $false)
Write-Host 'Removed only this installer-owned current-user protocol registration.'
Write-Host 'Previously installed application versions, user settings, snapshots and all worktrees were preserved.'
