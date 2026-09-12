# Startup-derived connections

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: `src/DotnetEfCoreMcp.Server/Connections/ConnectionSource.cs`,
`ConnectionRegistryEntry.cs`, `src/DotnetEfCoreMcp.Server/DbContextDiscovery/DbContextActivator.cs`,
`src/DotnetEfCoreMcp.Server/Querying/RoslynQueryExecutor.cs`, `OutOfProcessRoslynQueryExecutor.cs`,
`src/DotnetEfCoreMcp.Server/Tools/EfCoreMcpTools.cs`, `src/DotnetEfCoreMcp.QueryHost` · Tests:
`tests/DotnetEfCoreMcp.Server.Tests/Connections/ConnectionRegistryTests.cs`,
`.../DbContextDiscovery/DbContextActivatorTests.cs`, `.../Compilation/UserQuerySourceGeneratorTests.cs`,
`.../Querying/RoslynQueryExecutorTests.cs`, `.../Querying/OutOfProcessRoslynQueryExecutorTests.cs`,
`.../Tools/EfCoreMcpToolsApplicationFactoryTests.cs`

> **P2 #16 — Startup-derived connections (`ApplicationFactory` source).** Implemented.
> `run_query` supports `ApplicationFactory` connections when `QueryExecution:Mode` routes
> execution out-of-process/pooled/auto; every other tool (`preview_query_sql`, `get_schema`,
> `get_entity_schema`, `run_sql_query`, `list_migrations`, `generate_migration_script`, mutation
> tools) rejects `ApplicationFactory` connections before constructing a `DbContext` in the MCP
> server process, since only `run_query`'s out-of-process path currently has an isolated-host
> route to construct one safely. See [WORK-TRACKER.md](./WORK-TRACKER.md) for the open-items
> summary.

## Goal

Let an operator register a connection whose provider and connection string are **not**
supplied to the server at all. Instead, the server invokes the target application's own
`IDesignTimeDbContextFactory<TContext>` and trusts *that* factory's fully-configured
`DbContext` — including whatever connection string the factory resolved internally from
its own configuration, secret store, or environment. This removes the need to duplicate a
target application's secret-resolution logic in the MCP server's own `Connections`
configuration, at the cost of the server no longer being able to see, log, or override the
resulting connection string.

This is explicitly an **opt-in, out-of-process-only** capability, never a default. It is
additive to — not a replacement for — the existing [server-side `Connections`
registry](./connections.md), which remains the default and recommended mechanism.

## Context & rationale

