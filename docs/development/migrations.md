# Migration inspection & script generation

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: [`src/DotnetEfCoreMcp.Server/Migrations`](../../src/DotnetEfCoreMcp.Server/Migrations), [`list_migrations`/`describe_migration`/`compare_schema`/`generate_migration_script` tools](../../src/DotnetEfCoreMcp.Server/Tools/EfCoreMcpTools.cs) · Tests: `tests/DotnetEfCoreMcp.Server.Tests/Migrations`

> **P1 #11 — Migration inspection and script generation.** Implemented, together with the
> P1 #11a split-assembly discovery and the P1 #11b migration and schema-drift diagnostics
> (`describe_migration`, `compare_schema`, `list_migrations` parity fields). This page is the
> canonical reference for all four migration tools; the design rationale for P1 #11b is in the
> [migration & schema-drift diagnostics design record](./migration-diagnostics.md).

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
  `__EFMigrationsHistory`; see "Read-only safety" below. Because it only reads, it is available on
  every connection, including production and `ReadOnly` connections. `generate_migration_script` produces
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

Four tools are provided under P1 #11 / #11b:

### `list_migrations`

Inspects EF Core migration state for a `DbContext`. Available on every connection, including
production and `ReadOnly` connections.

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
    { "migrationId": "20240102000000_AddUsers", "target": "SqlServer:8.0.0", "outOfOrder": false }
  ],
  "databaseExists": true,
  "appliedStateAvailable": true,
  "migrations": [
    { "migrationId": "20240101000000_InitialCreate", "applied": true },
    { "migrationId": "20240102000000_AddUsers", "applied": false }
  ],
  "knownMigrationCount": 2,
  "lastAppliedMigration": "20240101000000_InitialCreate",
  "unknownAppliedMigrations": [],
  "hasPendingModelChanges": false
}
```

The result mirrors `dotnet ef migrations list` and `dotnet ef migrations has-pending-model-changes`:

| Field | EF equivalent | Meaning |
|---|---|---|
| `migrations[]` | `dotnet ef migrations list`, `Database.GetMigrations()` | Every known migration in order. `applied` is omitted (null) when applied state is unavailable. |
| `knownMigrationCount` | `GetMigrations().Count()` | `0` usually means the wrong (or a missing) `migrationsAssembly`. |
| `lastAppliedMigration` | last of `GetAppliedMigrationsAsync()` | The last history row in order; omitted when nothing is applied or applied state is unavailable. The anchor for `generate_migration_script fromMigration`. |
| `unknownAppliedMigrations` | applied − known | History rows the loaded code doesn't know: the database is ahead of, or diverged from, the code. |
| `pendingMigrations[].outOfOrder` | none | `true` when a pending migration ID sorts (ordinally) before `lastAppliedMigration`, such as a migration merged from an older branch. |
| `hasPendingModelChanges` | `Database.HasPendingModelChanges()` | The model differs from the latest snapshot. Needs no database; omitted (null) when it can't be computed. |

- The migration assembly supplies the known migration IDs in EF Core's migration ordering.
  When the target database is reachable, `IHistoryRepository` reads applied IDs from
  `__EFMigrationsHistory`; `pendingMigrations` is the ordered difference. The response must
  make unavailable applied state explicit rather than presenting metadata as applied state.
- `databaseExists` is `false` when the target database does not respond to a lightweight
  existence probe; in that case `appliedMigrations` is unavailable and `pendingMigrations`
  lists every known migration.
- Do not infer `isMigrationInProgress`: EF Core does not expose reliable state for a partial or
  failed migration. Omit this field until a provider-independent, read-only definition exists.

### `describe_migration`

Returns the structured operations of one migration, so an agent can see what it does without
generating SQL. It never opens the database, so it's available on every connection, including
production and `ReadOnly` connections, and works when the database is unreachable.

| Parameter | Type | Notes |
|---|---|---|
| `contextName` | `string` | CLR type name of the `DbContext`. |
| `migrationId` | `string` | A full migration ID, or a unique case-insensitive name/suffix such as `AddCustomerEmail`. An ambiguous suffix is an error that lists the matches; an unknown ID points at `list_migrations`. |
| `connectionName?` | `string` | Used only to select the provider passed to `IMigrationsAssembly.CreateMigration`. Never opened. |
| `migrationsAssembly?` | `string` | Same resolution as `list_migrations`. |
| `direction?` | `string` | `up` (default) or `down`. Anything else is an error. |

Returns:

```jsonc
{
  "contextName": "MyAppContext",
  "migrationId": "20261113093414_AddCustomerEmail",
  "direction": "up",
  "operations": [
    { "kind": "AddColumn", "table": "Customers", "name": "Email", "columnType": "TEXT", "isNullable": true }
  ],
  "operationCount": 1
}
```

- Each operation is `{ kind, schema?, table?, name?, columnType?, isNullable?, defaultValueSql?,
  newName?, columns?, sql?, truncated? }`; null fields are omitted. `kind` is the EF Core operation
  type name without the `Operation` suffix (`CreateTable`, `AddColumn`, `RenameColumn`, …).
- `CreateTable` includes its `columns` (name, type, nullability, default SQL); `CreateIndex` and
  `AddForeignKey` include column names. Operation types without a dedicated projection are reported
  as `{ kind }` only.
- `Sql` operation bodies are capped by `Migrations:MaxScriptLength` and flagged `truncated: true`
  when cut short.
- `MigrationOperationIndex` (built from every known migration's `Up` operations, in order) maps a
  table or column to the latest migration that introduces that name (`CreateTable` and its columns,
  `AddColumn`, `RenameColumn`, `RenameTable`). It's the attribution source for `compare_schema`.

### `compare_schema`

Compares the `DbContext` model with the live database catalog. It embeds
[EfCore.SchemaCompare](https://github.com/JonPSmith/EfCore.SchemaCompare) (MIT, by Jon P Smith),
which reads the catalog through EF Core's reverse-engineering `IDatabaseModelFactory` (built via
`Microsoft.EntityFrameworkCore.Design`'s design-time services). Only the
`CompareEfWithDb(DbContext)` overload is used, on a context from the normal `CreateContext` path,
so connection strings never come from client input or `appsettings.json`. It reads catalog metadata
and `__EFMigrationsHistory` only, so it's available on every connection, including production and
`ReadOnly` connections.

| Parameter | Type | Notes |
|---|---|---|
| `contextName` | `string` | CLR type name of the `DbContext`. |
| `connectionName?` | `string` | Logical connection name; falls back to the active connection. |
| `migrationsAssembly?` | `string` | Same resolution as `list_migrations`; used for attribution. |
| `tables?` | `string[]` | Filters reported results by table name, schema-qualified or not, case-insensitively. The whole catalog is still read. |
| `includeExtras?` | `boolean` | Default `false`. When `true`, sets `AlwaysRunStage2` and reports `extraInDatabase` (tables, columns, and indexes the model doesn't map). Noisy on shared databases. |

Returns:

```jsonc
{
  "contextName": "SampleAppDbContext",
  "connectionName": "prod",
  "hasDifferences": true,
  "notInDatabase": [{ "kind": "Column", "table": "Customers", "column": "Email", "entity": "Customer",
                      "property": "Email", "introducedBy": "20261113093414_AddCustomerEmail",
                      "introducedByIsPending": true }],
  "different": [{ "kind": "Column", "table": "Orders", "column": "Amount", "entity": "Order",
                  "property": "Amount", "attribute": "ColumnType", "expected": "decimal(18,2)",
                  "found": "decimal(18,4)" }],
  "extraInDatabase": [],
  "comparedTableCount": 3,
  "truncated": false
}
```

- Entries are `{ kind, table, column?, name?, entity?, property?, attribute?, expected?, found?,
  introducedBy?, introducedByIsPending? }`; null fields are omitted. `kind` is `Table`, `Column`,
  `PrimaryKey`, `ForeignKey`, or `Index`; `name` is the constraint or index name; `table` is
  `schema.table` when the model maps a non-default schema.
- **Attribution.** `notInDatabase` tables and columns are looked up in `MigrationOperationIndex`
  (latest `CreateTable`/`AddColumn`/`RenameColumn`/`RenameTable` producing the name).
  `introducedByIsPending` is `true` when that migration isn't in `__EFMigrationsHistory`, and is
  omitted when history can't be read. A missing `introducedBy` means no migration creates the
  object: a model change without a migration (compare `hasPendingModelChanges`).
- Entities denied by the connection's access policy are never reported.
- Output is capped at `Migrations:MaxDriftEntries` entries in total (default 500), filled in
  `notInDatabase`, `different`, `extraInDatabase` order; `truncated: true` when capped.
- The library is synchronous, so it runs on a worker task. The tool stops waiting after the
  connection's `CommandTimeoutSeconds` plus `Migrations:CancellationMargin` and returns a timeout
  error; the abandoned worker still owns and disposes its context.
- On SQLite a missing database file is reported as an error rather than created, and empty
  constraint-name differences are ignored because SQLite's scaffolder can't read constraint names.
- Known comparison noise is dropped on every provider:
  - entities mapped with `ToSqlQuery` or to a table-valued function, which have no table of their own;
  - column types that differ only in case, such as `NVARCHAR(MAX)` vs. `nvarchar(max)`;
  - the database default that EF Core's own `AddColumn` adds to backfill a non-nullable column, together with the `ValueGenerated` Never/OnAdd mismatch that default causes.

Limitations (inherited from EfCore.SchemaCompare and EF Core's scaffolder):

- **Whole-catalog cost.** It always scaffolds the entire database before filtering; `tables` only
  narrows the report. Large catalogs may need a larger `CommandTimeoutSeconds`.
- **TPT/TPC** inheritance isn't checked correctly.
- **No case-insensitive name matching**: a table or column that differs only by case is reported
  as missing.
- EF 8+ complex types and primitive collections are only partially covered; JSON-mapped columns
  assume default column names.
- Only the in-process `CreateContext` path is supported; `ApplicationFactory` connections are
  rejected as for the other migration tools.

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

## Diagnosing schema drift

When a query fails with `Invalid column name`, `Invalid object name`, `no such column`,
`no such table`, or `column ... does not exist`, the code was usually deployed ahead of the
schema. The `list_migrations` and `compare_schema` tool descriptions carry this hint, and
`run_query`/`run_sql_query` append a static pointer to those tools when the error matches one of
those patterns (no extra provider text is echoed). The workflow:

1. `list_migrations` on the failing connection: `pendingMigrations`, `lastAppliedMigration`,
   `unknownAppliedMigrations`, `hasPendingModelChanges`.
2. `compare_schema` on the same connection, optionally with `tables`: `notInDatabase` entries with
   `introducedBy`/`introducedByIsPending`.
3. `describe_migration` with the `introducedBy` ID to see the operations the database is missing.
4. `generate_migration_script` against a **development** connection with
   `fromMigration` set to the failing database's `lastAppliedMigration`, and hand the preview to
   whoever deploys schema changes.

No tool applies migrations or executes DDL.

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
on all four migration tools resolves this by configuring EF Core's `.MigrationsAssembly(Assembly)` (the
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
  dependency) surface as `McpException`s, matching every other assembly-loading failure path.
  As with `load_assembly`, the message may echo back the offending path itself (so the caller
  can see what was rejected and why), but never connection strings, parameter values, or other
  provider-internal details — see [Connection management](./connections.md) for what "redacted"
  means in this codebase.

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
- `describe_migration` never opens the database; `compare_schema` reads only catalog metadata
  (through EF Core's scaffolding reader) and `__EFMigrationsHistory`.
- `generate_migration_script` never calls `IMigrator.Migrate()` or any
  `Database.ExecuteSqlRaw`/`BeginTransaction`. It produces a SQL string only.
- Every tool builds its context through `CreateContext(...)` and disposes it deterministically, so
  no connection is held between calls (`compare_schema`'s worker task disposes its own context).

## Configuration surface

Mirroring `RawSqlExecutionOptions`, `MigrationsOptions` binds the `Migrations` configuration
section:

- `Migrations:Enabled` (boolean, default `false`) — must be explicitly enabled to use
  `generate_migration_script`. `list_migrations`, `describe_migration`, and `compare_schema` are always available (read-only, no DDL).
- `Migrations:MaxScriptLength` (int, default 100 000) — character cap for generated SQL.
- `Migrations:CancellationMargin` (TimeSpan, default 5 s) — extra margin over the
  connection's command timeout when scripting or comparing schemas.
- `Migrations:MaxDriftEntries` (int, default 500) — total entries `compare_schema` returns.

## Open questions

- Whether to expose `fromMigration`/`toMigration` as opaque strings (migration IDs) or as
  structured `migrationId: string` objects. Current lean: opaque strings, matched via EF
  Core's own lookup, with the tool rejecting unknown IDs.
