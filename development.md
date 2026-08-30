# Development Roadmap

This document tracks the features needed to get `dotnet-efcore-mcp` from an empty repo to a usable MCP server. Check items off as they're implemented. Sub-bullets under an item are implementation notes, not separate tasks, unless they have their own checkbox.

## 0. Project scaffolding

- [ ] Create the .NET solution (`dotnet-efcore-mcp.sln`)
- [ ] Create the MCP server project (e.g. `src/DotnetEfCoreMcp.Server`)
- [ ] Choose and wire up an MCP server SDK/library (e.g. the official [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk)) with stdio transport as the initial target
- [ ] Add a `.editorconfig` / analyzer baseline consistent with the rest of the codebase
- [ ] Add a basic test project (e.g. `tests/DotnetEfCoreMcp.Server.Tests`) with the existing test runner wired up
- [ ] Add CI workflow (build + test) if/when this repo gets a CI pipeline

## 1. Assembly loading

- [ ] Load a target project's compiled output (`bin/Debug/<tfm>/*.dll`) given a file path
- [ ] Load the assembly in an isolated, **collectible** `AssemblyLoadContext` so it can be unloaded/reloaded (e.g. after a rebuild) without restarting the server
- [ ] Resolve the target assembly's dependencies (its own `.deps.json` / referenced DLLs alongside it), so it doesn't fail to load due to missing EF Core / provider assemblies
- [ ] Handle assembly load failures gracefully (missing file, wrong TFM/runtime mismatch, missing dependencies) with clear error messages surfaced to the MCP client
- [ ] Detect and handle stale/locked DLLs (e.g. project was rebuilt while the server was running)

## 2. DbContext discovery

- [ ] Scan a loaded assembly for types deriving from `Microsoft.EntityFrameworkCore.DbContext`
- [ ] Support assemblies with multiple `DbContext` types (list them, let the caller pick one by name)
- [ ] Handle `DbContext` types that require constructor arguments (e.g. `DbContextOptions<T>`) — construct via `DbContextOptionsBuilder` rather than assuming a parameterless constructor
- [ ] Support `DbContext` types configured via `OnConfiguring` (no options passed in) as a distinct case from ones requiring externally-supplied options
- [ ] Handle design-time factories (`IDesignTimeDbContextFactory<T>`) if present, as an alternative construction path

## 3. Connection management (security-sensitive)

- [ ] Define a "Connections" registry on the **server side** (not read from the target project's own config) mapping a logical connection name → provider + connection string
- [ ] Support at least one connection string per configured `DbContext`/environment (e.g. `MyApp.Context` → SQL Server connection string)
- [ ] Store connection strings securely at rest:
  - [ ] Never persist secrets in plain text in the repo/config committed to source control
  - [ ] Support loading from environment variables and/or OS-level secret stores (e.g. `dotnet user-secrets`, environment variables, or a mounted secrets file) as the primary mechanism
  - [ ] Redact connection strings from all logs, error messages, and MCP tool output
- [ ] Validate/allowlist which providers are supported initially (e.g. SQL Server, PostgreSQL, SQLite) and reject unknown providers explicitly
- [ ] Enforce that a given `DbContext` type can only ever be connected using connection strings from the server-side registry, never arbitrary strings supplied by the MCP client/agent
- [ ] Support per-connection access scoping (e.g. read-only vs. read-write) as a registry-level setting, independent of the database user's own permissions
- [ ] Fail closed: if no matching connection is configured for a requested context, refuse to connect rather than falling back to any default

## 4. Schema / model discovery

- [ ] Build the EF Core model for a resolved `DbContext` instance (`context.Model`)
- [ ] Expose entity types, their CLR properties, and mapped column names/types
- [ ] Expose primary keys, foreign keys, and navigation properties (relationships)
- [ ] Expose owned types and inheritance hierarchies (TPH/TPT/TPC) where present
- [ ] Serialize the discovered schema into a compact, agent-friendly format (e.g. JSON) suitable for an MCP tool response
- [ ] Cache the discovered schema per loaded assembly/context and invalidate it when the assembly is reloaded

## 5. Query execution

- [ ] Define the query input format the agent will send (e.g. a LINQ-like query DSL, a subset of expressions, or a safe query string translated to LINQ)
- [ ] Translate/execute the incoming query against the real `DbSet<T>` for the requested entity
- [ ] Enforce read-only execution by default (no `SaveChanges`, no tracked entities, `.AsNoTracking()`)
- [ ] Enforce a maximum result size / row limit and require pagination for larger result sets
- [ ] Enforce a query timeout (command timeout / cancellation token) to avoid runaway queries
- [ ] Reject or restrict unsafe query shapes (e.g. arbitrary raw SQL, unbounded `.Include()` graphs) unless explicitly allowlisted
- [ ] Serialize query results (including related/included entities) into a response format that avoids circular references
- [ ] Surface EF Core / provider exceptions as clear, sanitized error messages (no leaking connection strings or stack traces with sensitive info)

## 6. MCP tool surface

- [ ] `list_contexts` — list discovered `DbContext` types available from the currently loaded assembly
- [ ] `get_schema` — return the model/schema for a given context
- [ ] `run_query` — execute a query against a given context and entity
- [ ] `load_assembly` (or startup-only configuration) — point the server at a project's build output
- [ ] Decide whether assembly/connection configuration is done via server startup config only, or also exposed as MCP tools (weigh flexibility vs. attack surface)

## 7. Auditing & observability

- [ ] Log every executed query (context, entity, query shape, row count, duration) without logging secrets
- [ ] Add structured logging with configurable verbosity
- [ ] Add basic metrics/telemetry hooks (optional, later-stage)

## 8. Documentation

- [ ] Document how to configure a target project + connection string once the config format is decided
- [ ] Document the MCP tool contract (inputs/outputs) for each tool once implemented
- [ ] Update [README.md](./README.md)'s "Getting started" section once the initial solution exists
