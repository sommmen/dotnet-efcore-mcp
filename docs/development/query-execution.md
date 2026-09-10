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

`QueryExecution:OutOfProcessHostPath` does not need to be set manually in the common case: if left
unset, the server auto-detects the query host via
[`QueryHostLocator`](../../src/DotnetEfCoreMcp.Server/Querying/QueryHostLocator.cs). The packaged
NuGet/`dotnet tool install` distribution bundles the query host's full publish output in a
`queryhost/` subfolder next to the server's own binaries (see the `PublishQueryHostForBundling` /
`IncludeQueryHostInPackage` MSBuild targets in `DotnetEfCoreMcp.Server.csproj`), so this is found
automatically once installed. When running from a local solution build instead (e.g. `dotnet
run`/tests, no packaging step has run), the locator falls back to the sibling
`DotnetEfCoreMcp.QueryHost` project's own build output. Set `OutOfProcessHostPath` explicitly to
override auto-detection, e.g. when pointing at a custom or externally deployed query host.

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
  `return <query>;`. This is the currently supported execution path in `run_query`. A single optional trailing `;` is stripped and accepted.
- **Statement mode:** if the trimmed text uses a top-level `{ ... }` block or contains multiple statements,
  the server would treat it as a statement body for local variables, multiple steps, and explicit
  `return` statements. This is **not supported** in `run_query` by design: entity-level access policy enforcement (described below) requires a pre-compilation, syntactic root/entity extraction before Roslyn compilation and execution, and the current pre-check only performs that extraction over a single expression. Statement mode remains intentionally unsupported because analyzing arbitrary statement bodies for the same guarantees is not tractable for that pre-check; this is a permanent design constraint, not a temporary gap.

Because the query is compiled as real C#, the supported operator surface is the full LINQ surface
available to the loaded app and referenced assemblies. Common provider-translatable shapes include
`Where`, `Select`, `GroupBy`, ordering, `Skip`, `Take`, `Distinct`, aggregates, element operators,
set operators, `Join`, `GroupJoin`, and `SelectMany`; other public `DbSet<T>` properties on the
same context can be referenced by name inside the query. Client-side operators can also be used
deliberately after `AsEnumerable()` or materialization, but once the result is no longer an
`IQueryable` it is returned through `QueryResult.Scalar` instead of row-shaped output.

`UserQuerySourceGenerator` emits `using System.Linq;` and `using Microsoft.EntityFrameworkCore;`
plus a `using` for the target `DbContext`'s own namespace, every `DbSet<T>` entity type's
namespace, and the namespace of any enum-typed property declared directly on those entities
(`CollectQueryNamespaces`). This is what lets `Orders` (an inherited `DbSet` member) and a sibling
type like an entity's own enum (e.g. `PartnerType.Transport`, if `PartnerType` lives in the same
namespace as the context or one of its entities) both resolve unqualified inside the query text —
without this, only inherited members resolved unqualified while sibling types required full
qualification, which was a source of confusion for callers writing ad hoc query snippets. Types
that live in unrelated namespaces (or whose short name collides with another type reachable this
way) still require explicit qualification.

The server enforces safety boundaries: only configured metadata references are available at
compile time; `unsafe` code is disabled; query length is capped (via `MaxQueryLength`); and compile/runtime failures are sanitized without logging raw query
text or sensitive provider data. Query *complexity* (as opposed to length) is additionally bounded
before compilation: `MaxExpressionNodes` caps the total number of parsed syntax nodes,
`MaxExpressionDepth` caps nesting depth, and `MaxQueryOperators` caps the number of LINQ
query-operator calls (`Where`, `Select`, `OrderBy`, etc.). Raw `Include`/`ThenInclude` calls in query
text are rejected outright, directing callers to the structured `QueryRequest.Include` parameter
instead (see "Bounded nested include paths" below). All of this is enforced purely from the parsed
syntax tree, before `UserQuerySourceGenerator` builds the generated query context and before Roslyn
compilation, provider translation, or database access can occur; see "Roslyn query complexity
limits" below for details.

No terminal call is required for `IQueryable` results — `run_query` materializes them server-side
and applies an automatic take cap, so fragments like
`Orders.Where(o => o.Number == "123NL")` work without an explicit
`.ToList()`/`.ToListAsync()`/`.FirstOrDefault()`. Adding a terminal element operator or explicit
`Take(n)` still narrows the result. By contrast, already-materialized results or client-side
`IEnumerable` pipelines are returned as scalars, not paged row sets. **Deterministic result ordering
requires an explicit `OrderBy` — the server applies no automatic ordering.**

Execution defaults to `QueryTrackingBehavior.NoTracking`, subject to the selected connection's
command timeout and server cancellation. An explicit `.AsTracking()` can opt back into tracking.
`SaveChanges()`/`SaveChangesAsync()` remain blocked unless
`QueryExecution:AllowMutationsInRunQuery=true` **and** the resolved connection is non-production
`ReadWrite`. Sequence results receive the configured default page when no `Take` is
supplied; any caller `Take` is clamped to `MaxTake`. Terminal scalar aggregates are not paginated.

