param(
    [string] $PackageDirectory = $PSScriptRoot,
    [string] $InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\GitWorkspacePool'),
    [switch] $SkipProtocolRegistration
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw 'This package targets Windows only.'
}

$source = [System.IO.Path]::GetFullPath((Join-Path $PackageDirectory 'app'))
$sourceExe = Join-Path $source 'GitWorkspacePool.Tray.exe'
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    throw "The extracted package does not contain an application executable: $sourceExe"
}

# Do not allow an extracted package to redirect install files through links.
foreach ($entry in Get-ChildItem -LiteralPath $source -Recurse -Force) {
    if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The package contains an unsupported symlink or junction: $($entry.FullName)"
    }
}

$root = [System.IO.Path]::GetFullPath($InstallRoot)
$versions = Join-Path $root 'versions'
New-Item -ItemType Directory -Path $versions -Force | Out-Null

$hash = (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash.ToLowerInvariant()
$releaseId = $hash.Substring(0, 20)
$destination = Join-Path $versions $releaseId
$installedExe = Join-Path $destination 'GitWorkspacePool.Tray.exe'
$created = $false

if (Test-Path -LiteralPath $destination) {
    if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf) -or
        (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) {
        throw "Existing release location differs; no files were overwritten: $destination"
    }
} else {
    $stage = Join-Path $versions ('.pending-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $source -Force) {
        Copy-Item -LiteralPath $entry.FullName -Destination $stage -Recurse
    }

    $stageExe = Join-Path $stage 'GitWorkspacePool.Tray.exe'
    if (-not (Test-Path -LiteralPath $stageExe -PathType Leaf) -or
        (Get-FileHash -LiteralPath $stageExe -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) {
        throw "Staged executable differs; staged files were retained: $stage"
    }

    # Startup validation happens before any protocol or app state changes.
    # The smoke mode never opens the tray or modifies production settings.
    $marker = Join-Path $stage ('smoke-' + [guid]::NewGuid().ToString('N') + '.txt')
    $probeArgs = '--smoke-test "' + $marker + '"'
    $probe = Start-Process -FilePath $stageExe -ArgumentList $probeArgs -PassThru
    try {
        if (-not $probe.WaitForExit(60000)) {
            $probe.Kill()
            $probe.WaitForExit()
            throw "Application smoke probe timed out; staged files retained: $stage"
        }
        if ($probe.ExitCode -ne 0 -or
            -not (Test-Path -LiteralPath $marker -PathType Leaf) -or
            (Get-Content -LiteralPath $marker -Raw).Trim() -ne 'GWP_PORTABLE_SMOKE_V1') {
            throw "Application smoke probe failed; staged files retained: $stage"
        }
    } finally {
        # Release the probe's Windows process handle before renaming its
        # executable's parent directory. It must not survive publication.
        $probe.Dispose()
    }

    Remove-Item -LiteralPath $marker -Force

    # Windows AV/indexing may briefly retain a handle to the executable
    # after the probe exits. Retry only transient sharing/access errors,
    # never replace an existing release, and retain the stage on failure.
    $published = $false
    for ($attempt = 0; $attempt -lt 6; $attempt++) {
        if (Test-Path -LiteralPath $destination) {
            throw "Release destination became occupied; staged files retained: $stage"
        }
        try {
            [System.IO.Directory]::Move($stage, $destination)
            $published = $true
            break
        } catch {
            $reason = $_.Exception
            if ($null -ne $reason.InnerException) {
                $reason = $reason.InnerException
            }
            if (($reason -isnot [System.IO.IOException] -and
                 $reason -isnot [System.UnauthorizedAccessException]) -or
                $attempt -eq 5 -or
                -not (Test-Path -LiteralPath $stage -PathType Container)) {
                throw
            }
            Start-Sleep -Milliseconds (200 * ($attempt + 1))
        }
    }
    if (-not $published) {
        throw "Could not publish staged release; files retained: $stage"
    }
    $created = $true
}

if (-not $SkipProtocolRegistration) {
    $registry = [Microsoft.Win32.Registry]::CurrentUser
    $keyPath = 'Software\Classes\prworkspace'
    $commandPath = $keyPath + '\shell\open\command'
    $previous = $registry.OpenSubKey($commandPath, $false)
    $previousCommand = if ($null -ne $previous) { [string] $previous.GetValue('') } else { '' }
    if ($null -ne $previous) { $previous.Close() }

    # Do not take over an unrelated app's URL handler.
    if ($previousCommand -and -not $previousCommand.Contains('GitWorkspacePool.Tray.exe')) {
        throw 'prworkspace: is registered to an unrelated executable. No registration was changed.'
    }

    $protocol = $registry.CreateSubKey($keyPath)
    $protocol.SetValue('', 'URL:Git Workspace Pool Protocol')
    $protocol.SetValue('URL Protocol', '')
    $protocol.Close()

    $command = $registry.CreateSubKey($commandPath)
    $command.SetValue('', '"' + $installedExe + '" "%1"')
    $command.Close()
}

Write-Host "Git Workspace Pool preview installed: $installedExe"
if ($created) {
    Write-Host 'Existing versions, settings, snapshots, and worktrees were preserved.'
}
if ($SkipProtocolRegistration) {
    Write-Host 'Registry registration skipped for isolated smoke testing.'
} else {
    Write-Host 'Current-user prworkspace: protocol now targets this release.'
}
Write-Host 'Start the installed GitWorkspacePool.Tray.exe to launch the tray.'
