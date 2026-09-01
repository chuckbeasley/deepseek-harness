# .NET pipeline (Phase 1 foundations)

Build, test, and CI conventions for the .NET port. This is the Phase 1
tooling half of the conversion plan in `plan/`; phase context is in
`plan/04-execution-phases.md`, and the current 10-project solution is
`deepseek-harness.slnx`.

## Gate ladder

The .NET gate ladder, in the order CI runs it:

1. **Build** — `dotnet build deepseek-harness.slnx -c Release` compiles all 10
   projects (src libraries, the three console executables, and the test
   projects).
2. **Console runners** — the console-based suites run as plain executables.
   This is the current test path because some suites are console-based:
   - `dotnet run --project tests/Cordis.Tests/Cordis.Tests.csproj -c Release --no-build`
   - `dotnet run --project tests/Hsh.Spike.Tests/Hsh.Spike.Tests.csproj -c Release --no-build`
   - `dotnet run --project src/Hsh/Hsh.Spike/Hsh.Spike.csproj -c Release --no-build` (headless smoke)
3. **xUnit** — the framework test path:
   `dotnet test tests/Cordis.Core.Tests/Cordis.Core.Tests.csproj -c Release --no-build`
4. **Coverage** — a coverage gate (Coverlet plus a CI threshold) is a
   later-phase goal; Phase 1 stands up the runners, not the threshold. NuGet
   packaging follows the same later-phase schedule.

`tests/Cordis.Core.Tests.Runner/` is the sandbox-only console twin of the
xUnit suite: it has no csproj, is compiled with bare `csc` by the build
scripts, and its 37 assertions duplicate the xUnit project — CI exercises them
once, through `dotnet test`.

## Local runs

On an unrestricted host:

```
dotnet restore deepseek-harness.slnx
dotnet build deepseek-harness.slnx -c Release
dotnet test tests/Cordis.Core.Tests/Cordis.Core.Tests.csproj -c Release --no-build
dotnet run --project tests/Cordis.Tests/Cordis.Tests.csproj -c Release --no-build
dotnet run --project tests/Hsh.Spike.Tests/Hsh.Spike.Tests.csproj -c Release --no-build
dotnet run --project src/Hsh/Hsh.Spike/Hsh.Spike.csproj -c Release --no-build
```

## Sandbox caveat

Under the dev sandbox, `dotnet build` and `dotnet test` intermittently fail
with MSB3883 ("Access is denied"): MSBuild's Csc task spawns the compiler with
captured output, and the sandbox denies child processes that capture output
through pipes. Restore works and `csc` runs with inherited stdio, so the
sandbox path compiles each project directly with `csc` and runs the
zero-dependency console suites through the build-and-test scripts:

- `build-and-test.ps1` — slice (`Hsh.Spike` smoke + `Hsh.Spike.Tests`)
- `build-and-test-cordis.ps1` — `Cordis.Cosmokit` + `Cordis.Schemastery` + `Cordis.Tests`
- `build-and-test-cordis-core.ps1` — `Cordis.Core` + the console twin runner

Network access to nuget.org is also blocked from the sandbox, so restoring new
packages (xUnit and friends) must happen on an unrestricted host; the xUnit
packages are already in the local NuGet cache.

## Analyzer and formatting policy

`Directory.Build.props` and the `[*.cs]` section of `.editorconfig` define the
shared C# settings: .NET analyzers on at the latest-recommended level,
deterministic builds with CI determinism, and repo naming/formatting
conventions. Warnings are not errors yet — the Phase 0/1 spike carries CS1591
XML-doc gaps on public API; flip `TreatWarningsAsErrors` to `true` once the
port is doc-complete.
