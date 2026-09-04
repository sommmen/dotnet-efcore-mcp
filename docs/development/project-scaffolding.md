# Project scaffolding

[← Back to Development Guide](../../DEVELOPMENT.md)

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
- [x] Add CI workflow (build + test) if/when this repo gets a CI pipeline
  - Implemented in [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml): restores,
    builds (`Release`), and runs `dotnet test` against `dotnet-efcore-mcp.slnx` on push/PR
    to `main`, on `merge_group`, and via manual `workflow_dispatch`.
- [x] Keep .NET's invariant globalization mode **disabled** for the server
  (`<InvariantGlobalization>false</InvariantGlobalization>` in
  `src/DotnetEfCoreMcp.Server/DotnetEfCoreMcp.Server.csproj`)
  - Invariant mode is a common size/startup-time optimization for containerized .NET apps, but
    `Microsoft.Data.SqlClient.SqlConnection.Open()` throws `NotSupportedException: Globalization
    Invariant Mode is not supported` when it is enabled, which blocks every query against a SQL
    Server target - confirmed via a live SQL Server integration test. Because SQL Server is one
    of this project's explicitly supported providers (see
    [Connection management](./connections.md)), invariant mode cannot be enabled here.
  - Deployment implication: this server's runtime environment (including any container base
    image) must ship ICU data (the default `mcr.microsoft.com/dotnet/*` images already do; only
    slimmed/Alpine-without-ICU or explicitly invariant-mode images would need adjusting). No
    action is needed for a standard `dotnet publish`/framework-dependent or self-contained
    deployment on Windows or Linux, which include ICU by default.
  - `run_query`'s error formatting still recognizes the invariant-mode `NotSupportedException`
    message defensively (see `EfCoreMcpTools.FormatQueryError`) and returns an actionable "enable
    ICU/globalization support" hint if a caller's own deployment re-enables invariant mode despite
    this default, or overrides it via the `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT` environment
    variable.
