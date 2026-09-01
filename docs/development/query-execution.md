# Query execution

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: [`src/DotnetEfCoreMcp.Server/Querying`](../../src/DotnetEfCoreMcp.Server/Querying) ·
Tests: [`tests/DotnetEfCoreMcp.Server.Tests/Querying`](../../tests/DotnetEfCoreMcp.Server.Tests/Querying)

See also the [README "MCP tool contract"](../../README.md#mcp-tool-contract) section for the
`run_query` request/response shape and Dynamic LINQ parameter-binding rules.

- [x] Define the query input format the agent will send (e.g. a LINQ-like query DSL, a subset of expressions, or a safe query string translated to LINQ)
  - `QueryRequest`: `entity`, `where`, `parameters`, `orderBy`, `skip`, `take`, `include`
    (see [`QueryRequest.cs`](../../src/DotnetEfCoreMcp.Server/Querying/QueryRequest.cs)),
    executed via `System.Linq.Dynamic.Core`.
- [x] Translate/execute the incoming query against the real `DbSet<T>` for the requested entity
- [x] Enforce read-only execution by default (no `SaveChanges`, no tracked entities, `.AsNoTracking()`)
- [x] Enforce a maximum result size / row limit and require pagination for larger result sets
  - `QueryExecutionOptions.MaxTake` (default 200) is enforced via `Math.Clamp` regardless
    of what the caller requests or omits; `skip`/`take` are always honored for paging.
- [x] Enforce a query timeout (command timeout / cancellation token) to avoid runaway queries
- [x] Reject or restrict unsafe query shapes (e.g. arbitrary raw SQL, unbounded `.Include()` graphs) unless explicitly allowlisted
  - `run_sql_query` is disabled by default and must be explicitly enabled through
    `RawSqlExecution:Enabled`. It is independently rejected for production and non-`ReadWrite`
    connections, even when globally enabled. `include` entries are validated against the entity's
    actual navigation property names (rejecting anything else), and the resulting projection is
    hard-capped to exactly one level of navigation depth regardless of what is requested — there
    is no way to request a deeper/unbounded graph.
  - Only EF-model-mapped scalar properties are ever reflected over and projected (via
    `IEntityType.GetProperties()`/`GetNavigations()`), never arbitrary public CLR members —
    so `[NotMapped]` computed properties or unrelated members can't leak into results.
  - `QueryExecutionOptions.MaxIncludedCollectionItems` (default 200) caps how many items are
    materialized per included *collection* navigation (e.g. `include=["Orders"]`), so a
    single row with a huge one-to-many collection can't bypass the top-level row cap.
- [x] Serialize query results (including related/included entities) into a response format that avoids circular references
  - Cycle-safety is structural (depth-bounded dictionary projection, not tracked EF Core
    entities), with `System.Text.Json`'s `ReferenceHandler.IgnoreCycles` as a
    defense-in-depth second layer in the MCP tool serialization step.
- [x] Surface EF Core / provider exceptions as clear, sanitized error messages (no leaking connection strings or stack traces with sensitive info)
  - `QueryExecutionException` messages never include connection strings (only entity/
    context/parameter-shape information); provider exceptions are wrapped, not passed
    through verbatim.
