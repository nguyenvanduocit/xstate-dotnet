#Requires -Version 7.0
[CmdletBinding()]
param([string] $WorkspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))
$ErrorActionPreference = 'Stop'
$reportDirectory = Join-Path $WorkspaceRoot 'tmp/xstate-parity'
[IO.Directory]::CreateDirectory($reportDirectory) | Out-Null
$reportPath = Join-Path $reportDirectory 'upstream-source-verification.json'
[IO.File]::Delete($reportPath)
$pin = Get-Content -LiteralPath (Join-Path $WorkspaceRoot 'src/XState/upstream.json') -Raw | ConvertFrom-Json
$archiveRelative = 'data/library-source/xstate-' + $pin.commit + '.zip'
$archivePath = Join-Path $WorkspaceRoot $archiveRelative
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($archiveHash -ne $pin.archiveSha256) { throw 'Pinned upstream archive checksum mismatch.' }
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $prefix = 'xstate-' + $pin.commit + '/'
    $packagePrefix = $prefix + $pin.package + '/'
    $examplePrefix = if ($pin.exampleDirectory) { $prefix + $pin.exampleDirectory + '/' } else { $null }
    $files = [Collections.Generic.List[object]]::new()
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName.EndsWith('/') -or (-not $entry.FullName.StartsWith($packagePrefix, [StringComparison]::Ordinal) -and ($null -eq $examplePrefix -or -not $entry.FullName.StartsWith($examplePrefix, [StringComparison]::Ordinal)))) { continue }
        $relative = $entry.FullName.Substring($prefix.Length)
        $sourcePath = Join-Path (Join-Path $WorkspaceRoot $pin.sourceDirectory) $relative
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw ('Pinned upstream source missing: ' + $relative) }
        $stream = $entry.Open()
        try { $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        $actual = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) { throw ('Pinned upstream source differs from archive: ' + $relative) }
        $files.Add([pscustomobject]@{ path = $relative; sha256 = $expected })
    }
    if ($files.Count -eq 0) { throw 'Pinned archive contains no core package files.' }
    [pscustomobject]@{ commit = $pin.commit; archive = $archiveRelative; archiveSha256 = $archiveHash; sourceDirectory = $pin.sourceDirectory; files = $files.ToArray() } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
    [pscustomobject]@{ verified = $true; files = $files.Count } | ConvertTo-Json -Compress
}
finally { $archive.Dispose() }
