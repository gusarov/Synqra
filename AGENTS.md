# Synqra Agent Notes

## Overview

Synqra is an event-sourced state-management and CQRS framework.

This repository is also used as a **submodule of Quotaly**.

## Branch Workflow

- Active development for the Quotaly-integrated flow happens on `master-quotaly`.
- Treat `master-quotaly` as the working branch for new changes unless explicitly told otherwise.
- `master` remains the upstream/mainline branch that changes will eventually be merged into.
- Be careful when discussing or preparing merges: in this repo, branch choice is part of the intended workflow, not an incidental local preference.

The main runtime flow is:

1. Commands are submitted to a store or projection.
2. Commands emit events.
3. Events are appended to storage.
4. Projections and object stores replay/apply events to materialize state.

Core abstractions you will see repeatedly:

- `IAppendStorage<T, TKey>`: append-log style storage, primarily used for events.
- `IBlobStorage<TKey>`: lower-level blob persistence used by append storage adapters.
- `IProjection`: event processor / projection surface.
- `IObjectStore`: object-aware store that tracks live model instances.
- `ISbxSerializer`: custom binary serializer used across storage and transport.
- `ITypeMetadataProvider`: runtime type/collection metadata registry.

## Solution Map

- `Synqra.Model`
  Core commands, events, visitors, IDs, type metadata, JSON converters.
- `Synqra`
  Runtime glue: replication, network serialization, shared services.
- `Synqra.BinarySerializer` and `Synqra.BinarySerializer.Abstractions`
  SBX serializer and schema/versioning support.
- `Synqra.CodeGeneration`
  Source generator / analyzer used for bindable model support and generated bindings.
- `Synqra.AppendStorage.*`
  Append storage implementations and adapters.
  Current active implementations include `JsonLines` and `BlobStorage`.
- `Synqra.BlobStorage.*`
  Blob backends such as file, SQLite, IndexedDB, and MongoDB.
- `Synqra.Projection.*`
  Projection/object-store implementations for in-memory, file, SQLite, etc.
- `Synqra.Utils`
  Shared utilities used across the solution.
- `Tests/Synqra.Tests`
  Main TUnit test suite, including AOT-sensitive integration coverage.
- `Tests/Synqra.Tests.TestHelpers`
  Shared test infrastructure.
- `Contoso`
  Example/demo app spanning model, projection, web host, WASM, and Playwright.

## Toolchain And Build

- SDK is pinned by `global.json` to `.NET SDK 10.0.100`.
- The solution is multi-targeted in several places, especially `net8.0`, `net9.0`, and `net10.0`.
- `Directory.Build.props` enables nullable reference types, latest language version, source-generated config binding, and AOT-compatible settings where possible.

Useful commands:

- `dotnet build -c Release`
- `dotnet test Tests/Synqra.Tests -c Release -- --treenode-filter "/*/*/*[(Category!=Performance)&(CI!=false)]/*[(Category!=Performance)&(CI!=false)]"`
- `docker build --target test --progress=plain .`
- `docker build --target buildaot --progress=plain .`
- `aot.cmd`
  Windows helper that publishes and runs the AOT test executable.
- `migrate.cmd <MigrationName>`
  Updates EF migrations/scripts for `Synqra.Projection.Sqlite` and the test project.

## Testing Notes

- Tests use **TUnit**, not xUnit/NUnit.
- The default CI-oriented filter excludes tests marked with `[Property("CI", "false")]`.
- Performance tests are intentionally opt-in and should usually stay out of normal validation.
- The Docker `buildaot` stage is important. It publishes `Tests/Synqra.Tests` for `linux-x64` and runs the published binary, so AOT regressions matter.

## AOT And Serialization Constraints

- Keep **System.Text.Json** compatibility in mind when changing models, events, or storage formats.
- Native AOT compatibility is a real requirement, not an aspiration. The test project publishes with `PublishAot=true`.
- `SignalR` was intentionally removed from the sync path because it did not work well in the Native AOT scenario.
- If you touch binary serialization, schema evolution, or type discovery, review:
  - `Synqra.BinarySerializer/readme.md`
  - `Synqra.AppendStorage.Abstractions/README.md`
  - `Synqra.Projection.Sqlite/README.md`

## Change Guidance

- Prefer minimal, targeted changes. A lot of code is cross-cutting across storage, projection, generator, and AOT paths.
- When touching synchronization/background-host tests, do not let them run loose in parallel:
  - use `[NotInParallel]` when a test spins servers, sockets, or shared background workers
  - dispose hosts/nodes explicitly in teardown
- Be careful with replay ordering. A number of tests assume event order is preserved across append storage and projection replay.
- Generated files, schema attributes, migration scripts, and compiled EF artifacts should only be updated when the change actually requires them.

## Good First Read Before Editing

- `readme.md`
- `Dockerfile`
- `Directory.Build.props`
- `Tests/Synqra.Tests/Synqra.Tests.csproj`
- the project-specific README nearest the subsystem you are changing
