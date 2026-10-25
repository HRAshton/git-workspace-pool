[CmdletBinding()]
param(
    [string] $GitExecutable = 'git',

    [string] $EditorExecutable,

    [string] $SettingsFile = (Join-Path $env:APPDATA 'GitWorkspacePool/settings.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw 'RC2 prerequisite inspection requires Windows.'
}

# Diagnostic-only: does not write settings, register the protocol, start an
# editor, run Git in a repository, or change any user files.
$gitCommand = Get-Command $GitExecutable -ErrorAction Stop
$rawGitVersion = (& $gitCommand.Source --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Git version command failed: $rawGitVersion"
}
if ($rawGitVersion -match '^git version ([0-9]+)[.]([0-9]+)[.]([0-9]+)') {
    $actualVersion = [Version]::new(
        [int] $Matches[1],
        [int] $Matches[2],
        [int] $Matches[3]
    )
} else {
    throw "Git version cannot be verified: $rawGitVersion"
}

$requiredVersion = [Version] '2.40.0'
if ($actualVersion -lt $requiredVersion) {
    throw "Git $requiredVersion or newer is required by this RC2 support policy; found $rawGitVersion"
}

if (-not $PSBoundParameters.ContainsKey('EditorExecutable')) {
    if (Test-Path -LiteralPath $SettingsFile -PathType Leaf) {
        $settings = Get-Content -LiteralPath $SettingsFile -Raw | ConvertFrom-Json
        $editorSetting = $settings.PSObject.Properties['editorExecutable']
        if ($null -ne $editorSetting -and
            -not [string]::IsNullOrWhiteSpace([string] $editorSetting.Value)) {
            $EditorExecutable = [string] $editorSetting.Value
        }
    }
}

if ([string]::IsNullOrWhiteSpace($EditorExecutable)) { $EditorExecutable = 'code.exe' }
if ($EditorExecutable -eq 'code.exe') {
    $resolvedEditor = $null
    foreach ($entry in $env:PATH.Split([System.IO.Path]::PathSeparator)) {
        $directory = $entry.Trim().Trim('"')
        if (-not [System.IO.Path]::IsPathFullyQualified($directory)) { continue }
        $candidate = Join-Path $directory 'code.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $resolvedEditor = $candidate; break }
        if ([System.IO.Path]::GetFileName($directory.TrimEnd('\')) -eq 'bin') {
            $candidate = [System.IO.Path]::GetFullPath((Join-Path $directory '../Code.exe'))
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $resolvedEditor = $candidate; break }
        }
    }
    if (-not $resolvedEditor) { throw 'code.exe not found on PATH; select the absolute VS Code Code.exe path.' }
    $EditorExecutable = $resolvedEditor
}

if (-not [System.IO.Path]::IsPathFullyQualified($EditorExecutable) -or
    -not $EditorExecutable.EndsWith('Code.exe', [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $EditorExecutable -PathType Leaf)) {
    throw "VS Code Code.exe not found at the configured path: $EditorExecutable"
}

Write-Host "PASS: Windows; Git $actualVersion (minimum policy $requiredVersion); VS Code Code.exe present at $EditorExecutable"
Write-Host 'Read-only prerequisite inspection, not an installed browser/tray/editor test.'
