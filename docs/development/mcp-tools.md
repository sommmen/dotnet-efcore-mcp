# MCP tool surface

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: [`src/DotnetEfCoreMcp.Server/Tools/EfCoreMcpTools.cs`](../../src/DotnetEfCoreMcp.Server/Tools/EfCoreMcpTools.cs)

See the [README "MCP tool contract"](../../README.md#mcp-tool-contract) section for the
per-tool parameter/response reference; this page tracks the surface's design decisions.

- [x] `list_contexts` — list discovered `DbContext` types available from the currently loaded assembly
- [x] `get_schema` — return the model/schema for a given context
- [x] `run_query` — execute a read-only LINQPad-style query expression against a selected context
- [x] `test_connection` — run a bounded, redacted connection-health diagnostic for a given context/connection
- [x] `insert_entity` — insert a metadata-validated entity through a non-production `ReadWrite` connection
- [x] `update_entity` — update a metadata-validated entity through a non-production `ReadWrite` connection
- [x] `delete_entity` — delete a metadata-validated entity through a non-production `ReadWrite` connection
- [x] `get_entity_schema` — return the complete cached definition for one exact entity
- [x] `search_schema` — search the cached schema for compact entity/property/relationship matches

## P0 #1 — LINQPad-style `run_query`

`run_query` accepts a required `query: string` containing Roslyn-compiled LINQPad-style C# rooted
at a `DbSet<>` property on the selected `DbContext`. For example:

```csharp
ShopProducts
    .Where(c => c.Domain.ShortName == "nl")
    .Where(c => c.Slug == "polijstdoek-deluxe")
    .Select(c => new { c.Id, c.Slug })
```

The first identifier is case-sensitive and must resolve to an actual public `DbSet<T>` property
whose `T` is in the EF model. This is the only `run_query` request shape: the former structured
`entity`/`where`/`parameters`/`orderBy`/`skip`/`take`/`include` parameters are not supported.
Projection, grouping, joining, paging, and aggregate semantics belong to this one query surface.

Authoring has two modes. If the trimmed text parses as one complete expression, the server emits
`return <query>;`. If it ends with `;` or uses a top-level block, the server treats it as
statement mode and expects the query text itself to `return` the final value. Because the query is
compiled as real C#, the supported operator surface is the full LINQ surface available to the
loaded app and referenced assemblies, including `Join`, `GroupJoin`, `SelectMany`, cross-`DbSet`
queries, and local variables in statement-mode queries. Client-side operators remain possible after
`AsEnumerable()` or materialization, but non-`IQueryable` results are returned through the scalar
slot instead of row-shaped output.

**Note:** Full statement-mode support (including access-policy pre-checks) is currently incomplete and tracked in P0 #9.
Expression-mode queries are the primary execution path; statement-mode is planned but requires further implementation work.

Access policy is enforced by `RunQueryCore` pre-check before Roslyn execution. The Roslyn pipeline then applies
cancellation/timeout, take caps, and safe result projection. `IQueryable` results receive the configured 
default row-count capping (200 by default) when no `Take` is present, and any supplied `Take` is clamped 
to the configured maximum. Scalar results remain scalars. The generated `UserQuery` type defaults to
`QueryTrackingBehavior.NoTracking`; an explicit `.AsTracking()` can opt back into tracking, but
`SaveChanges()` remains blocked unless `QueryExecution:AllowMutationsInRunQuery=true` and the
selected connection is non-production `ReadWrite`. Focused executor tests cover expression mode,
statement mode, joins, projections, aggregates, mutation gating, complexity limits, and paging
behavior. See [Query execution](./query-execution.md) for the full operator/behavior reference.

Execution location is configured with `QueryExecution:Mode` (`InProcess`, `OutOfProcess`,
`Pooled`, or `Auto`), and Roslyn compilation settings live under `QueryCompilation`; see
[Query execution](./query-execution.md) and
[Roslyn-compiled `UserQuery`](./roslyn-user-query.md).

## Proposed open work — P0 #2: `run_query` continuation indicator

Add `hasMoreRows: boolean` to successful **sequence-returning** `run_query` responses. It reports
whether another row matches after the effective `skip` and effective `take` window; it does not add a
row to `rows`, change `rowCount`, or expose a total count. The MCP response contract should explicitly
say that `take: 0` returns no rows and `hasMoreRows: false`, rather than probing or materializing a row.
Terminal scalar aggregates have no page window and return no `hasMoreRows` signal.

Implement this in `QueryExecutor` by retrieving no more than one sentinel row beyond the effective
take for positive takes, using the existing filtered, ordered, skipped query. Remove the sentinel
before projection and construct the result with the new flag; retain the current clamping and
read-only behavior. Add tool binding/serialization coverage plus executor tests for an empty result,
an exact effective take, an extra row, a clamped take, nonzero skip, and `take: 0`.
- [x] `run_sql_query` — execute explicitly enabled raw SQL only against development `ReadWrite` connections
- [x] `load_assembly` (or startup-only configuration) — point the server at a project's build output
  - Implemented as both: an optional `TargetAssemblyPath` startup config value AND a
    `load_assembly` MCP tool.
- [x] Decide whether assembly/connection configuration is done via server startup config only, or also exposed as MCP tools (weigh flexibility vs. attack surface)
  - Decision: expose `load_assembly` as a tool (in addition to startup config), and always
    require a `connectionName` (never a raw connection string) for `get_schema`/`run_query`.
    Rationale: `load_assembly` only grants filesystem read access already available to the
    server process itself (same trust boundary), so exposing it as a tool doesn't add a new
    privilege — but it *is* a code-execution primitive (any DLL on disk can be loaded and its
    types reflected over), so an optional `AssemblyLoader:AllowedRoots` allowlist
    (`AssemblyLoaderOptions`) restricts `load_assembly` to a configured set of root
    directories when set; empty (the default) remains unrestricted for trusted,
    single-user local dev setups.
- [x] Format successful tool payloads for agent-efficient consumption without changing MCP transport framing
  - Successful tool text content defaults to [TOON](https://github.com/Cysharp/ToonEncoder),
    while the stdio stream remains JSON-RPC and errors retain MCP-native behavior.
  - `IToolResultFormatter` keeps the tool surface independent of the encoding library.
    `ToonToolResultFormatter` is the default implementation and `JsonToolResultFormatter`
    preserves the former indented JSON payload for `ToolOutput:Format=json` compatibility.

## Proposed open work — P0 #4: `preview_query_sql`

Add a read-only `preview_query_sql` MCP tool with the same request inputs as `run_query`:
`contextName`, `query`, optional `connectionName`, and optional `targetName`. It returns a single
`sql` string containing the SQL generated by the configured EF Core provider for queries whose
final result is still an `IQueryable`. It is a preview, not a query result: do not return rows,
row counts, or connection details.

Factor the shared root resolution, authorization, Roslyn compilation, and `IQueryable`
construction into one path used by both `run_query` and `preview_query_sql`. The preview obtains
SQL exclusively with EF Core `ToQueryString()` from that built `IQueryable`; it must never
enumerate the query, open a connection, create or execute a database command, call `SaveChanges`,
or otherwise mutate state. Statement-mode queries that return scalars, lists, or client-side
`IEnumerable` values should be rejected for preview.

Before formatting a successful response or reporting an error, sanitize the preview: never expose
connection strings, parameter values, provider exception details, or stack traces. The tool description
must make this non-execution guarantee explicit. Focused tool tests should cover request binding,
forwarding to the shared builder, successful SQL serialization, sanitized validation/provider failures,
and prove that a preview does not execute a command or touch a connection.

## P0 #5 — `test_connection` diagnostics

`test_connection` takes a required `contextName: string` and optional `connectionName: string`. The
optional name follows the existing active-connection behavior; resolution goes through the same
`ResolveConnection` fail-closed path as `get_schema` and `run_query` (explicit name, active-connection
fallback, or an `McpException` for an unknown or inactive connection - no code path accepts a raw
connection string). It returns a compact, redacted result with the context name, logical connection
name, provider, environment classification, and a stable status: `healthy`, `failed`, or `timedOut`.
It never returns query rows, schema, raw configuration, or connection strings.

It is a non-mutating connectivity diagnostic: it constructs the requested `DbContext`, forwards the
resolved entry's `CommandTimeoutSeconds` and the MCP `CancellationToken` to
`ConnectionHealthChecker.CheckAsync`, and disposes the context immediately after the probe completes.
It never runs caller SQL, calls `SaveChanges`, or changes the active connection. Caller cancellation
propagates as a thrown `OperationCanceledException` rather than a status value; an internal timeout
(the command timeout plus `QueryExecutionOptions.CancellationMargin`) instead yields the `timedOut`
status; any provider failure yields `failed`. Only safe identifiers (context name, connection name,
provider, environment, status, elapsed time) are logged - provider exception text, inner exceptions,
stack traces, credentials, hosts, database names, and connection strings are never logged or returned.
See [`ConnectionHealthChecker`](../../src/DotnetEfCoreMcp.Server/Connections/ConnectionHealthChecker.cs)
for the bounded probe/timeout/cancellation classification logic.

Focused tool-surface tests
([`EfCoreMcpToolsTestConnectionTests`](../../tests/DotnetEfCoreMcp.Server.Tests/Tools/EfCoreMcpToolsTestConnectionTests.cs))
cover required/optional parameter binding and active-connection fallback, the unknown- and
no-active-connection error paths, the redacted success and failure payload shapes, and cancellation
propagation. Connection-layer tests
([`ConnectionHealthCheckerTests`](../../tests/DotnetEfCoreMcp.Server.Tests/Connections/ConnectionHealthCheckerTests.cs))
cover the `healthy`/`failed`/`timedOut` classifications and cancellation in isolation, without a real
slow provider, via an internal probe-delegate overload. See
[Connection management](./connections.md) for the registry-level design.

## Proposed open work — P0 #6: schema slicing/search

Add `get_entity_schema(contextName: string, entityName: string)` and
`search_schema(contextName: string, query: string, maxResults?: number)`. Both are read-only and
cache-only: after resolving `contextName`, they operate solely on the schema already held by
`SchemaCache`; they must not create a context, connect to a provider, or rediscover EF metadata.

`get_entity_schema` returns the complete cached entity definition for an exact entity name.
`search_schema` returns compact entity/property/relationship match summaries rather than full
entities. Search is case-insensitive and deterministic; `query` must be non-empty. Its result count
defaults to 10 and cannot exceed 25, with invalid counts rejected. Return `truncated` whenever more
matches exist than the effective limit.

Pass the cached schema through a policy-ready selector before slicing or searching. P0 #6 does not
implement authorization, but this seam must permit a future access-policy evaluator to filter the
visible entities, properties, and relationships without changing the two public contracts. Add
focused MCP binding/forwarding tests plus schema tests for cache-only behavior, slice fidelity,
unknown names, matching/order, caps and `truncated`, invalid input, and policy-seam forwarding.

## Proposed open work — P0 #7: query complexity limits beyond row count

`run_query` and `preview_query_sql` add no caller-controlled limits for this item. The relevant
server-side cap currently in place is the `QueryExecution` setting `MaxQueryLength` (described in
[Query execution](./query-execution.md)). Additional constraints (`MaxExpressionNodes`, `MaxExpressionDepth`, 
`MaxQueryOperators`, `MaxIncludedCollectionItems`) are proposed open work (P0 #7).
All requests share the same validated pipeline, so an oversized query is rejected
before provider translation or a database round-trip.

Tool descriptions should note that `run_query` and `preview_query_sql` are subject to
server-configured limits, without hard-coding numeric limits. Failures surface through the existing 
`QueryExecutionException`-to-MCP-error mapping and name only the exceeded limit and its configured maximum — never caller query text.

Focused tool-surface tests should verify rejection at every applicable cap, that errors do not echo
caller input, and that requests at or under every cap bind and forward as intended.

## Proposed open work — P0 #8: database-side `run_query` collection-include cap

This item adds no `run_query` parameter. `MaxIncludedCollectionItems` remains server-side
`QueryExecution` configuration, but its contract is strengthened: every requested included
**collection** returns at most that many children *per root parent*, and the server must apply that
limit in database execution before materialization. The current projection-time cap is not sufficient,
because ordinary `Include` has already loaded the full collection by then.

Implement the executor with ordered, provider-translated filtered collection includes (`Take` per
parent), or an equivalently bounded split-query strategy whose child SQL uses per-parent limiting.
The stable child order (normally primary key) is part of the observable result contract. Never replace
this with client-side collection truncation. Reference includes, one-level include validation, root
paging, existing errors, and safe scalar-only nested projection stay unchanged. With a configured cap
of zero, a requested collection is represented as empty without fetching its child rows.

Focused MCP/integration tests should bind and forward `include` unchanged, then verify a root with
more children than the configured cap returns only the capped, deterministically ordered set. Verify
multiple roots each receive their own cap, plus below/exact/zero-cap boundaries, reference includes,
and root `skip`/`take`. Capture SQLite commands or translated SQL to prove the child query has
server-side limiting/window logic before materialization; a response-only assertion is insufficient
because it could pass after loading every child row.

## Proposed open work — P0 #9: per-connection policy enforcement

Keep the public tool parameters unchanged: every existing `connectionName`, `contextName`, and `entity` is evaluated against the selected connection's server-side `AccessPolicy`; clients cannot supply or override policy data. Enforcement covers `list_contexts`, `get_schema`, `get_entity_schema`, `search_schema`, `run_query`, and `preview_query_sql`. Tools that do not select a context or entity remain outside this policy's entity decision.

Each tool uses the same policy evaluator and sanitized authorization failure. Discovery tools return filtered views rather than denied entries, while direct lookup/execution rejects denied or unlisted selectors. Add focused tool-surface tests for forwarding the connection identity to the evaluator, coverage of every listed tool, allowlist-over-deny precedence, and non-disclosure of excluded contexts/entities in list, schema, and search responses.

## P1 #11 — migration inspection & script generation

Two structured, server-side tools for EF Core migration state and scripting, reusing the
existing `ConnectionRegistry`/`DbContext` factory path (no raw connection strings from clients):

- `list_migrations` — non-mutating read of `appliedMigrations`, `pendingMigrations`,
  `databaseExists`, and `appliedStateAvailable`, sourced from the migration assembly's known
  migration IDs and, when a live connection exists, `IHistoryRepository`'s read of
  `__EFMigrationsHistory`. Always available (no enable flag) on non-production connections,
  including `ReadOnly`; rejected only on production connections.
- `generate_migration_script` — produces SQL via `IMigrator.GenerateScript(...)` as a preview
  only (never executes, opens no transaction, no `SaveChanges`). Gated by a `Migrations:Enabled`
  configuration flag (default `false`, restart-required to toggle — same convention as
  `RawSqlExecution:Enabled`) and rejected for production and `ReadOnly` connections. The
  response is capped by a configurable `Migrations:MaxScriptLength` (default 100,000 characters)
  and truncated at a best-effort statement boundary; the response carries `truncated: true` when
  cut short.

`run_sql_query` remains a separate, independent opt-in escape hatch; see
[Migration inspection](./migrations.md) for the full contract, EF Core integration points
(`IMigrator`, `IMigrationsAssembly`, `IHistoryRepository`), read-only safety rules, and
configuration surface. Tests under
[`tests/DotnetEfCoreMcp.Server.Tests/Migrations/`](../../tests/DotnetEfCoreMcp.Server.Tests/Migrations/)
cover parameter binding/response shape, the `Migrations:Enabled` gate, production/`ReadOnly`
connection rejection, script truncation, redacted failure messages (SQLite's lack of idempotent
script support, unknown migration IDs), and proof that both tools never mutate the target
database (no `Migrate()`, `ExecuteSqlRaw`, transaction, or `SaveChanges` call, and no database
file is created as a side effect of previewing a script).

## P1 #12 — structured mutation tools

The three structured, single-entity write tools — `insert_entity`, `update_entity`, and
`delete_entity` — are gated by `EntityMutations:Enabled` (default `false`) and restricted to
non-production `ReadWrite` connections resolved only through the existing
`ConnectionRegistry`/`DbContext` factory path. No tool accepts raw SQL, expressions, navigation
graphs, or connection strings.

Each tool binds structured parameters (`entity`, `values` and/or `key`, optional `concurrency`,
optional `connectionName`) and forwards them to a mutation executor that resolves the target
`IEntityType` from EF metadata before constructing, attaching, or saving anything. The executor
rejects unknown entities/properties, navigation/shadow properties, and any client-supplied value
for a computed, store-generated, or read-only property, then performs exactly one `SaveChanges`
call per request. Entities with EF concurrency tokens require original token values in
`concurrency`; a stale token, `DbUpdateConcurrencyException`, or zero-row result maps to a stable,
non-implementation-leaking conflict/not-found response rather than a success. See
[Structured mutations](./mutations.md) for the full contract, validation rules, concurrency and
affected-row result shape, and configuration surface.

Focused tool-surface tests cover binding/forwarding for all mutation tools, disabled/production/
`ReadOnly` rejection, and redacted failure classification; executor tests cover entity/property/key
validation and rejection of computed/store-generated/read-only/navigation/shadow properties;
mutation tests cover insert/update/delete success shape and accurate `affectedRows`; and
concurrency tests cover missing/stale token handling and conflict responses.

## Proposed open work — P1 #13: bounded nested `run_query` includes

Keep `include?: string[]` for legacy entity-shaped requests, with each entry a case-sensitive,
dot-separated EF navigation path (for example, `Orders.OrderLines`). Arbitrary-LINQ mode rejects
`include` whenever the chain projects, groups, aggregates, or otherwise changes the root entity shape;
this avoids silently ignoring includes or attempting to attach navigation data to non-entity rows. The
tool passes valid legacy entries without interpreting CLR members to `QueryRequest`; `QueryExecutor`
owns EF model validation, bounded query construction, and recursive response shaping. Existing
one-segment include entries remain valid.

Expose `QueryExecution:MaxIncludeDepth` and `QueryExecution:MaxIncludeCount` alongside the existing
per-collection `MaxIncludedCollectionItems` setting. The request fails, rather than truncating or
partially executing, when a path is malformed, exceeds the depth, makes the total path count exceed
the count limit, contains an unknown/scalar segment, repeats a path/navigation, or introduces a
cycle. Successful responses recursively contain only scalar values and requested navigation branches;
every collection branch at every depth is independently limited by
`MaxIncludedCollectionItems` per parent.

Add focused MCP-tool tests for nested `include` parameter binding and for error propagation from
model-path validation and both new limits. Pair them with the executor and SQLite integration cases
in [Query execution](./query-execution.md#proposed-open-work--p1-13-bounded-nested-include-paths),
including recursive projection and collection caps at every level.

## Proposed open work — P1 #14: `run_query` keyset/cursor pagination

Add an optional `pagination: { mode: "cursor", cursor?: string }` request object and an optional
`nextCursor: string | null` output to sequence-returning `run_query` results. Cursor mode is selected
explicitly; an omitted `cursor` requests its first page, whereas omitting `pagination` preserves the
current `skip`/`take` offset-paging contract unchanged. The two styles are mutually exclusive per
request — binding rejects cursor mode combined with `skip`. Terminal scalar aggregates reject
`pagination` and return no cursor or `hasMoreRows`. `nextCursor` is populated only when `hasMoreRows`
(P0 #2) is `true` for a sequence response and is otherwise `null`; a caller pages forward by
re-issuing the same request with `pagination.cursor` set to the previous response's `nextCursor`.

The tool passes the cursor-mode request through unchanged to `QueryExecutor`, which owns unique-key
tie-breaking, opaque encoding/decoding, and validation as described in
[Query execution](./query-execution.md#proposed-open-work--p1-14-keysetcursor-pagination). The tool
description should state plainly that v1 cursors are forward-only (no prior-page navigation) and that
a cursor is bound to the exact entity, context, and effective ordering that produced it; a mismatched
or tampered cursor is rejected with the existing sanitized error rather than any decoded key values.

Focused tool-surface tests cover `pagination`/`cursor` parameter binding, `nextCursor` response
serialization, rejection of combining cursor mode with `skip`, and forwarding of the sanitized
rejection for a malformed/mismatched cursor without leaking decoded values. Pair them with the
executor coverage in [Query execution](./query-execution.md#proposed-open-work--p1-14-keysetcursor-pagination).

## Proposed open work — P2 #15: named multi-target assembly registry

Extend `load_assembly` with an optional `targetName` parameter so a client can register (or
reload) a compiled assembly under a stable logical name instead of always replacing the
server's single implicit target. Add `list_loaded_assemblies` (returns every registered
target's name, source path, load timestamp, and current-default flag) and `select_target`
(changes only which registered name resolves when a call omits `targetName`, without
unloading any other target). Add the same optional `targetName` parameter, resolved the same
way as `connectionName`, to every tool that resolves an assembly-derived `DbContext` type:
`list_contexts`, `get_schema`, `run_query`, and `run_sql_query`.

Omitting `targetName` anywhere must behave exactly as today: a single implicit target is
loaded/replaced and used, with no new required parameters and no response-shape changes for
existing callers. See [Multi-target assembly registry](./assembly-registry.md) for the full
configuration surface, isolation/caching/lifecycle model (each named target keeps its own
collectible `AssemblyLoadContext` and reload watcher; `SchemaCache`'s `Type`-keyed cache
already isolates correctly across targets), and how this composes with the per-connection
`AccessPolicy` from P0 #9 (unaffected — policy stays scoped to the selected connection, not
the selected target).

Focused tool-surface tests cover registering/resolving multiple named targets, ALC/type
isolation between two targets, name-collision reload-in-place versus duplicate entries,
unknown-`targetName` rejection without leaking other registered names, the omitted-`targetName`
backward-compatibility path across all four downstream tools, and unaffected `AccessPolicy`
enforcement when a resolved context/entity comes from a non-default target.