### P1 #14 — cursor pagination

`run_query` also accepts an opt-in `pagination: { mode: "cursor", cursor?: string }`
object. Cursor pagination is forward-only and applies only to a query whose final
`IQueryable` element is a mapped entity. The query must specify an `OrderBy`; the
server appends any missing primary-key properties as ascending tie-breakers, then
uses the complete ordering for its keyset seek. `Skip()` is mutually exclusive
with cursor mode.

The first request uses `{ mode: "cursor" }`. When `hasMoreRows` is true, the
response includes `nextCursor`; pass it back as `cursor` with the same query to
obtain the next page. Cursors are HMAC-signed opaque tokens bound to the selected
context, entity, and complete ordering shape. A malformed, altered, or mismatched
token always fails with the same generic cursor error and never exposes its
decoded key values. Configure `QueryExecution:CursorSigningKey` when cursors must
survive a server restart; otherwise a process-local random key is used. Omitting
`pagination` preserves existing offset/`Take` behavior and does not return a
cursor.

## `preview_query_sql`

`preview_query_sql` accepts the same request shape as `run_query` (`contextName`, `query`, optional
`connectionName`, optional `targetName`) and reuses `run_query`'s exact root-name resolution,
entity-level access-policy checks, `MaxQueryLength` enforcement, and Roslyn compilation front end
(`EfCoreMcpTools.PreviewQuerySqlCore` mirrors `RunQueryCore` up through query compilation). Instead
of materializing rows, it returns the SQL the query would issue, obtained solely from the compiled
query's still-unexecuted `IQueryable.ToQueryString()`. When successful, the `ToQueryString()` call 
itself does not enumerate the query, open a database connection, create or execute a command, or 
call `SaveChanges` — however, the preceding compilation step executes the user-supplied C# 
expression, which may force early enumeration or side effects.

Only queries whose final value is an unexecuted `IQueryable` have SQL to preview. A
`QueryExecutionException` (surfaced with an actionable "Next step" hint via `FormatQueryError`) is
thrown for:
- scalar/element results, e.g. `Count()`, `FirstOrDefault()`, `Sum()`;
- already-materialized sequences, e.g. `.ToList()`;
- results produced by operators with no SQL translation, e.g. `Zip` (which returns a
  client-side-evaluated `IEnumerable<T>`, not an `IQueryable`).

**Execution mode is required to be in-process for preview; the tool rejects all non-`InProcess`
`QueryExecution:Mode` settings.** `ToQueryString()` requires local, live access to the compiled
`IQueryable`/query provider, and only a fully materialized `QueryResultWire` ever crosses the
out-of-process/pooled query host boundary — never a live, unexecuted `IQueryable`. Extending that
wire protocol to carry an unexecuted query would be substantially more invasive (a new protocol
version, DTOs, host process branches, and pool/worker plumbing) for no added safety benefit, since
the `ToQueryString()` call itself never opens a database connection or executes a command. Note that
the preceding compilation/invocation step executes the caller-supplied C# expression as ordinary code
(via `RoslynQueryExecutor.CompileAndInvokeAsync`), and such an expression can force early enumeration
(e.g., `Customers.ToList().AsQueryable()`) before `ToQueryString()` is reached. This requirement is
enforced by `EfCoreMcpTools.PreviewQuerySqlCore` (which rejects non-`InProcess` modes), and then calls
`RoslynQueryExecutor.PreviewSqlAsync` directly instead of going through `run_query`'s
`ExecuteRoslynAsync` mode switch.

## Access-policy scope and limitations

**Protected by policy:** `run_query` enforces entity-level access control via `EfCoreMcpTools.RunQueryCore` pre-check:
the parsed root identifier must match an existing public `DbSet<T>` property on the selected `DbContext`, and all
referenced entities (detected via regex word-match on property names in the expression text) must also be DbSet-backed.
This ensures queries can only access data through configured entity sets.

**Not protected by policy:** The following access patterns are **outside the policy scope** and are controlled entirely by
your EF Core configuration and DbContext design:
- **DbContext.Set<T>()** — dynamic entity access not rooted in a DbSet property
- **Database.ExecuteSqlRaw()** / **Database.ExecuteSqlInterpolated()** — raw SQL execution
- **Reflection-based access** to internal DbContext members

These patterns require statement-mode or explicit API calls unsupported in `run_query` expression-mode. If your security
model requires blocking these patterns, ensure your DbContext class itself does not expose them, or use your hosting
application's own authorization layer (e.g., role-based access control on the MCP server endpoint).

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
    code, and the `MaxQueryLength` cap (enforced before provider work begins). Complexity limits
    (`MaxExpressionNodes`, `MaxExpressionDepth`, `MaxQueryOperators`) and the raw `Include`/
    `ThenInclude` rejection are enforced the same way - see "Roslyn query complexity limits" below.
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

