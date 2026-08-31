# Development Roadmap

This document tracks the features needed to get `dotnet-efcore-mcp` from an empty repo to a usable MCP server. Check items off as they're implemented. Sub-bullets under an item are implementation notes, not separate tasks, unless they have their own checkbox.

## 0. Project scaffolding

- [x] Create the .NET solution (`dotnet-efcore-mcp.sln`)
  - Implementation note: created as `dotnet-efcore-mcp.slnx` (the newer XML-based .NET
    solution format) rather than a classic `.sln` file. `dotnet build`/`dotnet test`
    auto-discover it from the repo root; when referencing it explicitly use
    `dotnet-efcore-mcp.slnx`, not `.sln`. Judgment call: the newer format is the current
    default for `dotnet new sln` on the installed SDK (10.0.400) and is fully supported by
    the `dotnet` CLI; no functional difference for this project.
- [x] Create the MCP server project (e.g. `src/DotnetEfCoreMcp.Server`)
- [x] Choose and wire up an MCP server SDK/library (e.g. the official [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk)) with stdio transport as the initial target
  - Implementation note: `ModelContextProtocol` + `ModelContextProtocol.AspNetCore`-style
    hosting pattern via `Microsoft.Extensions.Hosting`:
    `AddMcpServer().WithStdioServerTransport().WithTools<EfCoreMcpTools>()` in
    `src/DotnetEfCoreMcp.Server/Program.cs`.
- [x] Add a `.editorconfig` / analyzer baseline consistent with the rest of the codebase
  - 4-space indent, file-scoped namespaces, nullable-aware analyzer severities (see
    root `.editorconfig`).
- [x] Add a basic test project (e.g. `tests/DotnetEfCoreMcp.Server.Tests`) with the existing test runner wired up
  - xUnit, `ProjectReference` to the server project, plus a pre-build MSBuild target that
    builds `tests/Fixtures/SampleApp` so tests can load its real compiled DLL.
- [ ] Add CI workflow (build + test) if/when this repo gets a CI pipeline
  - Left unchecked: out of scope for this MVP session per the task brief (no CI pipeline
    exists yet in this repo to wire into).

## 1. Assembly loading

- [x] Load a target project's compiled output (`bin/Debug/<tfm>/*.dll`) given a file path
- [x] Load the assembly in an isolated, **collectible** `AssemblyLoadContext` so it can be unloaded/reloaded (e.g. after a rebuild) without restarting the server
- [x] Resolve the target assembly's dependencies (its own `.deps.json` / referenced DLLs alongside it), so it doesn't fail to load due to missing EF Core / provider assemblies
  - Implemented via `AssemblyDependencyResolver` in `TargetAssemblyLoadContext`, so the
    target project's own `.deps.json` / adjacent DLLs resolve correctly.
- [x] Handle assembly load failures gracefully (missing file, wrong TFM/runtime mismatch, missing dependencies) with clear error messages surfaced to the MCP client
- [x] Detect and handle stale/locked DLLs (e.g. project was rebuilt while the server was running)
  - `AssemblyLoaderService` exposes a staleness check based on the DLL's last-write time
    vs. what was loaded; `load_assembly` can always be called again to force a reload
    (old collectible `AssemblyLoadContext` is unloaded first).

## 2. DbContext discovery

- [x] Scan a loaded assembly for types deriving from `Microsoft.EntityFrameworkCore.DbContext`
- [x] Support assemblies with multiple `DbContext` types (list them, let the caller pick one by name)
- [x] Handle `DbContext` types that require constructor arguments (e.g. `DbContextOptions<T>`) — construct via `DbContextOptionsBuilder` rather than assuming a parameterless constructor
- [x] Support `DbContext` types configured via `OnConfiguring` (no options passed in) as a distinct case from ones requiring externally-supplied options
- [x] Handle design-time factories (`IDesignTimeDbContextFactory<T>`) if present, as an alternative construction path

## 3. Connection management (security-sensitive)

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

## 4. Schema / model discovery

