[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $TestRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath($TestRoot)
if (Test-Path -LiteralPath $root) {
    throw 'Prerequisite test destination already exists.'
}
New-Item -ItemType Directory -Path $root | Out-Null

$probe = Join-Path $PSScriptRoot 'Test-RC2Prerequisites.ps1'
$code = Join-Path $root 'Code.exe'
$settings = Join-Path $root 'settings.json'

# A harmless fixture file proves executable-path discovery only. No GUI
# application is started and the user's own settings are never loaded.
[System.IO.File]::WriteAllBytes($code, [byte[]]@(1, 2, 3, 4))
@{ editorExecutable = $code } | ConvertTo-Json |
    Set-Content -LiteralPath $settings -Encoding utf8

& $probe -SettingsFile $settings
if ($LASTEXITCODE -ne 0) {
    throw 'Installed Git and configured Code.exe presence check failed.'
}

$failed = $false
try {
    & $probe -SettingsFile $settings -GitExecutable '__gwp_git_not_installed__'
} catch {
    $failed = $true
}
if (-not $failed) {
    throw 'Prerequisite check incorrectly accepted missing Git.'
}

$failed = $false
try {
    & $probe -SettingsFile $settings -EditorExecutable (Join-Path $root 'missing/Code.exe')
} catch {
    $failed = $true
}
if (-not $failed) {
    throw 'Prerequisite check incorrectly accepted missing VS Code.'
}

$failed = $false
try {
    & $probe -SettingsFile $settings -EditorExecutable 'Code.exe'
} catch {
    $failed = $true
}
if (-not $failed) {
    throw 'Prerequisite check accepted a relative editor path.'
}

if (-not (Test-Path -LiteralPath $code -PathType Leaf) -or
    (Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json).editorExecutable -ne $code) {
    throw 'Prerequisite diagnostics modified the test user configuration.'
}

Write-Host 'PASS: RC2 read-only Git/VS Code prerequisite inspection and safe refusal.'
