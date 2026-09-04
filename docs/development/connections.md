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
  - The first non-production connection is selected at startup, and `swap_connection` changes
    the active default. This active connection is used as a silent fallback by `get_schema`,
    `run_query`, and the other connection-scoped tools only when `connectionName` is omitted
    *and* at most one connection is registered overall. As soon as two or more connections are
    registered, an omitted `connectionName` throws a `McpException` listing every registered
    connection name (mirroring the `contextName` disambiguation behavior for multiple
    `DbContext`s) instead of silently using whichever connection happens to be active - this
    prevents a stale/unrelated "active" connection from being used unnoticed.
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

## Proposed open work — P0 #9: per-connection context/entity access policy

Extend each server-side `Connections:<connectionName>` entry with a **required** `AccessPolicy` object:

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

All selectors are exact, case-sensitive CLR `DbContext` full names and exact EF entity names; an entity selector is scoped as `<context full name>:<entity name>`. Empty arrays are allowed, but an omitted policy, unknown member, malformed selector, duplicate selector, or selector that cannot resolve against the loaded model is configuration-invalid. The registry must reject invalid policy before serving the connection; it must not infer a default context/entity policy.

The evaluator is fail-closed. A matching `AllowEntities` or `AllowContexts` selector permits a candidate even if the corresponding deny list also matches (**allowlist-over-deny precedence**). Otherwise a matching deny selector rejects it, and no matching allow also rejects it. Entity access requires a permitted context; an entity allow may make that entity available within its context, while it does not expose any other entity. Denials identify only the requested selector and connection, never enumerate permitted or prohibited alternatives.

Focused registry tests cover required-policy validation, exact selector resolution, duplicate/malformed/unresolved values, default denial, and allow-over-deny collisions at both context and entity scope.
