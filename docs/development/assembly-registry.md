# Multi-target assembly registry

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: TBC (planned) · Tests: `tests/DotnetEfCoreMcp.Server.Tests/AssemblyLoading`

> **P2 #15 — Named multi-target assembly registry.** This page documents the intended
> contract only; no implementation exists yet.

## Goal

Let the server hold several compiled target assemblies loaded simultaneously, each
addressable by a stable logical name, instead of the single implicit target that
[Assembly loading](./assembly-loading.md) supports today. This lets an MCP client work
against more than one project (or more than one build configuration of the same project)
in the same server session without repeated `load_assembly` calls that discard the
previous target.

## Context & rationale

Today `AssemblyLoaderService` holds exactly one `LoadedAssemblyHandle` behind a `_current`
field: every `load_assembly` call unloads the previous collectible
`AssemblyLoadContext` (ALC) and replaces it. The `list_assembly_candidates` tool
description already frames this as "switch targets", and every downstream tool
(`list_contexts`, `get_schema`, `run_query`, `run_sql_query`) resolves against
`assemblyLoader.Current` with no target parameter at all. This mirrors the connection
registry's own history: `ConnectionRegistry` already supports multiple named entries with
one "active" selection and a per-call `connectionName` override (see [Connection
management](./connections.md)). P2 #15 applies that same named-registry shape to assembly
targets, so a client can, for example, keep a "Debug" and a "Release" build of a project
loaded at once, or hold two different projects' assemblies loaded side by side, and pick
one per call the same way `connectionName` already picks a connection per call.

## Configuration and tool-surface changes

- `AssemblyLoaderOptions` gains an optional `Targets` map (`Dictionary<string,
  AssemblyTargetOptions>`), where each entry has its own `Path` (or `AllowedRoots`
  override) and `AutoReloadEnabled`. Entries are purely a *registration convenience* —
  they seed the registry at startup — not a hard requirement; targets can still be
  registered at runtime via `load_assembly`. Server-wide `AssemblyLoader:AllowedRoots`
  continues to gate every path, whether supplied at startup or via a tool call; a
  per-target `AllowedRoots` override may only narrow, never widen, the server-wide list.
- `load_assembly` gains an optional `targetName: string` parameter. When supplied, the
  loaded assembly is registered/replaced under that name instead of replacing the
  server's single implicit target; omitting it preserves today's behavior exactly (see
  "Backwards compatibility" below).