- [x] Build the EF Core model for a resolved `DbContext` instance (`context.Model`)
- [x] Expose entity types, their CLR properties, and mapped column names/types
- [x] Expose primary keys, foreign keys, and navigation properties (relationships)
- [x] Expose owned types and inheritance hierarchies (TPH/TPT/TPC) where present
  - `EntityTypeSchema.IsOwned`, `BaseEntityName`, `DiscriminatorProperty`. Not exercised
    by an integration test in this session (the SampleApp fixture has no owned
    types/inheritance), but the reflection code is straightforward metadata access
    (`entityType.IsOwned()`, `.BaseType`, `.FindDiscriminatorProperty()`) with no
    provider-specific behavior — flagged here as a coverage gap rather than a design gap.
- [x] Serialize the discovered schema into a compact, agent-friendly format (e.g. JSON) suitable for an MCP tool response
- [x] Cache the discovered schema per loaded assembly/context and invalidate it when the assembly is reloaded
  - `SchemaCache` keys by `DbContext` CLR type; `load_assembly` reloading into a new
    `AssemblyLoadContext` produces new `Type` instances (old ones are collectible/unloaded),
    so old cache entries can never be returned for a stale assembly — invalidation is a
    natural consequence of the identity change rather than an explicit cache-clear call.

## 5. Query execution

- [x] Define the query input format the agent will send (e.g. a LINQ-like query DSL, a subset of expressions, or a safe query string translated to LINQ)
  - `QueryRequest`: `entity`, `where`, `parameters`, `orderBy`, `skip`, `take`, `include`
    (see `src/DotnetEfCoreMcp.Server/Querying/QueryRequest.cs`), executed via
    `System.Linq.Dynamic.Core`.
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

## 6. MCP tool surface

- [x] `list_contexts` — list discovered `DbContext` types available from the currently loaded assembly
- [x] `get_schema` — return the model/schema for a given context
- [x] `run_query` — execute a read-only Dynamic LINQ query against a given context and entity
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

## 7. Visual Studio Code integration

- [x] Document a first-class `.vscode/mcp.json` setup using the stdio transport and `${workspaceFolder}`
- [x] Prompt for connection strings with a password-masked `${input:...}` variable so secrets are not committed
- [x] Discover C# project output assemblies beneath a configured `WorkspacePath`
  - Candidate discovery matches each `.csproj` output name, including explicit `AssemblyName`, and excludes dependency, `ref`, and `refint` DLLs.
  - Ranking prefers Debug over custom configurations over Release, then newest output and highest target framework.
- [x] Automatically load the preferred candidate at startup when `TargetAssemblyPath` is unset
  - An explicit target remains the highest-priority override; no candidates or discovery failures are non-fatal.
- [x] Expose `list_assembly_candidates` so agents can inspect alternatives and switch with `load_assembly`
- [x] Cover ranking, filtering, custom assembly names, empty output, and invalid workspace behavior with focused tests

## 8. Auditing & observability

- [x] Log every executed query (context, entity, query shape, row count, duration) without logging secrets
  - `QueryExecutor` logs (via injected `ILogger<QueryExecutor>`) the context type, entity
    name, effective skip/take, included navigation names, row count, and elapsed
    milliseconds for every `ExecuteAsync` call, plus a warning-level log on failure (entity/
    context only, never the connection string or full exception detail that could include
    it). `EfCoreMcpTools` similarly logs `load_assembly`/`get_schema` invocations.
- [x] Add structured logging with configurable verbosity
  - Standard `Microsoft.Extensions.Logging` structured logging (named parameters, not
    string interpolation), verbosity configurable via the normal `Logging:LogLevel`
    configuration section in `appsettings.json`/environment variables.
- [ ] Add basic metrics/telemetry hooks (optional, later-stage)
  - Left unchecked: explicitly optional/later-stage per the roadmap; no metrics
    pipeline exists in this repo yet to hook into (would need an OpenTelemetry or similar
    dependency decision that's out of scope for this MVP session).

## 9. Documentation

- [x] Document how to configure a target project + connection string once the config format is decided
- [x] Document the MCP tool contract (inputs/outputs) for each tool once implemented
- [x] Update [README.md](./README.md)'s "Getting started" section once the initial solution exists
