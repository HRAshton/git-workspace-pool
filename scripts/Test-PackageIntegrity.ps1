[CmdletBinding()]
param(
    [string] $PackageDirectory = $PSScriptRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath($PackageDirectory)

function Assert-NoLinks([string] $path) {
    $walk = $path
    while ($null -ne $walk) {
        if (Test-Path -LiteralPath $walk) {
            $item = Get-Item -LiteralPath $walk -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Package paths must not pass through links or junctions.'
            }
        }
        $parent = [System.IO.Directory]::GetParent($walk)
        $walk = if ($null -eq $parent) { $null } else { $parent.FullName }
    }
}
Assert-NoLinks $root
$manifestPath = Join-Path $root 'PACKAGE_MANIFEST.json'
Assert-NoLinks $manifestPath
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Missing package manifest.' }
$stream = [System.IO.File]::Open($manifestPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
try {
    $length = $stream.Length
    if ($length -gt 1048576) { throw 'Oversized package manifest.' }
    $bytes = [byte[]]::new([int] $length)
    $offset = 0
    while ($offset -lt $bytes.Length) {
        $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
        if ($read -eq 0) { throw 'Truncated package manifest.' }
        $offset += $read
    }
} finally { $stream.Dispose() }
$json = [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes).TrimStart([char] 0xFEFF)
$manifest = $json | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.sourceCommit -notmatch '^([a-f0-9]{40}|[a-f0-9]{64})$' -or
    $manifest.sourceTree -notmatch '^([a-f0-9]{40}|[a-f0-9]{64})$' -or
    @($manifest.files).Count -eq 0 -or @($manifest.files).Count -gt 1024) {
    throw 'Invalid package manifest.'
}
$expected = [System.Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    $relative = [string] $entry.path
    if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Contains('\') -or
        $relative.StartsWith('/') -or $relative.Contains(':') -or
        @($relative.Split('/') | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }).Count -ne 0 -or
        $relative -eq 'PACKAGE_MANIFEST.json' -or
        $entry.sha256 -notmatch '^[a-f0-9]{64}$' -or [long] $entry.bytes -lt 0 -or
        $expected.ContainsKey($relative)) {
        throw 'Unsafe or duplicate manifest entry.'
    }
    $expected.Add($relative, $entry)
}
# Enumerate one level at a time, rejecting a link before traversing it.
$pending = [System.Collections.Generic.Stack[string]]::new()
$pending.Push($root)
$count = 0
$directories = 0
while ($pending.Count -ne 0) {
    $directory = $pending.Pop()
    if (++$directories -gt 1024) { throw 'Package directory inventory exceeds its limit.' }
    foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Package contains a link or junction.'
        }
        if ($item.PSIsContainer) { $pending.Push($item.FullName); continue }
        $relative = $item.FullName.Substring($root.TrimEnd([char[]] '\/').Length + 1).Replace('\', '/')
        if ($relative -eq 'PACKAGE_MANIFEST.json') { continue }
        if (++$count -gt 1024 -or -not $expected.ContainsKey($relative)) {
            throw 'Package contains an unexpected file.'
        }
        $entry = $expected[$relative]
        if ($item.Length -ne [long] $entry.bytes -or
            (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) {
            throw "Package file failed integrity verification: $relative"
        }
        $expected.Remove($relative) | Out-Null
    }
}
if ($expected.Count -ne 0) { throw 'Package is missing manifest files.' }
Write-Host "PASS: package inventory and hashes; source $($manifest.sourceCommit), tree $($manifest.sourceTree)."
Write-Host 'Integrity is not publisher authentication. Verify the ZIP checksum from trusted build evidence before running package scripts.'
