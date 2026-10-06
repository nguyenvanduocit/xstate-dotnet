#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$pin = Get-Content (Join-Path $root 'src/XState/upstream.json') -Raw | ConvertFrom-Json
$data = Join-Path $root 'data/library-source'
New-Item -ItemType Directory -Force $data | Out-Null
$archive = Join-Path $data ('xstate-' + $pin.commit + '.zip')
$source = Join-Path $root $pin.sourceDirectory
if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest $pin.archive -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $pin.archiveSha256) { throw 'XState archive checksum mismatch.' }
if (-not (Test-Path -LiteralPath $source)) { Expand-Archive -LiteralPath $archive -DestinationPath $data }
$nodeVersion = (Get-Content (Join-Path $source '.node-version') -Raw).Trim()
$nodeArchiveName = 'node-v' + $nodeVersion + '-win-x64.zip'
$nodeArchive = Join-Path $data $nodeArchiveName
$nodeDirectory = Join-Path $data ('node-v' + $nodeVersion + '-win-x64')
$release = 'https://nodejs.org/dist/v' + $nodeVersion + '/'
if (-not (Test-Path -LiteralPath $nodeArchive)) { Invoke-WebRequest ($release + $nodeArchiveName) -OutFile $nodeArchive }
$sums = (Invoke-WebRequest ($release + 'SHASUMS256.txt')).Content
$expectedLine = $sums -split '\r?\n' | Where-Object { $_.EndsWith('  ' + $nodeArchiveName, [StringComparison]::Ordinal) }
if (-not $expectedLine) { throw 'Node archive checksum was not published.' }
$expectedHash = ($expectedLine -split '\s+')[0]
if ((Get-FileHash -LiteralPath $nodeArchive -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Node archive checksum mismatch.' }
if (-not (Test-Path -LiteralPath (Join-Path $nodeDirectory 'node.exe'))) { Expand-Archive -LiteralPath $nodeArchive -DestinationPath $data }
$runner = Join-Path $data 'xstate-test-tools'
New-Item -ItemType Directory -Force $runner | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'package.json'),(Join-Path $PSScriptRoot 'package-lock.json') -Destination $runner
& (Join-Path $nodeDirectory 'node.exe') (Join-Path $nodeDirectory 'node_modules/npm/bin/npm-cli.js') ci --prefix $runner --ignore-scripts --no-audit --no-fund
if ($LASTEXITCODE -ne 0) { throw 'Failed to install upstream test dependencies.' }
$modules = Join-Path $source 'node_modules'
$target = Join-Path $runner 'node_modules'
if (-not (Test-Path -LiteralPath $modules)) { New-Item -ItemType Junction -Path $modules -Target $target | Out-Null }
elseif ((Get-Item -LiteralPath $modules).Target -ne $target) { throw 'Existing upstream node_modules does not point to the pinned runner.' }
Write-Output ('Ready: XState ' + $pin.version + ', Node ' + $nodeVersion)
