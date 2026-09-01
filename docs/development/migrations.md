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
  `ReadWrite`-capable development connections, mirroring `run_sql_execute` gating
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
