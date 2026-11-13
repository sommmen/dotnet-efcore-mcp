# Migration & schema-drift diagnostics (design record)

[← Back to Development Guide](../../DEVELOPMENT.md)

> **P1 #11b: migration and schema-drift diagnostics.** Status: **Implemented.** This page records
> why and how the feature was designed. The tool contracts live in
> [Migration inspection](./migrations.md), which is the canonical reference.

## Motivation

A production background job in a real consumer app failed repeatedly with:

```
Microsoft.Data.SqlClient.SqlException: Invalid column name 'SomeNewColumn'.
   at EntityFrameworkQueryableExtensions.ToDictionaryAsync(...)
   at SyncService.SyncRecordsAsync(...)
```

A recent migration adds the column, and that migration lives in a different assembly from the
`DbContext`. This is the classic "code deployed ahead of the schema" drift. To diagnose it, an agent has to answer these questions:

1. Which migrations has *this* database applied, and which ones are known to the deployed code
   but still pending?
2. Which model columns or tables are actually missing from (or different in) the database?
3. Which migration introduces each missing piece?

Before P1 #11b the server answered only part of question 1, and only for non-production
connections:

| Gap | Before |
|---|---|
| Production inspection | `list_migrations` threw for `IsProduction` connections, so you couldn't diagnose the database that actually failed. |
| Database ahead of code | History rows the assembly didn't know were returned as "applied", with no indication that they were unknown. |
| Model vs. snapshot | Nothing reported model changes with no migration (`dotnet ef migrations has-pending-model-changes`). |
| Model vs. live schema | No model-to-catalog comparison; the agent had to hand-write `INFORMATION_SCHEMA` SQL through `run_sql_query`, which is disabled by default and rejected for production. |
| Migration content | Seeing a migration's operations required generating and reading the full SQL script (`Migrations:Enabled`, `ReadWrite`, non-production). |
| Attribution | Nothing mapped a missing column or table back to the migration that introduces it. |

## Principles

- **Investigate, never apply.** Every diagnostic tool is read-only. Nothing calls `Migrate()`,
  executes migration SQL, or issues DDL/DML.
- **Safe reads are on by default (AXI).** Reading `__EFMigrationsHistory`, migration metadata, and
  catalog metadata can't change data, so these tools need no opt-in flag and work on production
  and `ReadOnly` connections. The gates that guard real risk stay: `generate_migration_script`
  (`Migrations:Enabled`, non-production, `ReadWrite`), `run_sql_query`, and mutations.
- **Mirror the `dotnet ef` vocabulary.** Results use the concepts of `dotnet ef migrations list`,
  `dotnet ef migrations has-pending-model-changes`, and the runtime APIs
  `Database.GetMigrations()`, `GetAppliedMigrationsAsync()`, `GetPendingMigrationsAsync()`, and
  `HasPendingModelChanges()`.

## What shipped

