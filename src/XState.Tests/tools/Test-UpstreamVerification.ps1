#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$fixtureRoot = Join-Path $workspace ('tmp/xstate-parity/source-controls-' + [Guid]::NewGuid().ToString('N'))
$verifier = Join-Path $PSScriptRoot 'Verify-Upstream.ps1'
$commit = 'fixture'
$sourceDirectory = 'data/library-source/xstate-fixture'
$sourceFiles = [ordered]@{ 'packages/core/src/index.ts' = 'export const value = 1;'; 'packages/core/test/__snapshots__/case.snap' = 'original snapshot'; 'examples/toggle/main.ts' = 'original example' }
[IO.Directory]::CreateDirectory((Join-Path $fixtureRoot 'src/XState')) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $fixtureRoot 'data/library-source')) | Out-Null
$archivePath = Join-Path $fixtureRoot 'data/library-source/xstate-fixture.zip'
$archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($pair in $sourceFiles.GetEnumerator()) {
        $path = Join-Path (Join-Path $fixtureRoot $sourceDirectory) $pair.Key
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
        [IO.File]::WriteAllText($path, $pair.Value)
        $entry = $archive.CreateEntry('xstate-fixture/' + $pair.Key)
        $stream = $entry.Open()
        try { $bytes = [Text.Encoding]::UTF8.GetBytes($pair.Value); $stream.Write($bytes, 0, $bytes.Length) }
        finally { $stream.Dispose() }
    }
}
finally { $archive.Dispose() }
[pscustomobject]@{ commit = $commit; package = 'packages/core'; exampleDirectory = 'examples'; sourceDirectory = $sourceDirectory; archiveSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixtureRoot 'src/XState/upstream.json') -Encoding utf8NoBOM
$reportPath = Join-Path $fixtureRoot 'tmp/xstate-parity/upstream-source-verification.json'
$tests = [Collections.Generic.List[object]]::new()
function Assert-Rejected([string] $name, [string] $reason) {
    $rejected = $false
    try { & $verifier -WorkspaceRoot $fixtureRoot | Out-Null }
    catch { if ($_.Exception.Message -notlike ('*' + $reason + '*')) { throw }; $rejected = $true }
    if (-not $rejected) { throw ('Verifier accepted ' + $name) }
    if (Test-Path -LiteralPath $reportPath) { throw ('Verifier retained stale success evidence for ' + $name) }
    $tests.Add([pscustomobject]@{ name = $name; status = 'passed' })
}
& $verifier -WorkspaceRoot $fixtureRoot | Out-Null
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if ($report.files.Count -ne 3) { throw 'Verifier did not cover every core and example archive file.' }
$tests.Add([pscustomobject]@{ name = 'accepts matching source and snapshot'; status = 'passed' })
$examplePath = Join-Path (Join-Path $fixtureRoot $sourceDirectory) 'examples/toggle/main.ts'
[IO.File]::WriteAllText($examplePath, 'modified example')
Assert-Rejected 'modified example' 'source differs from archive'
[IO.File]::WriteAllText($examplePath, 'original example')
& $verifier -WorkspaceRoot $fixtureRoot | Out-Null
$snapshotPath = Join-Path (Join-Path $fixtureRoot $sourceDirectory) 'packages/core/test/__snapshots__/case.snap'
[IO.File]::WriteAllText($snapshotPath, 'modified snapshot')
Assert-Rejected 'modified snapshot' 'source differs from archive'
[IO.File]::WriteAllText($snapshotPath, 'original snapshot')
& $verifier -WorkspaceRoot $fixtureRoot | Out-Null
$sourcePath = Join-Path (Join-Path $fixtureRoot $sourceDirectory) 'packages/core/src/index.ts'
[IO.File]::Delete($sourcePath)
Assert-Rejected 'missing source' 'source missing'
[IO.File]::WriteAllText($sourcePath, 'export const value = 1;')
& $verifier -WorkspaceRoot $fixtureRoot | Out-Null
[IO.File]::AppendAllText($archivePath, 'tampered')
Assert-Rejected 'modified archive' 'archive checksum mismatch'
$summary = [pscustomobject]@{ total = $tests.Count; passed = $tests.Count; tests = $tests.ToArray() }
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $workspace 'tmp/xstate-parity/source-verifier-controls.json') -Encoding utf8NoBOM
$summary | ConvertTo-Json -Compress -Depth 5
