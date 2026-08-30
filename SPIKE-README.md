# Schemastery/Cosmokit Phase 0 spike

Minimal .NET 10 ports of the vendored Schemastery and Cosmokit libraries
(Phase 0 of the conversion plan in `plan/`), on branch
`port/dotnet10-schemastery`.

## Layout

```
src/Cordis/Cordis.Schemastery/   config schema DSL + validation (net10.0)
src/Cordis/Cordis.Cosmokit/      branded ids, path/home, timeout, retention,
                                 and general utilities (net10.0)
tests/Cordis.Tests/              zero-dependency console assertion suite
build-and-test.ps1               sandbox-safe manual build + test runner
```

No solution file is created here; a separate integration agent owns the
`.slnx`.

## Public API (summary)

- `Cordis.Schemastery.Schema` — immutable schema nodes. Factories: `Any`,
  `Never`, `Const`, `String`, `Number`, `Natural`, `Percent`, `Boolean`,
  `Array`, `Dict`, `Tuple`, `Object`, `Union`, `Intersect`, `Transform`,
  `Lazy`, `From`. Builders: `Required`, `Default`, `Min`, `Max`, `Step`,
  `Pattern`, `Role`, `Link`, `Comment`, `Description`, `Hidden`, `Loose`,
  `Disabled`, `Collapse`, `Deprecated`, `Experimental`, `Set`, `Push`,
  `Extra`. Entry points: `Validate` (throws `ValidationError` with structured
  `Path`), `TryValidate` (returns `SchemaValidationResult`), `Resolve`
  (registry dispatch, `RegisterType` for custom types).
- `Cordis.Cosmokit.BrandedId<TBrand>` / `Brand` — nominal string ids (the
  `Branded<B>` / `brandString` equivalent).
- `Cordis.Cosmokit` misc: `Misc`, `Deep` (clone/deepEqual), `Binary`, `Time`,
  `Strings`, `Arrays` (ports of cosmokit modules); `HomePaths`,
  `WorkspacePaths` (ports of `dsh-home-paths` / `dsh-util-workspace-path`);
  `TimeoutReason`/`Deadline`/`Timeout` (port of `dsh-timeout`);
  `ItemRetainer<T>`/`TextRetainer` + notice helpers (port of
  `dsh-output-retention`).

## Build and test

On an unrestricted host:

```
dotnet build src/Cordis/Cordis.Cosmokit/Cordis.Cosmokit.csproj
dotnet build src/Cordis/Cordis.Schemastery/Cordis.Schemastery.csproj
dotnet run --project tests/Cordis.Tests
```

Both library projects compile cleanly under native `dotnet build`
(0 warnings, 0 errors; verified against the committed tree). The sandbox,
however, intermittently blocks MSBuild's Csc task (it spawns the compiler
with captured output; MSB3883 "Access is denied"), and network access to
nuget.org is blocked from this environment. `dotnet test` with xUnit cannot
be restored here: the dependency closure needs `System.Reflection.Metadata
8.0.0`, which is absent from every local NuGet cache (the global cache has
6.0.1/7.0.0; the VS offline feed has <= 1.6.0). Per the Phase 0 task's
fallback rule, the test project is therefore a zero-dependency console
assertion suite; `build-and-test.ps1` compiles each project directly with
`csc` via response files and runs it (37 assertions, exits non-zero on any
failure). The xUnit packages (xunit 2.9.3, Microsoft.NET.Test.Sdk 17.14.1,
xunit.runner.visualstudio 3.1.4) are cached locally; a networked host can
restore them and switch the test project back to `dotnet test` with a
one-line csproj change.

## Deviations from the TypeScript sources

Documented in the Phase 0 report; the notable ones are: CLR numbers keep
their concrete type (int stays int) while range/step checks use doubles; the
`Schema.Dict` property is named `PropertySchemas` to avoid colliding with the
`Schema.Dict(...)` factory; builder `Set`/`Push` return a new node instead of
mutating; JS regex flags map to .NET `RegexOptions` (`i`/`m`/`s`); autofix on
a list element writes `null` instead of leaving a JS hole; `IsPlainObject`
accepts any non-list object (dictionaries and POCOs via property
reflection); `i18n`, `toJSON`, `bitset`, `date`, `regExp`, `function`, `is`,
and `arrayBuffer` schema types are deferred to Phase 1.

