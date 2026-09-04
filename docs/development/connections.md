# Connection management (security-sensitive)

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: [`src/DotnetEfCoreMcp.Server/Connections`](../../src/DotnetEfCoreMcp.Server/Connections) ·
Tests: [`tests/DotnetEfCoreMcp.Server.Tests/Connections`](../../tests/DotnetEfCoreMcp.Server.Tests/Connections)

See also the [README "Configure connections"](../../README.md#configure-connections-server-side-only) section for user-facing setup instructions.

- [x] Define a "Connections" registry on the **server side** (not read from the target project's own config) mapping a logical connection name → provider + connection string
- [x] Support at least one connection string per configured `DbContext`/environment (e.g. `MyApp.Context` → SQL Server connection string)
- [x] Store connection strings securely at rest:
  - [x] Never persist secrets in plain text in the repo/config committed to source control
    - `appsettings.json` only ships an empty `Connections` placeholder; real entries go
      into `dotnet user-secrets` (dev) or environment variables (any environment).
  - [x] Support loading from environment variables and/or OS-level secret stores (e.g. `dotnet user-secrets`, environment variables, or a mounted secrets file) as the primary mechanism
    - MVP supports `dotnet user-secrets` + `DOTNETEFCOREMCP_`-prefixed environment
      variables (env vars naturally override user-secrets in the default
      `IConfiguration` provider order). Production deployments should instead mount a
      secrets file or use a real vault (e.g. Azure Key Vault, HashiCorp Vault) — that
      integration is out of scope for this MVP but the `ConnectionRegistry` only depends
      on `IConfiguration`, so adding another provider later is a Program.cs-only change.
  - [x] Redact connection strings from all logs, error messages, and MCP tool output
    - `ConnectionRegistryEntry.ToString()` never includes the raw connection string.
  - [x] Keep unexpected tool-failure diagnostics safe and development-only
    - By default, MCP callers receive an opaque error reference only; the full exception is
      logged to server stderr with the same reference.
    - To add a safe failure category and vetted recovery hint while developing, set
      `ToolDiagnostics:ExposeSafeErrorDetails=true` (or
      `DOTNETEFCOREMCP_ToolDiagnostics__ExposeSafeErrorDetails=true`). The setting is
      forcibly ignored unless the server host environment is `Development`.
    - This mode never returns raw exception or inner-exception messages, stack traces,
      connection strings, SQL, query parameters, server names, or other provider data.
- [x] Validate/allowlist which providers are supported initially (e.g. SQL Server, PostgreSQL, SQLite) and reject unknown providers explicitly
  - Supported: `Sqlite`, `SqlServer`, `PostgreSql` (PostgreSQL). Unknown provider names throw
    `ConnectionRegistryConfigurationException` at registry construction time (fail fast,
    not on first use).
- [x] Enforce that a given `DbContext` type can only ever be connected using connection strings from the server-side registry, never arbitrary strings supplied by the MCP client/agent
  - The `run_query`/`get_schema` MCP tools accept only a `connectionName` string; there is
    no code path anywhere that accepts a raw connection string from a client.
- [x] Support per-connection access scoping (e.g. read-only vs. read-write) as a registry-level setting, independent of the database user's own permissions
  - `ConnectionRegistryEntry.AccessMode` (`ReadOnly`/`ReadWrite`, default `ReadOnly`) is
    parsed and validated. Implementation note / known limitation: this MVP exposes no
    write-capable MCP tool at all (query execution is unconditionally
    `.AsNoTracking()`, no `SaveChanges` path exists anywhere), so `AccessMode` is
    currently informational/forward-compatible only and not yet consulted by any code
    path. It becomes load-bearing once a future write tool is added — flagged here rather
    than silently doing nothing.
- [x] Fail closed: if no matching connection is configured for a requested context, refuse to connect rather than falling back to any default
  - `ConnectionRegistry.Get` throws `UnknownConnectionException` (listing known names) for
    any name not present in the registry; there is no fallback to an unconfigured connection.
- [x] Classify named connections as `Development`, `Staging`, `Production`, or `Unspecified`
  - Environment metadata is returned by `list_connections` without exposing connection
    strings. Existing configuration remains compatible by defaulting to `Unspecified`.
- [x] Maintain an active connection that can be changed at runtime
  - The first non-production connection is selected at startup. `swap_connection` changes
    the active default; `get_schema` and `run_query` use it when `connectionName` is omitted.
- [x] Apply RSFU safeguards to production connections
  - Production is forced to `ReadOnly`, never auto-selected, and requires an explicit
    `allowProduction: true` acknowledgement in `swap_connection`. A production-only
    registry starts with no active connection.
- [x] Add `test_connection` as a narrowly scoped, server-side connection-health diagnostic
  - `ConnectionHealthChecker` runs a single bounded `DbContext.Database.CanConnectAsync` probe,
    linking the MCP `CancellationToken` with a timeout derived from the resolved entry's
    `CommandTimeoutSeconds` plus `QueryExecutionOptions.CancellationMargin` (the same
    defense-in-depth shape used by query execution). Genuine caller cancellation propagates as
    `OperationCanceledException`; the internal timeout instead yields the `TimedOut` status, and any
    provider failure yields `Failed` - neither ever surfaces the underlying exception. The
    `test_connection` MCP tool resolves the connection through the existing fail-closed
    `ResolveConnection` path (explicit name, active-connection fallback, or `McpException` for
    unknown/inactive connections), constructs only the requested `DbContext`, and returns a redacted
    payload (context name, connection name, provider, environment, status) with only safe
    identifiers logged. It never accepts a raw connection string, executes user SQL, calls
    `SaveChanges`, or changes the active connection.

## P0 #9: per-connection context/entity access policy

Code: [`Connections/ConnectionAccessPolicy.cs`](../../src/DotnetEfCoreMcp.Server/Connections/ConnectionAccessPolicy.cs) ·
[`Schema/SchemaAccessPolicy.cs`](../../src/DotnetEfCoreMcp.Server/Schema/SchemaAccessPolicy.cs) ·
Tests: [`Connections/ConnectionAccessPolicyTests.cs`](../../tests/DotnetEfCoreMcp.Server.Tests/Connections/ConnectionAccessPolicyTests.cs) ·
[`Schema/ConnectionSchemaAccessPolicyTests.cs`](../../tests/DotnetEfCoreMcp.Server.Tests/Schema/ConnectionSchemaAccessPolicyTests.cs) ·
[`Tools/EfCoreMcpToolsAccessPolicyTests.cs`](../../tests/DotnetEfCoreMcp.Server.Tests/Tools/EfCoreMcpToolsAccessPolicyTests.cs)

- [x] Extend each server-side `Connections:<connectionName>` entry with a `AccessPolicy` object

  ```json
  {
    "AccessPolicy": {
      "AllowContexts": ["MyApp.Data.AppDbContext"],
      "DenyContexts": ["MyApp.Data.AdminDbContext"],
      "AllowEntities": ["MyApp.Data.AppDbContext:Order"],
      "DenyEntities": ["MyApp.Data.AppDbContext:AuditEntry"]
    }
  }
  ```

  All selectors are exact, case-sensitive CLR `DbContext` full names and exact EF entity
  names; an entity selector is scoped as `<context full name>:<entity name>`. Empty/omitted
  arrays are allowed (an absent `AccessPolicy` defaults to fully empty, i.e. everything
  denied — fail-closed, not fail-open). A malformed entity selector (missing/empty
  `context:entity` parts) or a selector that cannot resolve against the loaded model throws
  `ConnectionRegistryConfigurationException` — the registry/connection resolution rejects
  invalid policy before serving the connection rather than silently ignoring it.
- [x] Fail-closed evaluator with allow-over-deny precedence
  - `ConnectionAccessPolicy.IsContextReachable`/`IsContextAllowed`/`IsEntityAllowed` in
    [`ConnectionAccessPolicy.cs`](../../src/DotnetEfCoreMcp.Server/Connections/ConnectionAccessPolicy.cs):
    a matching `AllowEntities` or `AllowContexts` selector permits a candidate even if the
    corresponding deny list also matches (allowlist-over-deny precedence). Otherwise, a
    candidate that matches no allow selector is denied by default — deny lists therefore never
    widen access, they only exist for future policy audit/documentation clarity.
  - Entity access requires the context to be reachable; a narrower entity-level allow makes
    that one entity visible without granting blanket access to the rest of the context.
- [x] Enforce before context construction and query parsing
  - `EfCoreMcpTools.ResolveConnection` calls `AccessPolicy.EnsureResolvable` against the
    discovered `DbContext`/entity model on every connection resolution (misconfigured policy
    surfaces as `ConnectionRegistryConfigurationException`, distinct from a runtime denial).
  - `EnsureContextReachable`/`EnsureEntityAllowed` gate `GetSchema`, `GetEntitySchema`,
    `SearchSchema`, `RunQuery` (root `DbSet` name, both the Dynamic LINQ and Roslyn engines),
    and the entity mutation tools (`InsertEntity`/`UpdateEntity`/`DeleteEntity`) — all run
    before a `DbContext` is constructed or a query string is parsed.
- [x] Hook into `ISchemaAccessPolicy` to filter schema output without mutating the cache
  - `ConnectionSchemaAccessPolicy` (in
    [`Schema/SchemaAccessPolicy.cs`](../../src/DotnetEfCoreMcp.Server/Schema/SchemaAccessPolicy.cs))
    implements `ISchemaAccessPolicy.Apply` by projecting a filtered `SchemaDto` — denied
    entities, and their foreign keys/navigations/base-type references from other entities, are
    stripped from the copy returned to the caller. `GetSchema`, `GetEntitySchema`, and
    `SearchSchema` all route the shared, cached `SchemaDocument`/`SchemaDto` through a
    freshly-constructed policy instance per request via `SchemaSlicer`, so the cache entry
    itself is never mutated or replaced.
- [x] Reject denied/unknown requests through a unified, non-disclosing path
  - `AccessPolicyDeniedException`/`ConnectionRegistryConfigurationException` are translated to
    a plain `McpException` in `EfCoreMcpTools.Execute`/`ExecuteAsync`. Denial messages name
    only the requested selector and connection — never the set of permitted/prohibited
    alternatives — so a denied name is indistinguishable from a name that doesn't exist in the
    model at all (verified by `GetEntitySchema_DeniedEntityLooksIdenticalToAnUnknownEntity` and
    the "known entities" list in `get_entity_schema` being drawn only from the
    policy-filtered/visible schema).
  - `ListContexts` filters its `Descriptors` list to what the active connection's policy makes
    reachable when a connection is active; with no active connection (nothing configured yet)
    it stays unfiltered, since there is no policy to consult.

Focused tests cover precedence, default denial, reachability vs. blanket context allow,
`EnsureResolvable` selector-resolution failures, entity-selector parsing, schema filtering/
non-mutation/non-disclosure, and enforcement across `get_schema`/`get_entity_schema`/
`search_schema`/`run_query`/entity-mutation tool paths plus `list_contexts` filtering.
