# Migration inspection & script generation

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: TBC (planned) · Tests: `tests/DotnetEfCoreMcp.Server.Tests/Migrations`

> **P1 #11 — Migration inspection and script generation.** This page documents the
> intended contract only; no implementation exists yet.

## Goal

Surface Entity Framework Core migration state and produce migration scripts from the
server-side connection, without exposing raw connection strings, provider internals, or
unredacted migration content. This complements `run_sql_query` (the existing opt-in raw
SQL escape hatch) by providing a *structured, provider-aware* path for migration inspection
and idempotent script generation.

## Context & rationale

EF Core migrations are tracked in `__EFMigrationsHistory`. The migration assembly exposes the
known migration IDs, `IHistoryRepository` reads applied IDs from a live relational database, and
`IMigrator` (`Microsoft.EntityFrameworkCore.Relational`) generates provider-specific scripts. The
MCP server already owns a `ConnectionRegistry`, `DbContext` factory, and
`RawSqlExecutionOptions` gating; P1 #11 reuses those primitives rather than introducing a
parallel execution path.

`run_sql_query` is the current fallback for migrations (see
[README "Run SQL queries"](../../README.md#run-sql-queries)), but it requires the agent to
hand-author SQL against `__EFMigrationsHistory` and to construct idempotent migration
scripts manually. P1 #11 makes that structured.

## Security model (must match existing conventions)

- Reuse `ConnectionRegistry` resolution: tools accept a logical `connectionName` (or fall
  back to the active connection). There is no parameter path that accepts a raw connection
  string from a client.
- Read-only safety: `list_migrations` is non-mutating. It reads the migration assembly's known
  IDs and, when a live database is available, reads applied IDs from
  `__EFMigrationsHistory`; see "Read-only safety" below. `generate_migration_script` produces
  SQL text only — it never opens a transaction, executes `Migrate()`, or calls `SaveChanges`.
- Connection gating: `generate_migration_script` is restricted to non-production,
  `ReadWrite`-capable development connections, mirroring `run_sql_query` gating
  (`RawSqlExecution:Enabled` is not consulted here; a separate `Migrations:Enabled` flag — see
  below — gates the script-generation tool instead). Production and `ReadOnly` connections
  are always rejected.
- Redaction: never log or return connection strings, parameter values, or provider exception
  detail. Error messages surface only stable, actionable text (see the
  [Connection management](./connections.md) redaction rules).

## Tool contract

Two tools are planned under P1 #11:

### `list_migrations`

Inspects EF Core migration state for a `DbContext`.

| Parameter | Type | Notes |
|---|---|---|
| `contextName` | `string` | CLR type name of the `DbContext`, as returned by `list_contexts`. |
| `connectionName?` | `string` | Logical connection name from the registry. If omitted, the active connection is used. |
| `migrationsAssembly?` | `string` | Simple assembly name or DLL path to use as the EF Core migrations assembly, when migrations live in a different assembly than `contextName`'s `DbContext`. See "Split-assembly migration discovery" below. |

Returns:

```jsonc
{
  "contextName": "MyAppContext",
  "connectionName": "dev",
  "appliedMigrations": [
    { "migrationId": "20240101000000_InitialCreate", "productVersion": "8.0.0" },
    // ... in application order
  ],
  "pendingMigrations": [
    { "migrationId": "20240102000000_AddUsers", "target": "SqlServer:8.0.0" }
  ],
  "databaseExists": true,
  "appliedStateAvailable": true
}
```

- The migration assembly supplies the known migration IDs in EF Core's migration ordering.
  When the target database is reachable, `IHistoryRepository` reads applied IDs from
  `__EFMigrationsHistory`; `pendingMigrations` is the ordered difference. The response must
  make unavailable applied state explicit rather than presenting metadata as applied state.
- `databaseExists` is `false` when the target database does not respond to a lightweight
  existence probe; in that case `appliedMigrations` is unavailable and `pendingMigrations`
  lists every known migration.
- Do not infer `isMigrationInProgress`: EF Core does not expose reliable state for a partial or
  failed migration. Omit this field until a provider-independent, read-only definition exists.

### `generate_migration_script`

Produces an idempotent SQL migration script, scoped between two migration IDs.

| Parameter | Type | Notes |
|---|---|---|
| `contextName` | `string` | CLR type name of the `DbContext`. |
| `connectionName?` | `string` | Logical connection name; falls back to active connection. Must be non-production. |
| `fromMigration?` | `string` | Migration ID to script from (exclusive). `null`/`0` means "from the beginning of history". |
| `toMigration?` | `string` | Migration ID to script to (inclusive). Defaults to the latest migration. |
| `idempotent?` | `boolean` | If `true`, generate a script safe to run on an already-applied database (`__EFMigrationsHistory`-guarded). Defaults to `true`. |
| `migrationsAssembly?` | `string` | Simple assembly name or DLL path to use as the EF Core migrations assembly, when migrations live in a different assembly than `contextName`'s `DbContext`. See "Split-assembly migration discovery" below. |

Returns:

```jsonc
{
  "contextName": "MyAppContext",
  "connectionName": "dev",
  "fromMigration": null,
  "toMigration": "20240102000000_AddUsers",
  "idempotent": true,
  "sql": "-- Script generated ... GO ...",
  "truncated": false,
  "migrationCount": 2
}
```

- The SQL is produced via `IMigrator.GenerateScript(fromMigration, toMigration,
  idempotent)`. It is a *preview* only: the call never executes the script, opens a
  transaction, or mutates the database.
- `sql` is capped and truncated per the
  [Query execution](./query-execution.md#server-side-safety) truncation policy (see
  "Script truncation" below). `truncated: true` indicates the script was cut short.
- Provider-specific syntax (`GO` batching, parameter naming) is returned verbatim; the tool
  never attempts to rewrite the provider's output.

## EF Core integration points

- `IMigrator` (`Microsoft.EntityFrameworkCore.Relational`) supplies
  `GenerateScript(...)`; it is resolved from the context's `GetService<IMigrator>()`.
- The migration assembly (`IMigrationsAssembly`) supplies the known migration IDs, and the
  relational `IHistoryRepository` supplies applied IDs when a live database is available.
- `IServiceCollection`/`DbContext` construction flows through the same `CreateContext` path
  used by `run_query`/`get_schema`, so migration inspection reuses the existing
  `ConnectionRegistry` and provider configuration — no new context factory.

## Split-assembly migration discovery

> **P1 #11a — implemented.** Code: [`AssemblyLoaderService.ResolveMigrationsAssembly`](../../src/DotnetEfCoreMcp.Server/AssemblyLoading/AssemblyLoaderService.cs) ·
> [`TargetAssemblyLoadContext.LoadAdditionalAssembly`](../../src/DotnetEfCoreMcp.Server/AssemblyLoading/TargetAssemblyLoadContext.cs) ·
> [`DbContextActivator`](../../src/DotnetEfCoreMcp.Server/DbContextDiscovery/DbContextActivator.cs) ·
> Tests: [`Migrations/SplitAssemblyMigrationDiscoveryTests.cs`](../../tests/DotnetEfCoreMcp.Server.Tests/Migrations/SplitAssemblyMigrationDiscoveryTests.cs).

Some solutions keep EF Core migrations in a different assembly than the `DbContext` type —
for example `AuthDbContext` defined in `OPG.DAL` while its migrations live in `OPG.AuthApi`.
By default, EF Core assumes the migrations live in the assembly that defines the `DbContext`
(or the startup assembly), so `list_migrations`/`generate_migration_script` would otherwise
report zero known migrations for these contexts. The optional `migrationsAssembly` parameter
on both tools resolves this by configuring EF Core's `.MigrationsAssembly(Assembly)` (the
type-safe, by-object overload — never the by-name `.MigrationsAssembly(string)` overload) with
an explicitly resolved `Assembly` instance.

**Resolution rules**, applied by `AssemblyLoaderService.ResolveMigrationsAssembly`:

- If `migrationsAssembly` looks like a path (contains a directory separator or ends in
  `.dll`), it is resolved as a file path, gated by the same `AssemblyLoader:AllowedRoots`
  allow-list used by `load_assembly` — a path outside the configured roots is rejected with a
  redacted error, never loaded.
- Otherwise, it is treated as a simple assembly name and resolved as a *dependency of the
  already-loaded context assembly* via that assembly's `AssemblyDependencyResolver`
  (`.deps.json`) — i.e. the migrations assembly must be a package/project reference reachable
  from the loaded target, the same way `Assembly.Load(AssemblyName)` would resolve it at
  runtime for that target.
- In both cases the resolved assembly is loaded into the *same* `AssemblyLoadContext` as the
  active `load_assembly` target (via `TargetAssemblyLoadContext.LoadAdditionalAssembly`,
  idempotent by simple name). This is required because EF Core associates a migration with its
  `DbContext` by CLR `Type` reference equality (`[DbContext(typeof(MyContext))]`); if the
  context type were loaded twice into two different load contexts, the migration would not be
  recognized as belonging to it.
- Failures (path outside `AllowedRoots`, file not found, or name not resolvable as a
  dependency) surface as redacted `McpException`s, matching every other assembly-loading
  failure path — no filesystem layout or dependency-resolution internals are echoed back.

**Scope — supported ("reverse") direction only.** The currently loaded `load_assembly` target
must be (or reference) the assembly containing the `DbContext` type; `migrationsAssembly` then
points at the *separate* assembly holding the migrations. This matches the motivating
`opg-systems` scenario: load `OPG.DAL` (or an assembly that references it) as the target, then
pass `migrationsAssembly: "OPG.AuthApi"` (or its DLL path) to `list_migrations`/
`generate_migration_script`. The reverse arrangement — loading the migrations assembly as the
primary `load_assembly` target and expecting the `DbContext` type to be discovered from one of
*its* dependencies — is **not** supported, because `DbContextScanner.FindDbContextTypes` only
scans the single loaded target assembly, not its dependency graph. Point `load_assembly` (or
`list_contexts`) at the assembly that actually defines the `DbContext` type.

When `migrationsAssembly` is omitted, behavior is unchanged from before P1 #11a: EF Core's
default migrations-assembly convention applies, and `list_migrations` faithfully reports zero
known migrations for a context whose migrations live elsewhere.

## Script truncation

`generate_migration_script` must not return unbounded SQL. The server enforces a
configurable character cap via the `Migrations:MaxScriptLength` option (default 100 000
characters), mirroring `QueryExecutionOptions.MaxTake`/`RawSqlExecutionOptions.MaxRows`. When
the generated script exceeds the cap, the `sql` field is truncated and `truncated: true` is
set; the tool never emits a partial statement without the truncation flag, and errors rather
than silently producing a misleadingly short script when truncation would bisect a
statement the caller depends on (the truncation boundary is best-effort on statement
boundaries).

## Read-only safety

- `list_migrations` reads `__EFMigrationsHistory` only indirectly: when a live connection is
  available, `IHistoryRepository` reads applied migration IDs; when not, only the migration
  assembly's known IDs are reported and `databaseExists` is `false`. No `INSERT`/`UPDATE`/
  `DELETE`/`CREATE` is ever issued by these tools.
- `generate_migration_script` never calls `IMigrator.Migrate()` or any
  `Database.ExecuteSqlRaw`/`BeginTransaction`. It produces a SQL string only.
- Both tools go through `using var context = CreateContext(...)` so the context is disposed
  deterministically and never holds a connection between calls.

## Configuration surface

Mirroring `RawSqlExecutionOptions`, a `MigrationsExecutionOptions` block is proposed under
the `Migrations` configuration section:

- `Migrations:Enabled` (boolean, default `false`) — must be explicitly enabled to use
  `generate_migration_script`. `list_migrations` is always available (read-only, no DDL).
- `Migrations:MaxScriptLength` (int, default 100 000) — character cap for generated SQL.
- `Migrations:CancellationMargin` (TimeSpan, default 5 s) — extra margin over the
  connection's command timeout when scripting.

## Open questions

- Should `list_migrations` be available on `ReadOnly` connections? (Read-only is consistent
  with non-mutation, but production databases may not tolerate even `SELECT` against
  `__EFMigrationsHistory` from an untracked context.) Tentative decision: allow on
  non-production `ReadOnly`; reject on `Production`.
- Whether to expose `fromMigration`/`toMigration` as opaque strings (migration IDs) or as
  structured `migrationId: string` objects. Current lean: opaque strings, matched via EF
  Core's own lookup, with the tool rejecting unknown IDs.