| Step | Result | Reference |
|---|---|---|
| 1. `list_migrations` on every connection | The production rejection was removed; `generate_migration_script` keeps its refusals. | [Security model](./migrations.md#security-model-must-match-existing-conventions) |
| 2. `list_migrations` parity with `dotnet ef` | `migrations[]`, `knownMigrationCount`, `lastAppliedMigration`, `unknownAppliedMigrations`, `pendingMigrations[].outOfOrder`, `hasPendingModelChanges`. | [`list_migrations`](./migrations.md#list_migrations) |
| 3. `describe_migration` | Structured `Up`/`Down` operations plus `MigrationOperationIndex` for attribution. | [`describe_migration`](./migrations.md#describe_migration) |
| 4. `compare_schema` | Model vs. live catalog through EfCore.SchemaCompare, with `introducedBy` attribution. | [`compare_schema`](./migrations.md#compare_schema) |
| 5. Docs and surfacing | Tool-description hints, a static hint on `run_query`/`run_sql_query` missing-column/table errors, and a README troubleshooting recipe. | [Diagnosing schema drift](./migrations.md#diagnosing-schema-drift) |

With these, that incident becomes `list_migrations` → `compare_schema` →
`describe_migration` on the production connection, followed by `generate_migration_script` on a
development connection from production's `lastAppliedMigration`.

## Investigation: EfCore.SchemaCompare

`compare_schema` embeds [EfCore.SchemaCompare](https://github.com/JonPSmith/EfCore.SchemaCompare)
instead of a hand-rolled comparer.

| Aspect | Finding |
|---|---|
| License and maintenance | MIT, by Jon P Smith. Actively maintained (last push Dec 2025, not archived). |
| Versions | The major version tracks EF Core: `10.0.0` targets `net10.0` / EF Core 10, matching the server. |
| Dependencies | `Microsoft.EntityFrameworkCore.Relational`, `Microsoft.Extensions.Caching.Memory`, `Microsoft.Extensions.Configuration.Json`, `Newtonsoft.Json`. Since v8 it also requires the consumer to reference `Microsoft.EntityFrameworkCore.Design`. |
| How it reads the database | Reflects over EF's internal `DesignTimeServicesBuilder` to build design-time services and resolve the provider's `IDatabaseModelFactory`, i.e. EF Core's own reverse-engineering reader, so every scaffoldable provider works. |
| What it checks | Model → database: tables/views, columns (type, nullability, computed, default SQL, value generation), primary keys, foreign keys, indexes. Database → model (optional): unused tables, columns, and indexes. |
| Output | A structured `CompareLog` tree (`Type`, `State`, `Name`, `Attribute`, `Expected`, `Found`) that maps cleanly onto a JSON result. |
| Filtering | No "only these tables" option, so it always scaffolds the whole catalog. |
| Known limitations | TPT/TPC aren't checked correctly, no case-insensitive name matching, partial coverage of complex types and primitive collections, JSON columns assume default names. |

Risks retired by a spike before committing to the tool:

1. **Design-time services in our load-context setup.** Design-time services resolve successfully
   for contexts loaded through the collectible `TargetAssemblyLoadContext` (`SampleApp` and split
   migrations); no ALC changes were needed. Runtime `Design` assets are explicitly included.
2. **Connection handling.** Only `CompareEfWithDb(DbContext)` is used, on a context from
   `CreateContext`, so connection strings never come from client input or `appsettings.json`.
3. **Cost on large catalogs.** The scaffold reader is synchronous and takes no
   `CancellationToken`, so it runs on a worker task bounded by `CommandTimeoutSeconds` plus
   `Migrations:CancellationMargin`. Measured manually against a real production SQL Server
   database (about 125 mapped tables, 250 history rows): `list_migrations` took about 5 s on a
   cold run, and `compare_schema` about 2–6 s, well within the default 30 s timeout. That run also
   surfaced the comparison noise that `compare_schema` now drops (see
   [`compare_schema`](./migrations.md#compare_schema)).
4. **Internal API coupling.** The SchemaCompare major version is pinned to the EF Core major and
   grouped with EF Core in `renovate.json`.

Spike outcome: SQLite's scaffolder can't recover constraint names, so empty constraint-name
differences are ignored for SQLite only. A fully migrated fixture compares clean, and an
`InitialCreate`-only database reports `Customers.Email` attributed to `AddCustomerEmail`.

## Non-goals

- Applying, reverting, or "fixing" migrations, and any DDL execution.
- Hand-written per-provider catalog queries as a fallback for `compare_schema`.
- Out-of-process / startup-derived connections (P2 #16). These tools use the in-process
  `CreateContext` path; supporting the query host is follow-up work.
- Automatic error correlation beyond a static hint. The server only appends a fixed pointer to
  `list_migrations`/`compare_schema` when a query error names a missing column or table; the agent
  does the diagnosis.

## Decisions

- **Production access:** no setting. Read-only migration and schema inspection is allowed on every
  connection, including production.
- **Large databases:** `compare_schema` doesn't require `tables`. Output is capped at
  `Migrations:MaxDriftEntries`, and the tool description recommends `tables` for large catalogs.
- **Applied state source:** `list_migrations` keeps reading history through `IHistoryRepository`,
  preserving the "history unreadable → `appliedStateAvailable: false`" degradation.
  `hasPendingModelChanges` degrades to `null` with a warning when it can't be computed.
- **If the spike had failed,** `compare_schema` would have been deferred rather than replaced with
  hand-written catalog queries.