[Connection management](./connections.md) states the MVP contract plainly: "Define a
'Connections' registry on the **server side** (not read from the target project's own
config)". That design choice is deliberate and remains correct as the default: it keeps
secret material entirely under the server operator's control, gives every connection a
mandatory `AccessPolicy` (fail-closed allowlist, [P0 #9](./connections.md#p0-9-per-connection-context-entity-access-policy)),
and lets `ConnectionRegistryEntry.ToString()` guarantee redaction because the server
always already knows the exact string it must never print.

The prior feasibility assessment for this session identified a real gap in that model:
many real applications already have a working `IDesignTimeDbContextFactory<TContext>`
(required for `dotnet ef migrations add` to work at all) whose `CreateDbContext(string[])`
implementation already knows how to build a fully-configured, connected `DbContext` —
often by reading the *same* `appsettings.{Environment}.json` / user-secrets / Key Vault
/ environment-variable chain the running application itself uses. Today,
[`DbContextActivator.CreateInstance`](../../src/DotnetEfCoreMcp.Server/DbContextDiscovery/DbContextActivator.cs)
happily invokes that factory (`DbContextConstructorShape.DesignTimeFactory`) but then
immediately discards its result via `OverrideConnectionString`, which unconditionally
calls `instance.Database.SetConnectionString(entry.ConnectionString)` — replacing whatever
the factory resolved with the server's own registry entry. An operator who wants to point
the MCP server at "however this app already configures itself" has no supported way to do
that; they must re-derive and duplicate the same secret into the server's own
`Connections` configuration.

P2 #16 makes that override **conditional**: a new opt-in connection source lets an
operator say "for this logical connection, don't give me a connection string at all —
just run the target's own factory, out-of-process, and trust what it builds." This is
strictly additive: existing `Connections:<name>` entries are unaffected, `CreateInstance`'s
existing four activation paths are unchanged, and `OverrideConnectionString` keeps its
current unconditional behavior for every entry that still carries a `ConnectionString`.

### Why out-of-process only

The factory's `CreateDbContext` method is arbitrary third-party code: it may read
`Environment.GetEnvironmentVariable`, hit a secret manager over the network, mutate static
state, or throw in ways unrelated to database connectivity. Even today, in-process
activation already runs this code in the server's process; P2 #16 does not change that
core trust boundary. What it changes is that the server can no longer inspect or
override the *result*, which makes an already-arbitrary-code-execution path also an
already-arbitrary-connection-string path. Requiring `QueryExecution:Mode=OutOfProcess`
(or `Pooled`) for any `ApplicationFactory`-sourced connection keeps that resolved,
un-inspectable connection string confined to the disposable/recycled `DotnetEfCoreMcp.QueryHost`
process and off the main server's own stack/heap, matching the isolation rationale
already documented in [Out-of-process query execution](./query-execution-alternatives.md)
and [host pooling](./query-execution-host-pooling.md). In-process execution
(`QueryExecution:Mode=InProcess`) rejects an `ApplicationFactory` connection outright with
a clear configuration error rather than silently downgrading isolation.

## Non-goals (deferred)

Per the prior feasibility assessment, explicitly out of scope for this plan/MVP:

- Invoking a target's actual `Program.Main`/minimal-hosting startup pipeline (ASP.NET
  Core `WebApplicationBuilder`, generic `Host`, DI container, middleware, etc.) to obtain
  a `DbContext` the way the running application itself would. Only the narrower, already
  EF-Core-standard `IDesignTimeDbContextFactory<TContext>` contract is supported.
- A "bridge mode" package that runs *inside* the target application's own process and
  reports back to the MCP server (would remove the out-of-process constraint but is a
  much larger, separately-scoped feature).
- Any mechanism that returns the resolved connection string (or enough information to
  reconstruct it) to the MCP client. It stays confined to the query host process.
- Multiple simultaneous `ApplicationFactory` connections resolving to *different*
  factory-provided providers for the same logical name (a single logical connection has
  one fixed target-assembly + context-type + factory tuple, same as today).

## Configuration and tool-surface changes

### New `Source` discriminator on `Connections:<name>`

Extend `ConnectionRegistryEntry`/its options-binding counterpart with a `Source`
discriminator, defaulting to today's implicit behavior:

```jsonc
"Connections": {
  "Staging": {
    // existing shape, unchanged - Source defaults to "Explicit"
    "Provider": "SqlServer",
    "ConnectionString": "Server=...",
    "AccessMode": "ReadOnly",
    "Environment": "Staging",
    "AccessPolicy": { "AllowedContexts": ["MyApp.Data.AppDbContext"] }
  },
  "LocalFromFactory": {
    "Source": "ApplicationFactory",
    "AccessMode": "ReadOnly",
    "Environment": "Development",
    "AccessPolicy": { "AllowedContexts": ["MyApp.Data.AppDbContext"] }
    // No "ConnectionString" and no "Provider" - both are supplied by the
    // target's own IDesignTimeDbContextFactory<TContext> at resolution time.
  }
}
```

- `Source` is an enum: `Explicit` (default, current behavior) or `ApplicationFactory`.
- When `Source == ApplicationFactory`, `ConnectionString` **must be absent** —
  `ConnectionRegistry` throws `ConnectionRegistryConfigurationException` at startup if
  both are present, to avoid ambiguity about which one is authoritative. `Provider` is
  optional and, if present, is validated (not overridden) against the provider the
  factory-built context reports via its `Database.ProviderName`, so a mismatch fails
  fast with a clear message instead of silently querying the wrong provider's SQL
  dialect.
- `AccessPolicy`, `AccessMode`, `Environment`, and `CommandTimeoutSeconds` remain
  **mandatory/meaningful exactly as today** — this is the key security invariant: an
  `ApplicationFactory` connection still goes through `ConnectionRegistry`, still requires
  an explicit `AccessPolicy` (fail-closed, [P0 #9](./connections.md#p0-9-per-connection-context-entity-access-policy)),
  and is still subject to `IsProduction`/`AccessMode` update-forbidding. Only the literal
  connection-string material is sourced differently; every other guarantee
  [Connection management](./connections.md) documents is unchanged.
- `ConnectionRegistryEntry.ConnectionString` becomes nullable at the type level (or is
  replaced with an internal discriminated representation); every existing call site that
  reads it (`OverrideConnectionString`, health checks, redaction paths) must branch on
  `Source` first. `ToString()`'s existing redaction guarantee extends unchanged, since
  there is even less to redact — the field the server would print is simply never
  populated for this source.
- The `list_connections` tool output already omits the raw connection string; it gains a
  `source: "applicationFactory" | "explicit"` field so a client can tell which kind of
  connection it's looking at (still no secret material exposed either way).

### `DbContextActivator` changes

- `CreateInstance` keeps its existing signature and four activation paths unchanged for
  `Source == Explicit`.
- A new overload/parameter (e.g. `bool trustFactoryConnectionString = false`, threaded
  from `ConnectionRegistryEntry.Source`) makes the call to `OverrideConnectionString`
  conditional: when `trustFactoryConnectionString` is `true`, the design-time factory's
  `CreateDbContext(string[])` result is returned as-is, with its own
  `Database.GetConnectionString()` value intact.
- `trustFactoryConnectionString = true` is only valid when `DetermineConstructorShape`
  returns `DbContextConstructorShape.DesignTimeFactory`. Every other shape
  (`GenericOptions`, `NonGenericOptions`, parameterless/`OnConfiguring`) throws a clear
  `DbContextActivationException` at resolution time — an `ApplicationFactory` connection
  requires the target context to expose an `IDesignTimeDbContextFactory<TContext>`,
  full stop. This keeps the "arbitrary code, but a single well-known entry point"
  property that makes this feature reviewable.
- After construction, the provider actually configured on the returned context
  (`instance.Database.ProviderName`) is read and compared to any `Provider` declared in
  the registry entry (see above) and to the provider the server otherwise would have
  inferred from the loaded target assembly, surfacing a descriptive mismatch error rather
  than executing against the wrong SQL dialect.

### Protocol / execution changes

- `OutOfProcessQueryRequest.Connection` (`ConnectionRegistryEntry`, required today)
  becomes capable of carrying an `ApplicationFactory` entry with `ConnectionString ==
  null`; no new request type is needed since the entry itself now carries the
  discriminator. Bump `CurrentProtocolVersion` from `3` to `4` so an old/new
  server↔host mismatch fails the existing "unsupported protocol version" check rather
  than deserializing a null connection string unexpectedly.
- [`QueryHost/Program.cs`](../../src/DotnetEfCoreMcp.QueryHost/Program.cs) branches: when
  `request.Connection.Source == ApplicationFactory`, it resolves the `DbContext` via
  `DbContextActivator.CreateInstance(contextType, request.Connection, request.Provider,
  trustFactoryConnectionString: true, migrationsAssembly: ...)` instead of the path
  `RoslynQueryExecutor.ExecuteAsync` currently takes internally; `RoslynQueryExecutor`
  itself needs the same conditional threaded through wherever it currently calls
  `DbContextActivator`/`OverrideConnectionString` directly for its Roslyn-compiled
  `UserQuery_{token}` subclasses (see the existing internal-visibility comment on
  `OverrideConnectionString`).
- Discovery paths (`list_contexts`, `get_schema`, `check_connection_health`) that build
  a context in-process today via `EfCoreMcpTools.CreateContext` must also honor
  `Source == ApplicationFactory` by refusing in-process construction and either (a)
  routing through the same out-of-process host for a one-shot "resolve and describe"
  call, or (b) returning a clear "this connection requires out-of-process execution;
  schema/health for it available only via `run_query`" error for the in-process-only
  tools. Given schema discovery is comparatively low-risk (structural metadata rather
  than data), the simpler, more consistent option (a) is preferred: add an out-of-process
  "describe" request variant reusing the same host process so `get_schema` and
  `check_connection_health` behave identically regardless of connection source.

## Security / redaction plan

- **Never returned to the client.** The resolved connection string exists only inside
  the `DotnetEfCoreMcp.QueryHost` process's memory for the lifetime of one request (or
  one pooled-host lease). It is never included in `OutOfProcessQueryResponse`,
  `QueryResultWire`, or any tool output.
- **Never logged.** Audit every catch block reachable from an `ApplicationFactory`
  resolution path (`DbContextActivator.Invoke`, the design-time-factory branch of
  `CreateInstance`, `QueryHost/Program.cs`'s top-level catches) to confirm exception
  messages never interpolate `Database.GetConnectionString()` or any factory-thrown
  exception's `Data`/`ToString()` that might embed one. Where EF Core's own exceptions
  might embed a connection string (e.g. a provider-specific `SqlException`), apply the
  same sanitization pattern already used for cursor-token errors
  ([`query-execution.md`](./query-execution.md#p1-14-cursor-pagination)): a fixed,
  generic error message, with the real exception detail only in server-side (never
  MCP-client-facing) diagnostic logs, and even then with the connection string field
  scrubbed via a small regex/allowlist redactor rather than trusting the exception not
  to contain one.
- **`AccessPolicy` still fully enforced.** Because `ApplicationFactory` connections stay
  inside `ConnectionRegistry`/`ConnectionRegistryEntry`, `EnsureContextReachable` /
  `EnsureEntityAllowed` / `IsContextReachable` continue to run exactly as they do today —
  no new bypass is introduced. This is the reason the plan keeps the feature inside the
  existing registry rather than inventing a parallel "startup profile" concept.
- **Production protection unaffected.** `IsProduction`/`AccessMode` read straight off
  the registry entry regardless of `Source`, so an `ApplicationFactory` connection marked
  `Environment: Production` still can't be set active without the existing
  acknowledgment flow and is still denied writes.
- **Fail-closed on ambiguity.** Any of: `Source == ApplicationFactory` with a
  `ConnectionString` present, a missing `AccessPolicy`, a target `DbContext` without a
  discoverable `IDesignTimeDbContextFactory<TContext>`, or `QueryExecution:Mode ==
  InProcess`, throws a configuration/activation exception rather than silently falling
  back to another resolution path.

## Testing plan

> **Status: implemented.** The fixture and every test category below exist and pass
> (`tests/Fixtures/SampleApp/ApplicationFactoryDbContext.cs`,
> `ConnectionRegistryTests.Get_ApplicationFactoryConnection_AllowsNoConnectionString`,
> `DbContextActivatorTests`, `RoslynQueryExecutorTests`,
> `OutOfProcessRoslynQueryExecutorTests.ExecuteAsync_ApplicationFactoryConnection_UsesFactoryConnectionInIsolatedHost`,
> and `EfCoreMcpToolsApplicationFactoryTests` for the tool-layer rejection paths). The
> checklist below is retained as the canonical description of what each category covers.

- **Fixture.** Extend `tests/Fixtures/SampleApp` with a second, dedicated
  `DbContext` + `IDesignTimeDbContextFactory<TContext>` pair (or extend the existing
  [`DesignTimeFactory.cs`](../../tests/Fixtures/SampleApp/DesignTimeFactory.cs)) whose
  `CreateDbContext` resolves its connection string from an environment variable /
  in-memory test double for a fake secret store, so tests can assert the server used
  *that* resolution path rather than any registry-supplied string.
- **`ConnectionRegistry` unit tests** (`tests/DotnetEfCoreMcp.Server.Tests/Connections`):
  - Binds `Source: ApplicationFactory` with no `ConnectionString` successfully.
  - Rejects `Source: ApplicationFactory` + a present `ConnectionString`
    (`ConnectionRegistryConfigurationException`).
  - Rejects a missing `AccessPolicy` on an `ApplicationFactory` entry, same as today for
    `Explicit` entries.
- **`DbContextActivator` unit tests** (`.../DbContextDiscovery`):
  - `trustFactoryConnectionString: true` returns a context whose connection string
    equals the factory's own resolved value, not any registry value.
  - `trustFactoryConnectionString: true` combined with a non-factory constructor shape
    throws the new descriptive `DbContextActivationException`.
  - Provider-mismatch between the declared `Provider` and the factory-resolved
    provider throws with a message that never echoes the connection string.
- **Out-of-process integration tests** (`.../Querying`):
  - `run_query` against an `ApplicationFactory` connection round-trips real rows from
    the fixture's own (SQLite/in-memory) database, proving out-of-process execution
    used the factory's own resolution.
  - `QueryExecution:Mode=InProcess` with an `ApplicationFactory` connection fails fast
    with a clear configuration error, never attempting in-process activation.
  - `get_schema`/`check_connection_health` against an `ApplicationFactory` connection
    succeed via the new out-of-process "describe" path.
  - A forced factory exception (e.g. simulated missing secret) surfaces a sanitized
    error with no connection-string fragment, verified via a snapshot/string-contains
    negative assertion against the raw secret value used in the fixture.
- **Redaction regression test.** A single test that constructs an
  `ApplicationFactory` entry with a known, distinctive fake connection-string value and
  asserts that value never appears in: `list_connections` output, `run_query`/`get_schema`
  error text, or any captured log line across the above scenarios.

## Phased rollout

1. **Phase 1 (this plan's MVP).** `ConnectionRegistryEntry.Source`, `DbContextActivator`
   conditional override, out-of-process-only enforcement, protocol bump, fixture +
   focused tests as above. `get_schema`/`check_connection_health` may ship slightly
   after `run_query` if the out-of-process "describe" request variant needs more design
   time — `run_query` alone already delivers the core value ("don't hand-configure a
   connection string the app already knows").
2. **Phase 2 (future, separately scoped).** Consider secret-store-aware diagnostics
   (e.g. a `validate_connection` tool call that reports *whether* the factory resolved
   successfully without executing any query), and evaluate demand for the deferred
   "invoke real startup pipeline" and "bridge mode" options from the Non-goals section
   before committing to either.

## Work-tracker entry

Phase 1 above has landed: `ConnectionRegistryEntry.Source`/`ConnectionSource.ApplicationFactory`,
the `DbContextActivator` trusted-factory override, source-generator and `RoslynQueryExecutor`
support for `DesignTimeFactory`-shaped contexts, the protocol version bump to `4`, and the
out-of-process/tool-layer enforcement described above are all implemented and covered by focused
tests. Phase 2 (secret-store-aware diagnostics such as a `validate_connection` tool, and any
future "invoke real startup pipeline"/"bridge mode" work from the Non-goals section) remains
unscoped and is not tracked as an open item until there is concrete demand. The **P2 #16** row has
been removed from [WORK-TRACKER.md](./WORK-TRACKER.md) accordingly.
