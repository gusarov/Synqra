# Synqra Agent Notes

## Overview

Synqra is an event-sourced state-management and CQRS framework.

This repository is also used as a **submodule of Quotaly**.

## Branch Workflow

- Active development for the Quotaly-integrated flow happens on `master-quotaly`.
- Treat `master-quotaly` as the working branch for new changes unless explicitly told otherwise.
- `master` remains the upstream/mainline branch that changes will eventually be merged into.
- Be careful when discussing or preparing merges: in this repo, branch choice is part of the intended workflow, not an incidental local preference.

### PR push + CI tracking

- After pushing a commit to a PR branch, do **not** poll `gh pr checks` synchronously in a loop
  that blocks the conversation — the Azure Pipelines build regularly takes 2-3+ minutes, and the
  GitHub→Azure sync itself can lag well beyond that on top (observed: a pushed commit not
  reflected in the PR's `head_sha` for 10+ minutes despite the git ref already being correct on
  the remote).
- Track the build in the background instead — a background subagent or background task/wakeup —
  so the user's next messages get handled immediately rather than queuing behind a poll loop, and
  surface the result (pass/fail, and the failure log if it's red) when it actually completes.
- If a push doesn't trigger a build within a few minutes, check both ends before assuming
  something is broken: `git fetch` + `git log` on the remote branch (did the push really land?)
  and `gh api repos/<owner>/<repo>/pulls/<n>` for `head_sha` (has GitHub's PR object caught up?).
  A mismatch between the two is a sync-lag symptom, not a push failure — don't force-push again or
  re-trigger anything on the GitHub/Azure side without checking which end is actually stale first.

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

## Git Worktree Setup Gotcha (submodule + linked worktrees)

**Do not start an agent session rooted at this repo (`external/Synqra`) with a "create worktree"
option enabled.** Synqra is a git submodule of Quotaly, and its main checkout is an *absorbed*
gitdir (`external/Synqra/.git` is a file, not a directory). Absorbed submodule gitdirs rely on an
explicit `core.worktree` setting to know where their one true working tree lives. When a worktree
is created directly against this submodule's gitdir (`Quotaly/.git/modules/external/Synqra/...`)
instead of against the Quotaly superproject, the linked worktree inherits that `core.worktree`
setting and resolves it relative to its own (deeper) `$GIT_DIR` — landing one directory short, at
the bare `.git/modules/...` path instead of the real worktree directory. The working directory then
appears to contain nothing but `.git`/`.claude`, and `git status` lists git-internal plumbing files
(`objects/`, `refs/`, `hooks/`, etc.) as "untracked" — even though the index and HEAD are correct and
no data was lost.

**If you are an agent and detect you are operating in a linked worktree rooted at this submodule**
(working directory matches `external/Synqra/.claude/worktrees/<name>` or similar, i.e. the session
was *not* rooted at the Quotaly superproject) — **stop and tell the user**, rather than silently
working around it. Worktrees created against the superproject (`Quotaly` itself) do not hit this
bug, because git computes a fresh, correct `core.worktree` per superproject-worktree instead of
inheriting a shared one. Ask the user to recreate the session rooted at the Quotaly superproject
with the worktree option there instead.

If you must continue in a broken submodule-rooted worktree anyway (e.g. user asks you to proceed),
the one-time per-worktree fix is:

```
git config --worktree core.worktree "<absolute-path-to-this-worktree>"
git checkout -- .
```

This only touches this worktree's `config.worktree` (requires `extensions.worktreeConfig = true`,
already set) — it does not affect the main checkout or other worktrees.

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

## Code Style / Formatting

These mirror Quotaly's `AGENTS.md` (this repo is co-developed with it). Core principle:
**every logical item must be independently changeable without touching adjacent lines** —
minimal, auto-merge-safe diffs.

- **Indentation is TABS, not spaces.** There is no `.editorconfig`, so the convention is
  implicit — match the surrounding files. New `.cs` files written with 4-space indentation
  are wrong and will need converting; editors that auto-insert spaces must be configured to
  use tabs for this repo. (Quotaly, which consumes this submodule, follows the same tab convention.)
- **All `if` / `while` / `for` / `foreach` statements MUST have braces `{ }`, even for
  single-line bodies.** Braces go on their own line (Allman). No `if (x) return;` one-liners.
- **Multiline lists use a trailing comma where the language allows it** (collection and object
  initializers, attributes, enum members) — Style A. **Where a trailing comma is illegal**
  (parameter / argument lists), use a **leading comma** — Style B: the comma goes at the start
  of each continuation line, last line has no comma.
- **Closing brackets / parentheses go on their own line** when a call or initializer spans
  multiple lines, so elements can be added/removed without editing a neighbouring line.
- **Boolean chains put the operator at the start of the line** (`&& cond`, `|| cond`), often
  led by a `true`/`false` seed so every real clause is its own addable/removable line.
- Match the brace, spacing, and `using`-ordering style of the nearest existing file rather
  than imposing a different formatter.
- When a convenience property must NOT be persisted/serialized (e.g. a computed accessor on a
  `[SynqraModel]`), make it a read-only expression-bodied member. A `{ get; set; }` property is
  treated by the model-binding generator as a stored field and gets stamped into `[Schema]`
  (and emits a backing-field reference that may not exist).

## Testing Notes

- Tests use **TUnit**, not xUnit/NUnit.
- The default CI-oriented filter excludes tests marked with `[Property("CI", "false")]`.
- Performance tests are intentionally opt-in and should usually stay out of normal validation.
- The Docker `buildaot` stage is important. It publishes `Tests/Synqra.Tests` for `linux-x64` and runs the published binary, so AOT regressions matter.
- **MongoDB integration tests use Mongo2Go** (an ephemeral, self-contained `mongod`), mirroring
  Quotaly's `Quotaly.Features.Testing.IntegrationTests/IntegrationTestBase.cs`. Use a single
  static `MongoDbRunner` started once per process and reused (never disposed per-test; isolate
  via a unique database name) — per-test runners are slow and prone to port-rebind flakiness.
  Inject the connection string through configuration the same way the production DI binds it.
  These tests should **skip** (not fail) when the bundled `mongod` can't start. See
  `MongoAppendStorageTests`.
- **CI does provision a real mongod** — the Dockerfile copies `mongod` straight out of the
  `mongo:8.0` image and wraps the whole `dotnet test`/published-binary run in a `withmongo`
  script that starts it and exports `ConnectionStrings__Mongodb` first. There is no environment
  gap to work around: `[Property("CI", "false")]` on a Mongo-dependent test is therefore
  unjustified — don't add it, and don't assume an existing one is correct without checking the
  Dockerfile first. (An older note here claimed the opposite — "no Mongo service in CI" — which
  was true at some point but went stale; that's almost certainly why so many of these tags
  accumulated. `[NotInParallel]` is the actual constraint that still applies, since every
  Mongo-backed test in the suite shares that one mongod instance.)

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
