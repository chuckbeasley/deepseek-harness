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

The host sandbox blocks `dotnet build`/`dotnet test` (MSBuild's Csc task
spawns the compiler with captured output, which the sandbox denies; restore
itself works offline). `build-and-test.ps1` compiles each project directly
with `csc` via response files and runs the console assertion suite, which
exits non-zero on any failure.

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
