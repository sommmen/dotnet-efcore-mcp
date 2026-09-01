# MCP tool surface

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: [`src/DotnetEfCoreMcp.Server/Tools/EfCoreMcpTools.cs`](../../src/DotnetEfCoreMcp.Server/Tools/EfCoreMcpTools.cs)

See the [README "MCP tool contract"](../../README.md#mcp-tool-contract) section for the
per-tool parameter/response reference; this page tracks the surface's design decisions.

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
- [x] Format successful tool payloads for agent-efficient consumption without changing MCP transport framing
  - Successful tool text content defaults to [TOON](https://github.com/Cysharp/ToonEncoder),
    while the stdio stream remains JSON-RPC and errors retain MCP-native behavior.
  - `IToolResultFormatter` keeps the tool surface independent of the encoding library.
    `ToonToolResultFormatter` is the default implementation and `JsonToolResultFormatter`
    preserves the former indented JSON payload for `ToolOutput:Format=json` compatibility.