## Roslyn query complexity limits

`MaxTake` and `DefaultTake` bound result *size*; `MaxQueryLength` bounds the raw query *text*
length. `QueryComplexityValidator` additionally bounds query *shape* by parsing the query text into
a Roslyn syntax tree (the same expression-or-statement parse `UserQuerySourceGenerator` performs)
and checking it against three caps, plus a fixed rejection rule, all configured under
`QueryExecution` and all enforced in `RoslynQueryExecutor.CompileAndInvokeAsync` immediately after
the `MaxQueryLength` check - before `UserQuerySourceGenerator.Generate`, Roslyn compilation, or any
provider/database access:

- `MaxExpressionNodes` (default 500) - the total number of syntax nodes in the parsed tree.
- `MaxExpressionDepth` (default 32) - the deepest nesting level in the parsed tree.
- `MaxQueryOperators` (default 20) - the number of LINQ query-operator method-call nodes (`Where`,
  `Select`, `SelectMany`, `OrderBy`, `GroupBy`, `Join`, aggregates, element operators, `ToList`, etc.).
- Raw `Include`/`ThenInclude` method calls in the query text are always rejected, regardless of
  count, directing callers to the structured `QueryRequest.Include` parameter (see "Bounded nested
  include paths" below) instead. This closes a bypass where a raw `Include`/`ThenInclude` call
  embedded in `Query` text could execute without navigation-path validation, capability checks, or
  the per-parent database-side collection cap that the structured `include` pipeline enforces.

Because all of these checks are performed from the parsed syntax tree alone - no symbol resolution,
no compilation, no DbContext construction - none of them can themselves reach the database.
Violations throw the existing `QueryExecutionException`, naming only the exceeded limit, its
configured maximum, and the observed count (for example, "The query expression contains 812 syntax
nodes, exceeding the configured maximum of 500 (MaxExpressionNodes)"), or - for a raw `Include`/
`ThenInclude` call - directing the caller to the structured `include` parameter; never the caller's
query text, `where`/`orderBy`/`include` values, or parameter data. Because `run_query` and
`preview_query_sql` both route through `RoslynQueryExecutor.CompileAndInvokeAsync`, their
enforcement and error shape are identical, including for the out-of-process and pooled execution
modes, which construct the same executor.

## Database-side collection-include caps (P0 #8 implemented)

`MaxIncludedCollectionItems` bounds every requested collection navigation **per parent** in the
database query plan, before response projection. Each validated collection navigation uses a
provider-translatable filtered include with a deterministic primary-key ordering and `Take`; collection
queries use split-query execution to retain the per-parent bound without N+1 queries. Reference
navigations remain regular includes.

The cap applies independently to every parent, requested branch, and nested collection level. A value
of `0` produces an empty included collection. Root `skip`/`take`, no-tracking execution, cancellation,
timeout handling, and safe scalar projection are unchanged. The response is never made compliant by
truncating an already-materialized collection.

SQLite integration tests cover below/exact/above/zero cap boundaries, deterministic ordering,
independent parent caps, root paging, and executed child-command SQL containing a provider-translated
limit before materialization.

## P0 #9: policy-gated context/entity execution

Before a query path constructs a context, compiles Roslyn code, generates SQL, or connects to the
database, it authorizes the requested `contextName` plus the root `DbSet`/entity and any additional
public `DbSet` roots referenced by the query against the selected connection's `AccessPolicy`. Both
`run_query` and `preview_query_sql` use this shared guard, so no alternate execution path bypasses it.

Denied or unlisted selectors fail closed with a sanitized authorization error and perform no
model/database work. Focused tests cover both execution tools, allowed-over-denied precedence,
unmatched-selector rejection, and unchanged allowed-query behavior.

## Bounded nested include paths (P1 #13 implemented)

`QueryRequest.Include` accepts case-sensitive, dot-separated EF navigation paths such as
`Orders.OrderLines`; existing one-segment entries remain valid. Before execution, each segment
is resolved from the current EF model navigation metadata as a navigation property. Empty, unknown, scalar, duplicate,
over-depth, repeated-navigation, and cyclic paths fail the request with `QueryExecutionException`
before query construction or database execution.

`QueryExecution:MaxIncludeDepth` (default `3`) bounds segments per path, and
`QueryExecution:MaxIncludeCount` (default `5`) bounds paths per request. Successful entity-shaped
queries recursively return mapped scalar values plus only the requested navigation branches. Every
collection branch applies the database-side `MaxIncludedCollectionItems` per-parent cap; reference
branches recurse only when requested. Root paging and the read-only, no-tracking execution contract
remain unchanged.

Focused executor and integration tests cover model-path validation, both limits, recursive result
shape, caps at each collection level, root paging, and the pre-execution rejection path.
