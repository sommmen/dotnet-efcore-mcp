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

This project aims to fill that gap. See [development.md](./development.md) for the current feature status and roadmap.

## How it works (planned)

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

Early scaffolding stage — no code yet. See [development.md](./development.md) for the feature checklist.

## Prior art / inspiration

Research turned up no ready-made server doing exactly this, but several related projects informed the approach:

- [ProjGraph.Mcp](https://github.com/HandyS11/ProjGraph) — reads EF Core `DbContext`/`ModelSnapshot` *source* to produce an ERD; closest match for schema discovery, but static-analysis based rather than loading a live assembly.
- [Elarion](https://github.com/swimmesberger/Elarion) — source-generates MCP tools directly on top of an app's own `DbContext`; a good reference for exposing EF Core over MCP, but requires building the tools into the app.
- [How to Expose an EF Core Database to an AI Agent via MCP](https://startdebugging.net/2026/05/how-to-expose-an-ef-core-database-to-an-ai-agent-via-mcp/) — blueprint article covering `IDbContextFactory`, allowlisted model inspection, read-only projections, pagination, and tenant scoping; the closest conceptual match to this project's goals.

## Getting started

Not yet available — the project has not been scaffolded with code. This section will be filled in once the initial .NET solution exists.

## Security

Because this server can execute arbitrary queries against a real database on behalf of an agent, security is a first-class concern, not an afterthought. See the "Security & connection management" section of [development.md](./development.md) for the planned safeguards (connection string storage, query allowlisting/read-only enforcement, result limits, auditing).

## Contributing

This is an early-stage personal/experimental project. Issues and PRs are welcome once the initial scaffolding lands.
