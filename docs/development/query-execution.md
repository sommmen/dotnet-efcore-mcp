# Query execution

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: [`src/DotnetEfCoreMcp.Server/Querying`](../../src/DotnetEfCoreMcp.Server/Querying) ·
Tests: [`tests/DotnetEfCoreMcp.Server.Tests/Querying`](../../tests/DotnetEfCoreMcp.Server.Tests/Querying)

See the [README "MCP tool contract"](../../README.md#mcp-tool-contract) for the public
`run_query` request and response shapes.

## Roslyn execution location

`run_query` uses the Roslyn/LINQPad-style `UserQuery` pipeline. Execution location is configured
with `QueryExecution:Mode`:

| Setting | Values | Default | Effect |
| --- | --- | --- | --- |
| `QueryExecution:Mode` | `InProcess`, `OutOfProcess`, `Pooled`, `Auto` | `Auto` | Selects where Roslyn queries execute. |
| `QueryExecution:OutOfProcessHostPath` | Absolute path to the query-host DLL | — | Required when the selected mode uses isolated execution. |
| `QueryExecution:PoolMaxWorkersPerTarget` | positive integer | `2` | Maximum warm persistent workers kept for one target assembly path + last-write-time build key. |
| `QueryExecution:PoolMaxTotalWorkers` | positive integer | `8` | Maximum pooled persistent workers across all target keys in one MCP server instance. |
| `QueryExecution:PoolMaxQueriesPerWorker` | positive integer | `50` | Recycles a pooled worker after this many successfully completed queries. |
| `QueryExecution:PoolIdleTimeoutSeconds` | positive integer | `300` | Recycles an idle pooled worker after this many idle seconds; each worker also self-terminates after 2× this window as defense in depth. |
| `QueryExecution:AllowMutationsInRunQuery` | `true` / `false` | `false` | Allows `SaveChanges()` from `run_query` only when the selected connection is also non-production `ReadWrite`. |

Related compilation settings live under `QueryCompilation`:

| Setting | Values | Default | Effect |
| --- | --- | --- | --- |
| `QueryCompilation:CompileTimeoutSeconds` | positive integer | `10` | Wall-clock budget for parsing, binding, and emitting one Roslyn query. |
| `QueryCompilation:AdditionalReferenceAssemblyNames` | string array | empty | Extra already-loaded assembly simple names added as metadata references for every compiled query. Server-side only. |

`InProcess` uses the server's existing Roslyn executor. `OutOfProcess` launches a short-lived
query-host process for each query. `Pooled` uses a bounded pool of long-lived out-of-process query
hosts keyed by target assembly full path + last-write-time UTC so warm workers never cross builds
or worktrees, but still retain out-of-process isolation from the MCP server itself. `Auto`
currently chooses `OutOfProcess` as a fail-closed default; compatibility fingerprinting for
choosing an in-process execution path has not yet been implemented, and `Auto` does not yet opt
into pooling.

For isolated execution, configure the query-host DLL and retain both deployment artifacts:

- the target application's adjacent `<target>.runtimeconfig.json`, which selects the target
  runtime/framework;
- the query host's adjacent `<query-host>.deps.json`, which resolves the query host and server
  dependency closure.

The host receives a versioned JSON request through standard input and returns one JSON response on
standard output. Connection strings travel only in that request, not on the process command line.
In `Pooled` mode, a persistent host stays alive for newline-delimited request/response exchanges
until the pool retires it or it self-terminates after extended idleness; `OutOfProcess` continues
to use the original one-request/one-process path unchanged.

For design background, see [Query execution alternatives](query-execution-alternatives.md). For
measured latency characteristics of out-of-process execution and a pooling design to reduce
per-query cost, see [Out-of-process query host latency: findings and a pooling
design](query-execution-host-pooling.md).

> [!IMPORTANT]
> `Pooled` trades some "fresh process per query" simplicity for much lower steady-state latency.
> Safety remains bounded by per-query timeout + kill-and-drop, per-worker recycling, and per-build
> keying, but warm workers can still retain process-level JIT/compiler caches until recycled. Keep
> `OutOfProcess` if you require the strongest isolation boundary.
>
> Pool bounds are per MCP server instance, not system-wide. Each concurrently running server or git
> worktree keeps its own independent pool, so the total system-wide worker count is
> `PoolMaxTotalWorkers × instance count`. Operators running several instances should size
> `PoolMaxTotalWorkers` down accordingly.

## Consumer-visible failures

`run_query` preserves its concise, entity-scoped failure message and returns a recognized safe
underlying cause without stack traces. It includes a recovery-oriented next step: queries using
row-limiting operators without deterministic ordering are directed to add ordering, and runtime
failures caused by invariant globalization mode direct operators to enable ICU/globalization support.
Other provider failures remain sanitized and direct callers to validate model names with `get_schema`,
validate query syntax, and consult server logs if required.

## LINQPad-style `run_query`

`run_query` accepts one required `query` string. Its first identifier must exactly match a public `DbSet<T>` property on the selected `DbContext`; `T` must be a mapped entity. For example:

```csharp
ShopProducts.Where(c => c.Domain.ShortName == "nl")
    .Select(c => new { c.Id, c.Slug })
```

This is the only public `run_query` request shape: the older structured
`entity`/`where`/`parameters`/`orderBy`/`skip`/`take`/`include` form is gone. Projection,
grouping, joining, paging, and aggregate semantics all live in caller-authored C#.

Authoring has two modes:

- **Expression mode:** if the trimmed text parses as one complete C# expression, the server emits
  `return <query>;`.
- **Statement mode:** if the trimmed text ends with `;` or uses a top-level `{ ... }` block, the
  server treats it as a statement body. Use this for local variables, multiple steps, and explicit
  `return` statements.

Because the query is compiled as real C#, the supported operator surface is the full LINQ surface
available to the loaded app and referenced assemblies. Common provider-translatable shapes include
`Where`, `Select`, `GroupBy`, ordering, `Skip`, `Take`, `Distinct`, aggregates, element operators,
set operators, `Join`, `GroupJoin`, and `SelectMany`; other public `DbSet<T>` properties on the
same context can be referenced by name inside the query. Client-side operators can also be used
deliberately after `AsEnumerable()` or materialization, but once the result is no longer an
`IQueryable` it is returned through `QueryResult.Scalar` instead of row-shaped output.

The server still enforces safety boundaries: only configured metadata references are available at
compile time; `unsafe` code is disabled; query length, expression-tree node count, tree depth, and
operator count are capped; and compile/runtime failures are sanitized without logging raw query
text or sensitive provider data.

No terminal call is required for `IQueryable` results — `run_query` materializes them server-side
and applies deterministic key ordering plus an automatic take cap, so fragments like
`Orders.Where(o => o.Number == "123NL")` work without an explicit
`.ToList()`/`.ToListAsync()`/`.FirstOrDefault()`. Adding a terminal element operator or explicit
`Take(n)` still narrows the result. By contrast, already-materialized results or client-side
`IEnumerable` pipelines are returned as scalars, not paged row sets.

Execution defaults to `QueryTrackingBehavior.NoTracking`, subject to the selected connection's
command timeout and server cancellation. An explicit `.AsTracking()` can opt back into tracking.
`SaveChanges()`/`SaveChangesAsync()` remain blocked unless
`QueryExecution:AllowMutationsInRunQuery=true` **and** the resolved connection is non-production
`ReadWrite`. Root primary-key ordering supplies deterministic ordering for un-ordered root
`IQueryable` sequences. Sequence results receive the configured default page when no `Take` is
supplied; any caller `Take` is clamped to `MaxTake`. Terminal scalar aggregates are not paginated.

## P0 #2 — `run_query` continuation indicator

Sequence `QueryResult` values (and their `run_query` responses) carry `hasMoreRows`. For a
positive effective take, it is `true` only if at least one row remains after applying the final sequence
ordering and effective `skip`/`take` values. It is not a total-count indicator: `rows` and `rowCount`
continue to contain at most the effective take rows, while the flag supports a subsequent query with an
advanced `skip`. When no rows remain—or when the result has exactly the effective take rows—the flag
is `false`. Terminal scalar aggregates have no page window; their `hasMoreRows` is always `false`.

After filtering, ordering, and skipping, the executor requests `effectiveTake + 1` rows for a
positive effective take (`QueryExecutor.MaterializeWithContinuationAsync`). The final row, if
present, is treated solely as a sentinel: it
sets `hasMoreRows` and is discarded before projection, so only the requested window is returned. The
sentinel probe is never executed for `effectiveTake == 0`: `take: 0` returns `rows: []`, `rowCount: 0`,
and `hasMoreRows: false` without materializing or querying the database, even when matching rows exist.
Take clamping, cancellation/timeout behavior, and bounded projection are unaffected. Focused executor
tests cover empty results, exact-take results, an extra row (over-limit), clamped takes, skipped
windows, and `take: 0`.
- [x] Translate/execute the incoming query against the real `DbSet<T>` for the requested entity
- [x] Enforce read-only execution by default (no `SaveChanges`, no tracked entities, `.AsNoTracking()`)
- [x] Enforce a maximum result size / row limit and require pagination for larger result sets
  - `QueryExecutionOptions.MaxTake` (default 200) is enforced via `Math.Clamp` regardless
    of what the caller requests or omits; `skip`/`take` are always honored for paging.
- [x] Enforce a query timeout (command timeout / cancellation token) to avoid runaway queries
- [x] Reject or restrict unsafe query shapes (e.g. arbitrary raw SQL, unbounded `.Include()` graphs) unless explicitly allowlisted
  - `run_sql_query` is disabled by default and must be explicitly enabled through
    `RawSqlExecution:Enabled`, then the MCP server must be restarted; it cannot be enabled by an
    MCP request or session. The disabled-tool response gives configuration examples for
    `appsettings.json`, `DOTNETEFCOREMCP_RawSqlExecution__Enabled=true`, and user-secrets, and
    directs callers to the always-on read-only `run_query` alternative where appropriate. It is
    independently rejected for production and non-`ReadWrite` connections, even when globally
    enabled. Raw SQL execution failures retain their sanitized provider message and add a
    `Cause` when available plus a next step to check `get_schema`, verify `@p0`-style placeholders,
    and consult server logs.
  - The Roslyn query surface is bounded by a curated metadata-reference list, disabled `unsafe`
    code, and the `MaxQueryLength` cap (enforced before provider work begins). Additional
    complexity limits (`MaxExpressionNodes`, `MaxExpressionDepth`, `MaxQueryOperators`) are
    proposed future work (see P0 #7 below).
  - `IQueryable` results are capped before materialization through `MaxTake`/`DefaultTake`;
    non-`IQueryable` results (including client-side `IEnumerable` pipelines) are returned via the
    scalar slot instead of row-shaped paging semantics.
- [x] Serialize query results (including related/included entities) into a response format that avoids circular references
  - Cycle-safety is structural (depth-bounded dictionary projection, not tracked EF Core
    entities), with `System.Text.Json`'s `ReferenceHandler.IgnoreCycles` as a
    defense-in-depth second layer in the MCP tool serialization step.
- [x] Surface EF Core / provider exceptions as clear, sanitized error messages (no leaking connection strings or stack traces with sensitive info)
  - `QueryExecutionException` messages never include connection strings (only entity/
    context/parameter-shape information); provider exceptions are wrapped, not passed
    through verbatim.

## Proposed open work — P0 #4: generated SQL preview

Add a read-only `preview_query_sql` tool with the same request inputs as `run_query`:
`contextName`, `query`, optional `connectionName`, and optional `targetName`. It should use the
same root resolution, access-policy checks, Roslyn compilation, and sequence-shaping front end as
`run_query`, but stop at provider-generated SQL instead of materializing rows.

Previewing has a strict non-execution boundary: it must not enumerate or materialize the query, open a
database connection, create or execute a command, invoke `SaveChanges`, or otherwise read or write
database state. It does not use the separately enabled `SqlQueryExecutor` raw-SQL path. SQL formatting
and parameter declarations may differ by provider, but they must represent the same validated LINQ
shape as `run_query`.

The preview path must obtain SQL only from an unexecuted `IQueryable` via `ToQueryString()`. That
means statement-mode queries are valid only when their final `return` value is still an
`IQueryable`; scalar results, client-side `IEnumerable` pipelines, and already-materialized lists
should be rejected for preview because they do not have a provider-translatable SQL
representation.

Apply the same root-name validation, allowed-reference surface, ordering/take behavior, and
complexity limits before SQL generation. Reject invalid, unsafe, or unsupported requests with the
existing sanitized `QueryExecutionException`-style errors; never expose connection strings,
credentials, provider internals, or stack traces in SQL or failure responses. Focused tests should
cover equivalent SQL for valid expression- and statement-mode `IQueryable` queries, shared
validation rejection paths, provider-specific SQL rendering, and proof that preview produces no
connection or command activity.

## Proposed open work — P0 #7: query complexity limits beyond row count

`MaxTake` and `DefaultTake` already bound result *size*.
`MaxQueryLength` is the only shape constraint currently enforced on Roslyn-authored `run_query` input
before provider work begins; it is server-side configuration under `QueryExecution`, not a per-request override.

Proposed future work: add `MaxIncludedCollectionItems`, `MaxExpressionNodes`, `MaxExpressionDepth`, 
and `MaxQueryOperators` constraints to enforce further complexity limits before Roslyn compilation
reaches provider translation or database access. Reject oversized or overly-complex queries before
execution or preview. Command timeout and capped paging will continue to bound valid but expensive provider
work.

Violations throw the existing `QueryExecutionException`, consistent with every other validation
failure. The message names only the exceeded limit and configured maximum (for example, "expression
length 812 exceeds the configured maximum of 500 characters") — never caller `where`, `orderBy`,
`include`, expression, or parameter text. Because `run_query` and `preview_query_sql` share the
pipeline, their error shape and enforcement are identical.

Focused validation: cover each cap at its exact boundary and one over, including multiple
violations. Prove that rejected input never reaches provider translation or the database with a
provider/connection spy. Add MCP contract tests for `run_query` and `preview_query_sql` proving
sanitized errors do not echo supplied query text.

## Proposed open work — P0 #8: database-side collection-include cap

`MaxIncludedCollectionItems` must bound each requested collection navigation **in the database**,
before EF materializes entities. The current `run_query` path invokes ordinary string-based `Include`,
materializes the root graph, and then passes the configured maximum to projection. That sequence can
trim the response shape, but it has already read an unbounded child collection and therefore does not
provide the required execution-time limit.

Replace the collection-include path with provider-translatable filtered include expressions: for each
validated direct collection navigation, apply a stable ordering (the child primary key unless an
explicit supported ordering is introduced) and `Take(MaxIncludedCollectionItems)` inside the include.
If expression construction makes that impractical, use an equivalent split-query plan that applies the
same per-parent limit in generated SQL (for example, a partitioned `ROW_NUMBER`/`Skip`/`Take` plan)
and avoids N+1 child queries. Reference navigations remain ordinary one-level includes. Do not use
post-materialization `Take`, since it still loads every child row. `0` fetches/materializes no members
for requested collection navigations while retaining their empty collection shape.

Preserve validation, `AsNoTracking`, command timeout/cancellation, root `skip`/`take`, safe scalar
projection, and the existing one-level include restriction. The cap is independent for every parent
and every requested collection navigation; its deterministic child ordering must make repeated calls
stable.

Focused validation: seed a parent above the cap and assert only the cap is returned; seed multiple
parents and prove each receives up to the cap rather than sharing a global limit; cover below-cap,
exact-cap, and zero-cap collections, omitted includes, and reference navigations. Combine root
`skip`/`take` with a capped collection include and, if multiple collection includes are supported,
cover each independently. In SQLite integration tests, capture executed SQL/commands (or inspect the
translated query) and assert the child query contains provider-translated limiting/window logic, so the
test demonstrates rows are limited before materialization rather than merely truncated in projection.

## Proposed open work — P0 #9: policy-gated context/entity execution

Before any query path constructs a context, compiles Roslyn code, generates SQL, or connects to the
database, authorize the requested `contextName` plus the root `DbSet`/entity and any additional
public `DbSet` roots referenced by the query against the selected connection's `AccessPolicy`. The
same guard is required for `run_query` and `preview_query_sql`; shared entry points must receive
the already-authorized identities so no alternate execution path can bypass them.

A denied or unlisted selector fails closed with a sanitized authorization error and performs no model/database work. Focused tests verify each execution tool rejects disallowed context and entity requests before parsing or SQL/database access, permits an explicit allow despite a matching deny, denies unmatched selectors, and preserves existing behavior for allowed requests.

## Proposed open work — P1 #14: keyset/cursor pagination

Add an opt-in keyset ("cursor") pagination mode for final **sequence** results alongside the existing
offset-based `skip`/`take` paging: a `pagination: { mode: "cursor", cursor?: string }` request object
selects keyset paging, with an omitted `cursor` requesting its first page; a `nextCursor: string | null`
response field is populated whenever `hasMoreRows` (P0 #2) is `true`. Terminal scalar aggregates reject
cursor pagination and expose neither `nextCursor` nor `hasMoreRows`. A request omitting `pagination`
behaves exactly as today — offset paging and keyset paging are mutually exclusive per request, not
merged, and cursor mode rejects `skip`.

Keyset correctness depends on a **unique, deterministic order**: when `cursor` is supplied, append the
root entity's primary key (in a stable direction) to the caller's `orderBy` terms if it is not already
present, so ties on the caller's ordering columns cannot reorder or duplicate rows across pages. Reject
a `cursor` request whose resolved ordering is not unique-terminated only if the primary key itself
cannot be appended (for example, a composite key EF cannot resolve); otherwise the append is automatic
and transparent to the caller.

The cursor is an opaque, server-encoded token (not a raw offset or exposed key value) containing the
last-returned row's order-key values and a hash/version tag binding it to the requesting entity,
context, and the effective ordering shape used to produce it. Validate every incoming cursor before
building the query: reject malformed encoding, a cursor produced for a different entity/context, and a
cursor whose bound ordering shape no longer matches the request's current `orderBy` — each with the
existing sanitized `QueryExecutionException` error, never echoing the decoded key values or raw token
back to the caller.

Scope v1 to **forward-only** pagination: `cursor` always resumes strictly after the referenced row in
the effective ascending/descending order already established by `orderBy`. Backward paging (returning
prior pages from a cursor) is explicitly deferred to a later item; document this bound in the tool
description so callers do not assume symmetric forward/backward navigation. Keyset paging must
coexist with, not replace, the current mechanisms: `take` continues to bound the page size the same
way for both paging styles, `hasMoreRows` keeps its existing sentinel-row semantics (P0 #2) and is
still how a caller learns whether to request `nextCursor`, and legacy offset (`skip`/`take`) pagination
remains fully supported and unaffected for requests that do not supply `cursor`.

Focused validation: add executor tests for cursor-absent parity with existing `skip`/`take` behavior,
first-page cursor issuance, resuming from an issued cursor with and without caller-supplied `orderBy`,
automatic unique-key tie-breaker append, rejection of a malformed/tampered/mismatched-entity/
mismatched-ordering cursor, rejection of combining `cursor` with nonzero `skip`, and `hasMoreRows`/
`nextCursor` agreement (a `null` `nextCursor` whenever `hasMoreRows` is `false`). Add MCP contract
tests for `cursor` request binding and `nextCursor` response serialization, and confirm the sanitized
rejection message never discloses decoded key values.

## Proposed open work — P1 #13: bounded nested include paths

Extend `QueryRequest.Include` from one-level navigation names to dot-separated EF model paths, such
as `Orders.OrderLines.Product`. Treat every segment as case-sensitive and resolve it exclusively
against the current `IEntityType`'s EF navigation metadata before building an `Include` expression or
opening a query. A segment that is unknown, scalar, or otherwise not a navigation fails the entire
request with a `QueryExecutionException`; never fall back to CLR reflection or silently omit it.

Add `QueryExecutionOptions.MaxIncludeDepth` and `MaxIncludeCount`. `MaxIncludeDepth` bounds the
number of navigation segments in each path, and `MaxIncludeCount` bounds the number of requested
paths after validation/deduplication. Reject an empty segment, an over-depth path, too many paths,
a repeated path, and a path that repeats a navigation already traversed in that path. Reject cycles
rather than relying on a depth limit to make a cyclic graph safe (for example,
`Orders.Customer.Orders` is invalid); repeated navigation traversal and cycles must be detected from
the resolved EF navigation chain, not merely by comparing textual input.

Replace the current one-level response shaping with recursive projection driven by those validated
model paths. Each projected object contains its mapped scalar values plus only the requested child
navigation branches; references recurse only along the requested path. At **every** included
collection navigation level, materialize no more than `MaxIncludedCollectionItems` children,
independently for each parent and level. This cap applies equally to a root collection, nested
collections, and multiple branches; it is not a global response budget and must not be deferred until
after an unbounded collection has materialized. Preserve existing read-only, no-tracking, root
paging, timeout, and root row-cap behavior.

Focused executor/integration tests should cover valid single and multi-segment paths; EF-model-only
validation; unknown properties, scalar segments, malformed paths, repeated paths, repeated
navigation traversal, and cycles; both include limits; recursive response shape; and collection caps
at the root and each nested level with multiple parents. Include below-cap, exact-cap, and
above-cap cases to prove each parent collection is capped independently before projection. Tool tests
should verify `include` binding and propagate the same validation failures without changing the
existing one-level include contract when only one segment is supplied.