- A new `list_loaded_assemblies` tool (analogous to `list_connections`, if one exists, or
  simply mirroring `list_assembly_candidates`'s discovery shape) returns every registered
  target's name, source path, load timestamp, and whether it is the current default —
  without re-scanning the filesystem.
- A new `select_target` tool (analogous to `swap_connection`) sets which registered name
  resolves when a tool call omits `targetName`. It only changes the *default*; it never
  unloads other registered targets.
- Every existing per-call tool that resolves an assembly-derived `DbContext` type
  (`list_contexts`, `get_schema`, `run_query`, `run_sql_query`) gains the same optional
  `targetName: string` parameter already used for `connectionName`, resolved the same way:
  omitted falls back to the current default target, an unknown name is a rejected,
  actionable error that does not enumerate other registered names.

## Named target selection

Target names follow the same selector shape already used for `connectionName`: an
opaque, case-sensitive, server-assigned or client-supplied logical string, never a
filesystem path. Resolution order for every tool call is: explicit `targetName` argument →
current default target (set at startup, or via `select_target`, or implicitly the first/
only registered target) → a rejected "no target selected" error when nothing has been
loaded yet, matching today's `RequireLoadedAssembly` failure mode. Registering a name that
already exists reloads that named target in place (same "stale/locked DLL" and unload-old-
ALC handling `AssemblyLoaderService.Load()` already performs), it does not create a second
entry under the same name and does not disturb other registered targets.

## Isolation/caching/lifecycle

- **Isolation.** Each named target gets its own isolated, collectible
  `AssemblyLoadContext` and its own `AssemblyDependencyResolver`, exactly as the single
  current target does today — the registry becomes a `Dictionary<string,
  LoadedAssemblyHandle>` (or equivalent) instead of a single `_current` field, still
  guarded by the existing `_gate` lock (or a per-entry lock, to avoid one target's reload
  blocking calls against another). Types from one named target's ALC are never shared with
  or resolved against another's; a `contextName` passed with one `targetName` must resolve
  only within that target's assembly.
- **Caching.** `SchemaCache` already keys on the CLR `Type` object via a
  `ConditionalWeakTable`, and distinct ALCs produce distinct `Type` identities even for
  same-named types, so per-target isolation is a natural consequence of the existing cache
  design — no cache key changes are needed, but cache entries for a target that is
  unregistered/replaced become unreachable (and are dropped) only once that target's old
  ALC is actually unloaded and collected, same as today's single-target reload behavior.
- **Lifecycle.** `AssemblyReloadWatcher` becomes scoped per named target: each registered
  target with `AutoReloadEnabled` gets its own `FileSystemWatcher` (or the watcher is
  extended to multiplex several watched files), debouncing and reload-in-place per name
  exactly as it does today for the single target. Explicitly unregistering a named target
  (a new `unload_assembly`-style operation, or re-registering nothing under that name) must
  stop its watcher and unload its ALC; the default-target pointer must be reassigned (or
  cleared, requiring an explicit `select_target`/`load_assembly` before further calls) if
  the removed name was the current default.

## Backwards compatibility

Calling `load_assembly` without `targetName`, and calling `list_contexts`/`get_schema`/
`run_query`/`run_sql_query` without `targetName`, must behave exactly as they do today:
a single implicit target is loaded/replaced and used. Concretely, the implicit
single-target behavior is modeled as an unnamed call always targeting (and replacing) the
current default entry — by default an internal reserved name — so existing MCP clients
that never pass `targetName` see no behavior change, no new required parameters, and no
change to existing tool response shapes. The registry is strictly additive: nothing in P2
#15 removes the "replace the currently loaded assembly" semantics that `load_assembly`'s
tool description already documents; it only adds an opt-in way to keep more than one
target around at once.

## Access-policy interaction

The per-connection `AccessPolicy` (P0 #9; see [Connection management](./connections.md))
stays scoped to connections, not assembly targets — a named assembly target only supplies
which `DbContext`/entity CLR types exist to resolve `contextName` against; it is the
selected *connection* whose `AccessPolicy` allow/deny lists gate which of those resolved
context/entity names a call may actually use. Practically: `targetName` and
`connectionName` are resolved independently (target picks the loaded assembly/`DbContext`
type universe; connection picks the database plus its policy), and policy enforcement is
unchanged — it still runs after both are resolved, using the exact `DbContext` full name
and entity name resolved from the selected target's ALC, and still fails closed on missing/
malformed policy or a denied selector without disclosing other target's or connection's
permitted names. No `AccessPolicy` field is added to `AssemblyTargetOptions`; combining a
policy-restricted connection with a switched assembly target is expected to reduce, never
expand, what a call may reach, since both checks must independently pass.

## Focused validation

Cover, with focused tests: registering and resolving multiple named targets
simultaneously; type/ALC isolation between two targets (same type name in each does not
cross-resolve); name-collision reload-in-place semantics versus creating a duplicate
entry; unknown-`targetName` rejection without leaking other registered names;
unregistering the current default target and the required re-selection before further
calls succeed; per-target `AutoReloadEnabled` reload/debounce behavior without affecting
other registered targets; the omitted-`targetName` backwards-compatibility path across
`load_assembly`, `list_contexts`, `get_schema`, `run_query`, and `run_sql_query`; and
composition with connection `AccessPolicy` (a resolved context/entity from a non-default
target is still denied/allowed exactly per the selected connection's policy, independent of
which target supplied the type).
