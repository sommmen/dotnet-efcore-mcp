# Repository guidance

## Overview

`dotnet-efcore-mcp` is an [MCP](https://modelcontextprotocol.io/) (Model
Context Protocol) server that lets an AI agent query an existing Entity
Framework Core application's database without writing app code. It loads a
compiled project's assembly, reflects over its `DbContext`(s) to discover the
EF Core model (entities, properties, keys, relationships), and exposes
schema discovery and LINQ-style querying (read-only by default) to an MCP
client. See [DEVELOPMENT.md](./DEVELOPMENT.md) for current feature status and
module guides.

## Commit conventions

Use Conventional Commits for every commit message and pull request title:

```
<type>(<scope>): <description>
```

- `type` — one of `feat`, `fix`, `chore`, `docs`, `refactor`, `test`, `build`,
  `ci`, `perf`, `style`
- `scope` — the module, package, or area the change touches
- `description` — a short, imperative summary of the change

Examples:

- `feat(auth): add refresh token rotation`
- `fix(api): handle null response from upstream service`
- `chore(deps): bump dependency versions`
