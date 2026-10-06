#Requires -Version 7.0
[CmdletBinding()]
param([switch] $RequireComplete)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
Push-Location $root
$runStarted = $false
try {
    $pin = Get-Content 'src/XState/upstream.json' -Raw | ConvertFrom-Json
    $version = (Get-Content (Join-Path $pin.sourceDirectory '.node-version') -Raw).Trim()
    $node = Join-Path $root ('data/library-source/node-v' + $version + '-win-x64/node.exe')
    if (-not (Test-Path -LiteralPath $node)) { throw 'Run tools/Setup-Upstream.ps1 first.' }
    New-Item -ItemType Directory -Force 'tmp/xstate-parity' | Out-Null
    & $node (Join-Path $PSScriptRoot 'run-evidence.mjs') begin
    if ($LASTEXITCODE -ne 0) { throw 'Could not start execution evidence.' }
    $runStarted = $true
    & $node --test (Join-Path $PSScriptRoot 'run-evidence.test.mjs') (Join-Path $PSScriptRoot 'run-example-process.test.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Execution harness controls failed.' }
    & (Join-Path $PSScriptRoot 'Verify-Upstream.ps1')
    & $node (Join-Path $PSScriptRoot 'run-evidence.mjs') capture source
    if ($LASTEXITCODE -ne 0) { throw 'Source evidence failed.' }
    & (Join-Path $PSScriptRoot 'Test-UpstreamVerification.ps1')
    & $node (Join-Path $PSScriptRoot 'examples.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Example inventory failed.' }
    & $node --test (Join-Path $PSScriptRoot 'examples.test.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Example execution gate controls failed.' }
    & $node (Join-Path $PSScriptRoot 'inventory.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Inventory failed.' }
    & $node --test (Join-Path $PSScriptRoot 'type-assertions.test.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Compiler inventory controls failed.' }
    & $node (Join-Path $PSScriptRoot 'extract-parallel-fixtures.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Parallel fixture extraction failed.' }
    & $node --test (Join-Path $PSScriptRoot 'extract-parallel-fixtures.test.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Parallel fixture extraction controls failed.' }
    & $node (Join-Path $PSScriptRoot 'extract-graph-paths.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Graph path snapshot extraction failed.' }
    & $node --test (Join-Path $PSScriptRoot 'extract-graph-paths.test.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Graph path snapshot extraction controls failed.' }
    & $node (Join-Path $PSScriptRoot 'translate-data-tests.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Test translation failed.' }
    & $node --test (Join-Path $PSScriptRoot 'translate-data-tests.test.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Test translation controls failed.' }
    & $node (Join-Path $PSScriptRoot 'extract-transition-tables.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Transition table extraction failed.' }
    & $node --test (Join-Path $PSScriptRoot 'extract-transition-tables.test.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Transition table extraction controls failed.' }
    & $node 'data/library-source/xstate-test-tools/node_modules/vitest/vitest.mjs' run --config 'src/XState.Tests/tools/upstream.vitest.mjs' *> 'tmp/xstate-parity/upstream-run.log'
    if ($LASTEXITCODE -ne 0) { throw 'Upstream baseline failed; see tmp/xstate-parity/upstream-run.log.' }
    & $node (Join-Path $PSScriptRoot 'run-evidence.mjs') capture upstream
    if ($LASTEXITCODE -ne 0) { throw 'upstream execution evidence failed.' }
    & bun run lint:csharp -Project 'src/XState.Tests/XState.Tests.csproj'
    if ($LASTEXITCODE -ne 0) { throw 'C# lint failed.' }
    & dotnet 'tmp/csharp-lint/XState.Tests/XState.Tests/XState.Tests.dll' 'tmp/xstate-parity/csharp-results.json'
    if ($LASTEXITCODE -ne 0) { throw 'C# parity tests failed.' }
    & $node (Join-Path $PSScriptRoot 'run-evidence.mjs') capture native
    if ($LASTEXITCODE -ne 0) { throw 'native execution evidence failed.' }
    & $node 'data/library-source/xstate-test-tools/node_modules/vitest/vitest.mjs' run --config 'src/XState.Tests/tools/reference.vitest.mjs' *> 'tmp/xstate-parity/reference-run.log'
    if ($LASTEXITCODE -ne 0) { throw 'Runtime JS/C# differential checks failed; see reference-run.log.' }
    & $node (Join-Path $PSScriptRoot 'run-evidence.mjs') capture differential
    if ($LASTEXITCODE -ne 0) { throw 'differential execution evidence failed.' }
    & bun run lint:csharp -Project 'src/XState.Examples.Tests/XState.Examples.Tests.csproj'
    if ($LASTEXITCODE -ne 0) { throw 'Example C# lint failed.' }
    & dotnet 'tmp/csharp-lint/XState.Examples.Tests/XState.Examples.Tests/XState.Examples.Tests.dll' 'tmp/xstate-parity/csharp-examples.json'
    if ($LASTEXITCODE -ne 0) { throw 'Native example behavior checks failed.' }
    & $node (Join-Path $PSScriptRoot 'Run-ExampleCli.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Native example entrypoints failed.' }
    & $node 'data/library-source/xstate-test-tools/node_modules/vitest/vitest.mjs' run --config 'src/XState.Tests/tools/examples.vitest.mjs' *> 'tmp/xstate-parity/examples-reference.log'
    if ($LASTEXITCODE -ne 0) { throw 'Upstream example execution/differential checks failed; see examples-reference.log.' }
    & $node (Join-Path $PSScriptRoot 'run-evidence.mjs') capture examples
    if ($LASTEXITCODE -ne 0) { throw 'examples execution evidence failed.' }
    & $node (Join-Path $PSScriptRoot 'Compile-Contracts.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'C# compiler assertions failed.' }
    & $node --test (Join-Path $PSScriptRoot 'compiler-contracts.test.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Compiler assertion gate controls failed.' }
    & $node (Join-Path $PSScriptRoot 'run-evidence.mjs') capture compiler
    if ($LASTEXITCODE -ne 0) { throw 'compiler execution evidence failed.' }
    & $node (Join-Path $PSScriptRoot 'run-evidence.mjs') finish
    if ($LASTEXITCODE -ne 0) { throw 'Execution evidence failed final validation.' }
    [string[]] $gate = if ($RequireComplete) { @('--require-complete') } else { @() }
    & $node (Join-Path $PSScriptRoot 'parity-report.mjs') @gate
    exit $LASTEXITCODE
}
catch {
    if ($runStarted) { & $node (Join-Path $PSScriptRoot 'run-evidence.mjs') fail $_.Exception.Message }
    throw
}
finally { Pop-Location }
