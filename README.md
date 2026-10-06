# XState .NET

[![Tests and examples](https://github.com/nguyenvanduocit/xstate-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/nguyenvanduocit/xstate-dotnet/actions/workflows/ci.yml)

A native C# / .NET 8 port in progress of [XState](https://github.com/statelyai/xstate) 5.33.2, pinned to commit `fbee62e7c1586315ed478c2fedf530d7e0ff5a3e`. The runtime has no JavaScript, UI or game-host dependency. MIT licensed; upstream attribution is retained.

**Incomplete port, not an official Stately release.** Passing CI covers the implemented subset. Runtime cases, compiler contracts and executable examples still have pending work. See the [status and evidence](src/XState/PORT-STATUS.md) and [known review findings](src/XState/CONTINUING.md) before adopting it.

## Build the library

Install the .NET 8 SDK selected by global.json, then run:

```powershell
dotnet build src/XState/XState.csproj -c Release
```

The native library targets net8.0. The full upstream comparison harness currently requires **Windows x64, PowerShell 7, .NET 8 SDK and Bun**. Other host operating systems have not been verified by this pipeline.

## Test and run examples

From this repository root:

```powershell
bun run setup
bun run test
dotnet run --project src/XState.Examples/XState.Examples.csproj -c Release -- workflow-hello
```

Setup downloads and verifies the pinned upstream source archive and Node binary, then installs locked test dependencies into ignored data/. Test always runs source/inventory controls, the original upstream suite, lint/build, native runtime/resource checks, JavaScript differential tests, executable example behavior and CLI checks, and compiler contracts. These projects use console test runners; `dotnet test` does not run their suites.

`bun run test:complete` additionally requires 100% parity and fails while any required case is pending. See the [harness guide](src/XState.Tests/README.md) for focused commands and [examples guide](src/XState.Examples/README.md) for available entrypoints. Console dialogue examples are included; interactive UI examples are explicitly excluded from scope.

Do not run two pipelines concurrently in the same checkout: they share tmp/xstate-parity/. Reports and logs are disposable; persist verified results and run IDs in the status document.

## CI and build artifacts

GitHub Actions runs the complete implemented test/example pipeline on pushes to main, pull requests and manual dispatches using a Windows runner. Test failures fail CI. Coverage that has not yet been ported remains visible in the job summary and reports; a manual `require_complete` input enables the strict coverage gate without repeating the suites.

Every run uploads available JSON evidence and logs for 14 days, including failed runs. Successful runs also pack the native library as a NuGet artifact for inspection. This workflow does not publish to NuGet.org or deploy to an application.

## Continue development

- [Runtime API](src/XState/README.md)
- [Next steps, regressions and review findings](src/XState/CONTINUING.md)
- [Architectural decisions and trade-offs](src/XState/DECISIONS.md)
- [Port history and verified coverage](src/XState/PORT-STATUS.md)
- [Contributor instructions](AGENTS.md)

The detailed port journal is currently written in Vietnamese. Source projects live under src/, repository tooling under tools/, and all required configuration lives here. The repository can be cloned and built without its original enclosing workspace.
