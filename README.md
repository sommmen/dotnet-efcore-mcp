# dotnet-efcore-mcp

An [MCP](https://modelcontextprotocol.io/) (Model Context Protocol) server that lets an AI agent query an existing **Entity Framework Core** application's database — without writing a single line of app code.

Point it at a compiled project (its `bin/Debug/**/*.dll` output), give it a connection string, and it will:

1. Load the assembly and discover the `DbContext` type(s) it contains.
2. Reflect over the EF Core model (entities, properties, keys, relationships) to build a schema the agent can understand.
3. Accept LINQ-style queries from the agent and execute them against the real database through the real `DbContext` — read-only by default.

The goal is a generic, reusable bridge between "I have an EF Core project" and "an AI agent can safely explore and query its data", instead of hand-writing MCP tools per project.

## Why

Most existing EF Core + MCP integrations require the tools to be baked into the application itself (source generators, hand-written MCP tool methods, etc.). There isn't a mature, drop-in server that:

- Accepts an **arbitrary, already-compiled** `DbContext` assembly.
- Discovers its model dynamically via reflection instead of design-time source generation.
- Exposes safe, generalized schema discovery and querying to an MCP client.

This project aims to fill that gap. See [DEVELOPMENT.md](./DEVELOPMENT.md) for the current feature status and module guides.

## How it works

```
Agent (MCP client)
   │  MCP tools: list_contexts, get_schema, run_query, ...
   ▼
dotnet-efcore-mcp server
   │  1. Loads target assembly (AssemblyLoadContext, isolated/collectible)
   │  2. Locates DbContext type(s), builds/serializes the EF Core model
   │  3. Resolves connection string for the requested context (Connections registry)
   │  4. Builds a DbContext instance, translates the requested query, executes it
   ▼
Target application's database
```

- **The target project is never modified.** We load its build output (the `.dll` in `bin/Debug/<tfm>/`), not its source.
- **Connections are configured on the MCP server**, not read from the target project's own configuration (appsettings, user secrets, etc.), so the server controls what a context is allowed to connect to.
- **Queries run against the real model** (including any Fluent API configuration, value converters, owned types, etc.) because we use the actual compiled `DbContext`, not a re-implemented schema.

## Status

MVP implemented: assembly loading, `DbContext` discovery, server-side connection registry,
schema discovery, safe Dynamic-LINQ query execution, and the MCP stdio tool surface are all
in place and tested (`dotnet test`). Structured logging is also in place; the optional
metrics/telemetry hooks are the only remaining open item — see
[DEVELOPMENT.md](./DEVELOPMENT.md) for the module-by-module breakdown and the [work
tracker](./docs/development/WORK-TRACKER.md) for outstanding work.

## Prior art / inspiration

Research turned up no ready-made server doing exactly this, but several related projects informed the approach:

- [ProjGraph.Mcp](https://github.com/HandyS11/ProjGraph) — reads EF Core `DbContext`/`ModelSnapshot` *source* to produce an ERD; closest match for schema discovery, but static-analysis based rather than loading a live assembly.
- [Elarion](https://github.com/swimmesberger/Elarion) — source-generates MCP tools directly on top of an app's own `DbContext`; a good reference for exposing EF Core over MCP, but requires building the tools into the app.
- [How to Expose an EF Core Database to an AI Agent via MCP](https://startdebugging.net/2026/05/how-to-expose-an-ef-core-database-to-an-ai-agent-via-mcp/) — blueprint article covering `IDbContextFactory`, allowlisted model inspection, read-only projections, pagination, and tenant scoping; the closest conceptual match to this project's goals.

## Getting started

### Prerequisites

- .NET SDK 10 (`net10.0`).
- A target .NET project that has an EF Core `DbContext` and has already been built (e.g.
  `dotnet build` producing `bin/Debug/net8.0/MyApp.dll` or similar — any TFM the target
  project builds for is fine; only the server itself targets `net10.0`).

### Build & test

```powershell
dotnet build dotnet-efcore-mcp.slnx
dotnet test dotnet-efcore-mcp.slnx
```

(Note: the solution file is `dotnet-efcore-mcp.slnx`, the newer XML-based solution
format — not a classic `.sln`. `dotnet build`/`dotnet test` from the repo root also
auto-discover it without naming it explicitly.)

### Configure connections (server-side only)

Connection strings are **never** read from the target project's own configuration and are
**never** accepted from an MCP client — only a logical connection *name* is ever passed
across the MCP boundary. Configure real connections on the server via `dotnet user-secrets`
(local/dev) and/or environment variables (any environment); the two sources are additive,
with environment variables overriding user-secrets for the same key, per the standard
`IConfiguration` provider order.

Each connection needs `ConnectionString`, plus optional `Provider` (one of `Sqlite`,
`SqlServer`, `PostgreSql`), `Environment` (`Development`, `Staging`, `Production`, or
`Unspecified`), `AccessMode` (`ReadOnly` — the default — or `ReadWrite`; see the note in
[`docs/development/connections.md`](./docs/development/connections.md) about its current
scope), and `CommandTimeoutSeconds` (defaults to 30).

`Provider` is optional because it's normally **inferred** from the EF Core provider package
referenced by the currently loaded target project assembly (e.g. referencing
`Microsoft.EntityFrameworkCore.SqlServer` infers `SqlServer`). Inference only ever looks at
the target assembly's compiled package references — never at the target project's own
configuration or connection strings. An explicit `Provider` always takes precedence over
inference, and is required if the target project references zero or more than one supported
EF Core provider package (inference then fails with an actionable error naming the
`Connections:<name>:Provider` key to set).

Keep the committed `appsettings.json` placeholder empty. For example, configure three named
environments with user-secrets, letting the provider be inferred:

```powershell
cd src/DotnetEfCoreMcp.Server
dotnet user-secrets init

dotnet user-secrets set "Connections:MyApp.Development:ConnectionString" "Server=...;Database=...;..."
dotnet user-secrets set "Connections:MyApp.Development:Environment" "Development"

dotnet user-secrets set "Connections:MyApp.Staging:ConnectionString" "Server=...;Database=...;..."
dotnet user-secrets set "Connections:MyApp.Staging:Environment" "Staging"

dotnet user-secrets set "Connections:MyApp.Production:ConnectionString" "Server=...;Database=...;..."
dotnet user-secrets set "Connections:MyApp.Production:Environment" "Production"
```

Or use environment variables (useful in containers/CI, and always takes precedence over
user-secrets for the same key):

```powershell
$env:DOTNETEFCOREMCP_Connections__MyApp.Development__ConnectionString = "Server=...;Database=...;..."
$env:DOTNETEFCOREMCP_Connections__MyApp.Development__Environment = "Development"
```

If a target project references more than one supported EF Core provider (or none), set
`Provider` explicitly for the affected connection(s):

```powershell
dotnet user-secrets set "Connections:MyApp.Development:Provider" "SqlServer"
```

The first non-production connection is active at startup. Use the `list_connections` MCP
tool to inspect redacted connection metadata and `swap_connection` to change the active
default used by `get_schema` and `run_query` when no connection name is supplied.

Production connections are always forced to `ReadOnly`, even if configured otherwise. They
are never selected automatically and `swap_connection` requires an explicit
`allowProduction: true` acknowledgement before activating one. A production-only registry
therefore starts without an active connection.

### Visual Studio Code setup

VS Code reads workspace MCP servers from `.vscode/mcp.json`. This example launches the server
from a sibling clone, passes the open workspace to automatic assembly discovery, and prompts for
the connection string without committing it:

```jsonc
{
  "servers": {
    "dotnet-efcore": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "C:\\repos\\dotnet-efcore-mcp\\src\\DotnetEfCoreMcp.Server"
      ],
      "cwd": "${workspaceFolder}",
      "env": {
        "DOTNETEFCOREMCP_WORKSPACEPATH": "${workspaceFolder}",
        "DOTNETEFCOREMCP_CONNECTIONS__Workspace__PROVIDER": "SqlServer",
        "DOTNETEFCOREMCP_CONNECTIONS__Workspace__CONNECTIONSTRING": "${input:efcore-connection-string}",
        "DOTNETEFCOREMCP_CONNECTIONS__Workspace__ACCESSMODE": "ReadOnly",
        "DOTNETEFCOREMCP_CONNECTIONS__Workspace__ENVIRONMENT": "Development"
      }
    }
  },
  "inputs": [
    {
      "id": "efcore-connection-string",
      "type": "promptString",
      "description": "EF Core database connection string",
      "password": true
    }
  ]
}
```

Change `PROVIDER` to `Sqlite` or `PostgreSql` when appropriate. On first start VS Code prompts
for the input and stores it securely; it is passed directly to the server process as configuration,
not through an MCP tool call. Use **MCP: List Servers** from the Command Palette to start, stop,
or restart this workspace server. See the official [VS Code MCP server configuration](https://code.visualstudio.com/docs/agent-customization/mcp-servers)
and [sensitive input variable reference](https://code.visualstudio.com/docs/agents/reference/mcp-configuration#_input-variables-for-sensitive-data).

When `WorkspacePath` is configured and `TargetAssemblyPath` is not, startup scans the workspace's
C# projects and automatically loads the best existing output. Assemblies whose metadata suggests
they contain a `DbContext`-derived type are preferred first; Debug builds are then preferred, with
the newest output and higher target framework breaking remaining ties. Dependency and `ref`
assemblies are excluded. If nothing has been built yet, the server still starts—build the target
project and restart the server, or use `list_assembly_candidates` followed by `load_assembly`.
By default, the candidate tool returns one preferred output per project, noting other available
builds in `otherBuildsOfThisProject`; pass `includeAllBuilds: true` to inspect every
configuration/TFM output or `pathFilter` to narrow a monorepo to projects whose path contains a
specific project or folder name.

> The VS Code Agent Host does not forward servers requiring interactive inputs to remote agents.
> This prompted configuration is intended for local VS Code chat; configure secrets in the remote
> environment separately for remote Agent Host use.

### Point the server at a target project

For clients other than VS Code, set `WorkspacePath` to a repository/workspace root to get the same
automatic discovery behavior. Alternatively, set `TargetAssemblyPath` in configuration (it takes
precedence over discovery and is loaded once at startup; failures are logged as a warning, not
fatal) — e.g. via `dotnet user-secrets set "TargetAssemblyPath" "C:\path\to\MyApp\bin\Debug\net8.0\MyApp.dll"` —
or call the `load_assembly` MCP tool at runtime with the same path. `load_assembly` can be called
again any time (e.g. after rebuilding the target project) to reload it.

By default, `load_assembly` accepts any absolute path (trusted local/dev usage is assumed —
the MCP client is presumed to run at the same trust level as whoever launched the server).
To restrict which directories may be loaded from, configure one or more allowed roots; any
path outside them is rejected before the assembly is touched:

```powershell
dotnet user-secrets set "AssemblyLoader:AllowedRoots:0" "C:\repos\MyApp\bin"
```

### Automatic reload after rebuild

Once an assembly has been loaded (via startup discovery, `TargetAssemblyPath`, or `load_assembly`),
the server watches its DLL on disk and automatically reloads it whenever the file changes — e.g.
when `dotnet build`/MSBuild finishes rebuilding the target project — so `load_assembly` normally
never needs to be called again after the first load. File-change events are debounced (MSBuild
writes the DLL more than once per build) and a reload attempt is retried a few times if the file is
still locked because a build is still in progress. If every retry fails (or the rebuilt assembly
fails to load, e.g. bad IL mid-write), the server logs a warning and keeps serving the previously
loaded assembly; call `load_assembly` manually once the issue is resolved. Disable this behavior
with:

```powershell
dotnet user-secrets set "AssemblyLoader:AutoReloadEnabled" "false"
```

### Using Git worktrees

The server, the target project, or both can live in a [Git worktree](https://git-scm.com/docs/git-worktree)
(e.g. `git worktree add ../myapp-feature`) instead of the primary checkout — `WorkspacePath`,
`TargetAssemblyPath`, and `load_assembly` all operate on plain absolute filesystem paths, so
worktrees need no special handling. Two things worth knowing:

- In VS Code, `${workspaceFolder}` resolves to whichever worktree is currently open, so the
  `mcp.json` example above works unchanged when the whole repo (server + target project) is
  checked out as a worktree — just open that worktree's folder.
- If the server and the target project live in **different** worktrees (or different repos
  entirely), point `WorkspacePath`/`TargetAssemblyPath` at the target project's worktree path
  explicitly rather than relying on `${workspaceFolder}`, since that variable only reflects the
  workspace VS Code currently has open, not the server's own working directory.

### Tune query execution limits

Two safety limits are configurable under `QueryExecution` (both have sane defaults, so this
section is optional):

- `MaxTake` (default `200`) — the hard cap on rows returned by a single query, regardless of
  the client-requested `take`.
- `MaxIncludedCollectionItems` (default `200`) — caps how many items are materialized per
  included collection navigation (e.g. `Include: ["Orders"]`), so a customer with 100,000
  orders can't blow up the response.

```powershell
dotnet user-secrets set "QueryExecution:MaxTake" "100"
dotnet user-secrets set "QueryExecution:DefaultTake" "50"
dotnet user-secrets set "QueryExecution:MaxQueryLength" "4000"
```

### Run the server

The server communicates over **stdio** (standard input/output carries the MCP JSON-RPC
protocol; all logging goes to standard error instead, so it never corrupts the protocol
stream):

```powershell
dotnet run --project src/DotnetEfCoreMcp.Server
```

Configure your MCP client (e.g. an agent host) to launch this as a stdio-based MCP server
process, or reference the same command from an `mcp.json`/equivalent client configuration.

### MCP tool contract

All tools are `[McpServerTool]`-attributed methods on a single class and operate against
exactly one currently-loaded target assembly at a time (see `load_assembly`).
Errors are surfaced as `ModelContextProtocol.McpException` with an actionable message
(no connection strings, no raw stack traces).

Successful tool payloads use [TOON](https://github.com/Cysharp/ToonEncoder) by default,
which reduces structural overhead for agent consumption. This affects only the text content
returned by each tool: the stdio transport remains MCP JSON-RPC. To retain the legacy,
indented JSON tool payloads, set `ToolOutput:Format` to `json` (for example,
`dotnet user-secrets set "ToolOutput:Format" "json"`).

| Tool | Parameters | Returns |
|---|---|---|
| `list_assembly_candidates` | `workspacePath: string`, `pathFilter?: string`, `includeAllBuilds?: bool` | `workspacePath` and DbContext-first, preference-ordered `candidates` (assembly path, project, configuration, target framework, write time, preferred flag). By default, one preferred candidate per project is returned with `otherBuildsOfThisProject`; set `includeAllBuilds` to list every configuration/TFM output. |
| `load_assembly` | `assemblyPath: string` | Loaded assembly path/time and discovered `DbContext` names, full names, and construction kinds |
| `list_contexts` | *(none)* | Current assembly path, stale flag, and discovered contexts |
| `get_schema` | `contextName: string`, `connectionName: string` | Entities with properties (CLR type, nullability, PK/FK/concurrency-token flags, column name/type), primary keys, foreign keys, navigations, owned-type/TPH-inheritance metadata |
| `get_entity_schema` | `entityName: string`, `contextName?: string` | Complete cached definition for one exact entity (same shape as `get_schema`'s `entities`). Cache-only: throws directing the caller to call `get_schema` first if nothing is cached yet for the resolved context. |
| `search_schema` | `contextName?: string`, `query: string`, `maxResults?: int` | Compact, case-insensitive substring matches (`entityName`, `entityNameMatched`, `matchingProperties`, `matchingRelationships`) across entity/property/relationship names, plus `totalMatchCount` and `truncated`. `maxResults` defaults to 10 and is capped at 25. Cache-only, same cache-miss behavior as `get_entity_schema`. |
| `run_query` | `contextName: string`, `query: string`, `connectionName?: string` | Root DbSet name, scalar-or-sequence result, effective sequence page size, safely projected rows, and a `hasMoreRows` continuation flag |
| `preview_query_sql` | `contextName: string`, `query: string`, `connectionName?: string`, `targetName?: string` | The provider-generated SQL for a `run_query`-style expression, obtained from the compiled, unexecuted `IQueryable` via `ToQueryString()`. Never opens a database connection, runs a command, or reads/writes rows, regardless of the configured `QueryExecution:Mode`. Rejects scalar/element results, already-materialized results, and non-translatable operators (e.g. `Zip`) with a message directing the caller to `run_query` instead |
| `run_sql_query` | `contextName: string`, `sql: string`, `connectionName?: string`, `parameters?: object[]` | Rows, row count, affected rows, maximum rows, and more-rows flag; disabled by default and restricted to development `ReadWrite` connections |
| `test_connection` | `contextName: string`, `connectionName?: string` | Redacted connection-health diagnostic: context name, resolved connection name, provider, environment, and a `healthy`/`failed`/`timedOut` status. Never returns query rows, schema, or a connection string, and never changes the active connection |
| `insert_entity` | `contextName: string`, `entity: string`, `values: object`, `connectionName?: string` | Inserted scalar values and actual affected rows; disabled by default and restricted to development `ReadWrite` connections |
| `update_entity` | `contextName: string`, `entity: string`, `key: object`, `values: object`, `concurrency?: object`, `connectionName?: string` | Updated scalar values and actual affected rows, or a stable not-found-or-concurrency-conflict result |
| `delete_entity` | `contextName: string`, `entity: string`, `key: object`, `concurrency?: object`, `connectionName?: string` | Actual affected rows, or a stable not-found-or-concurrency-conflict result |

`query` is Roslyn-compiled C# rooted at an exact, public `DbSet<T>` property on the selected
context, such as `Customers.Where(c => c.Age > 18).Select(c => new { c.Id, c.Name })`.
If the trimmed text parses as one complete expression, the server wraps it as `return <query>;` (this is the currently supported execution path). An optional single trailing `;` is accepted and stripped.
If the query uses a top-level `{ ... }` block or contains multiple statements, the server rejects it: statement mode is **not supported** by design due to access-policy enforcement constraints. Expression-mode queries (the currently supported path) allow the server to validate entity access before compilation. For full P0 #9 status, see the [development guide](docs/development/query-execution.md).

`IQueryable` results are materialized server-side and capped at 50 rows by default (up to a configured maximum of 200 rows, unless
reconfigured); no terminal `.ToList()`/`.FirstOrDefault()` call is required. Results are not
automatically ordered; add an explicit `OrderBy()` when using `Skip()`/`Take()` to ensure stable
pagination. Scalars are returned as scalars. Non-`IQueryable` results (for example a client-side
`Zip` after `AsEnumerable()` or an already-materialized `List<T>`) are also returned as scalars
rather than row-shaped query results.

Queries default to `QueryTrackingBehavior.NoTracking`. An explicit `.AsTracking()` can opt back
into tracking, but `SaveChanges()` remains blocked unless the server operator enables
`QueryExecution:AllowMutationsInRunQuery` and the selected connection is both non-production and
`ReadWrite`.

`run_sql_query` is an explicitly opt-in escape hatch for development diagnostics and migrations;
prefer the always-on `run_query` whenever its LINQPad-style C# contract is sufficient.
Enable raw SQL only in a local or development server configuration, then restart the MCP server;
it cannot be enabled per request or per agent session:

```powershell
dotnet user-secrets set "RawSqlExecution:Enabled" "true"
# Or for the current process:
$env:DOTNETEFCOREMCP_RAWSQLEXECUTION__ENABLED = "true"
```

Raw SQL positional values must use provider parameters named `@p0`, `@p1`, and so on (for
example, `SELECT * FROM Users WHERE Id = @p0` with `parameters: [42]`); values are passed as
ADO.NET parameters rather than interpolated. Result rows are materialized up to the configured
maximum (200 by default), but the command itself is not rewritten with a dialect-specific
`LIMIT` or `TOP`. The tool remains unavailable for production and `ReadOnly` connections even
when globally enabled. On an eligible development `ReadWrite` connection it can run mutating
SQL, including `DELETE`, `TRUNCATE`, or schema changes—enable it only where that risk is
acceptable.

`get_schema` and `run_query` both require `connectionName` even though `get_schema` never
queries the database — constructing the `DbContext` object at all (even for schema-only
purposes) requires a real connection string/provider to build its `DbContextOptions`.

`list_migrations` is always-on, read-only inspection (no DDL/DML, never mutates the target
database): it reports which migrations the assembly knows about, which are recorded as applied
in `__EFMigrationsHistory`, and which are pending. When the target database is unreachable,
`databaseExists` is `false`, every known migration is reported pending, and
`appliedStateAvailable` is `false` rather than presenting metadata as confirmed applied state.
It is rejected only for production connections.

`generate_migration_script` previews the SQL EF Core's `IMigrator.GenerateScript` would produce
between two migration IDs — it never executes the script, opens a transaction, or calls
`SaveChanges`. It is disabled by default; enable it (and restart the server, the same as
`RawSqlExecution:Enabled`) with:

```powershell
dotnet user-secrets set "Migrations:Enabled" "true"
# Or for the current process:
$env:DOTNETEFCOREMCP_MIGRATIONS__ENABLED = "true"
```

Even when enabled, it is rejected for production and `ReadOnly` connections. The returned script
is capped at `Migrations:MaxScriptLength` characters (100,000 by default) and truncated at a
best-effort statement boundary, with `truncated` set to `true` when that happens. Not every
provider supports idempotent scripts (for example, SQLite does not); retry with
`idempotent: false` if `generate_migration_script` reports that limitation.

## Security

Because this server can execute arbitrary queries against a real database on behalf of an agent, security is a first-class concern, not an afterthought. See the [Connection management](./docs/development/connections.md) and [Query execution](./docs/development/query-execution.md) module guides for the implemented safeguards (connection string storage, query allowlisting/read-only enforcement, result limits, auditing).

## Contributing

This is an early-stage personal/experimental project. Issues and PRs are welcome once the initial scaffolding lands.
