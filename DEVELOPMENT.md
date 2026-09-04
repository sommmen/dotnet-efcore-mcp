# Development guide

This is the entry point for developing `dotnet-efcore-mcp`. For what the project does and
how to run it, see [README.md](./README.md); this guide covers the internal module
breakdown, implementation status, and where to track outstanding work.

## Solution layout

- [`src/DotnetEfCoreMcp.Server`](./src/DotnetEfCoreMcp.Server) - the MCP server itself,
  organized by module (see slices below).
- [`tests/DotnetEfCoreMcp.Server.Tests`](./tests/DotnetEfCoreMcp.Server.Tests) - xUnit tests,
  mirroring the server's module folders.
- [`tests/Fixtures/SampleApp`](./tests/Fixtures/SampleApp) - a small EF Core app built as
  part of the test run so tests can load its real compiled DLL.
- Build/test: `dotnet build dotnet-efcore-mcp.slnx` / `dotnet test dotnet-efcore-mcp.slnx`
  (auto-discovered from the repo root; see [README.md](./README.md#build--test)).

## Feature and module guides

Each module has its own implementation checklist, design decisions, and notes:

| Module | Description |
|---|---|
| [Project scaffolding](./docs/development/project-scaffolding.md) | Solution/project setup, analyzers, test project, CI. |
| [Assembly loading](./docs/development/assembly-loading.md) | Loading a target project's compiled output into an isolated, collectible `AssemblyLoadContext`. |
| [Multi-target assembly registry](./docs/development/assembly-registry.md) | Planned P2 #15: named registry for holding several loaded target assemblies at once. |
| [`DbContext` discovery](./docs/development/dbcontext-discovery.md) | Finding and constructing `DbContext` types in a loaded assembly. |
| [Connection management](./docs/development/connections.md) | Server-side connection registry, secret storage, provider allowlisting (security-sensitive). |
| [Schema / model discovery](./docs/development/schema-discovery.md) | Building and caching an agent-friendly serialization of the EF Core model. |
| [Query execution](./docs/development/query-execution.md) | Safe Dynamic-LINQ query translation, read-only enforcement, result limits. |
| [Out-of-process query host pooling](./docs/development/query-execution-host-pooling.md) | Investigation findings on out-of-process `run_query` latency (dominated by per-query Roslyn compilation, not process startup) and a proposed bounded worker-pool design; not yet implemented. |
| [Roslyn-compiled `UserQuery`](./docs/development/roslyn-user-query.md) | In-progress rewrite: LINQPad-style `UserQuery : TDbContext` compiled with Roslyn. Implemented and covered by tests behind an opt-in `Engine` toggle; not yet the default, and `System.Linq.Dynamic.Core` has not yet been removed. |
| [MCP tool surface](./docs/development/mcp-tools.md) | The `list_contexts` / `get_schema` / `run_query` / `run_sql_query` / `load_assembly` tools and their exposure decisions. |
| [Migration inspection](./docs/development/migrations.md) | Planned P1 #11: structured `list_migrations`/`generate_migration_script` tooling. |
| [Structured mutations](./docs/development/mutations.md) | Planned P1 #12: gated single-entity insert, update, and delete tooling. |
| [Visual Studio Code integration](./docs/development/vscode-integration.md) | Workspace assembly discovery, `.vscode/mcp.json` setup, and `list_assembly_candidates`. |
| [Auditing & observability](./docs/development/observability.md) | Query logging and structured logging configuration. |

## Ongoing work

Outstanding/in-progress items across all modules are tracked in a single place:

**[Work tracker](./docs/development/WORK-TRACKER.md)**

Keep that document (not this hub) up to date as work starts, progresses, and completes.

## Security

Because this server can execute arbitrary queries against a real database on behalf of an
agent, security is a first-class concern. See [Connection
management](./docs/development/connections.md) for connection string storage and provider
allowlisting, and [Query execution](./docs/development/query-execution.md) for read-only
enforcement, result limits, and query-shape restrictions.
