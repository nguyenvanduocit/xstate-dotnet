# XState .NET contributor rules

This is a standalone native .NET 8 port of the XState commit pinned in src/XState/upstream.json.
Start with src/XState/CONTINUING.md and src/XState/DECISIONS.md. Keep MIT attribution.

- Use src/ for the runtime, console test runners and executable examples; tools/ owns repository lint.
- No dependency on an enclosing repository, game host, UI framework or JavaScript runtime in the library.
- Run bun run setup before the first test run on Windows x64 (.NET 8 SDK, PowerShell 7, Bun).
- After each C# edit run bun run lint:csharp -Project <src/project.csproj>; shared source/analyzers require bun run lint:csharp for all four projects.
- Run bun run test after behavior or harness changes. These are console runners, not dotnet test projects.
- Keep upstream test IDs and assertions. Unsupported cases stay pending. Do not narrow inventory or weaken expected results.
- bun run test:complete additionally requires full coverage and is expected to fail while parity remains incomplete.
- Never run two pipelines against the same tmp/xstate-parity directory. Read run-evidence.json and parity-report.json together.
- Record decisions, regressions, verified counts, run IDs and performance limits in the existing docs.
- data/ and tmp/ are disposable local dependencies/results and must not be committed. Never commit credentials or machine-specific paths.
