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
- [x] Publish the server as a NuGet MCP server package (see
  [NuGet's MCP server packaging guidance](https://learn.microsoft.com/en-us/nuget/concepts/nuget-mcp))
  with automated CI/CD to GitHub Packages
  - `version.json` (repo root): [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning)
    config, `0.1-preview` base version, public releases restricted to `main`.
  - `src/Directory.Build.props`: shared packaging metadata (authors, repository/project URLs,
    `PackageLicenseExpression=MIT`, `PackageReadmeFile`) plus the `Nerdbank.GitVersioning` and
    `DotNet.ReproducibleBuilds` package references for every project under `src/`.
    `IsPackable` defaults to `false` here; `DotnetEfCoreMcp.Server.csproj` opts back in
    explicitly since it's the only project actually published (the QueryHost is bundled inside
    the server's own package, not published standalone). `tests/Directory.Build.props` also sets
    `IsPackable=false` so a solution-wide `dotnet pack` doesn't produce packages for test/fixture
    projects.
  - `LICENSE` (repo root): MIT license.
    - Judgment call made without user confirmation (user was unavailable to answer): MIT was
      chosen to match the license convention of the related
      [`dotnet-agent-surface`](https://github.com/sommmen/dotnet-agent-surface) project. Flagged
      here for the repository owner to confirm or change.
  - `src/DotnetEfCoreMcp.Server/DotnetEfCoreMcp.Server.csproj`: `PackAsTool=true`,
    `ToolCommandName=dotnet-efcore-mcp`, `PackageType=McpServer` (per the NuGet MCP guidance
    above), package metadata, and a `.mcp/server.json` MCP registry manifest packed at
    `/.mcp/server.json` (version placeholders substituted automatically by
    Nerdbank.GitVersioning at pack time, following the pattern used by
    [Azure/bicep's `Bicep.McpServer`](https://github.com/Azure/bicep/blob/main/src/Bicep.McpServer)).
    - Known limitation: the MCP registry's `server.json` schema currently only validates
      `registryType: nuget` packages against `registryBaseUrl: https://api.nuget.org/v3/index.json`
      (no GitHub Packages registry type exists in the schema as of this writing), so the manifest
      declares the nuget.org registry even though the package is not actually published there yet.
      Revisit if/when this package is published to nuget.org for real, or if the MCP registry
      schema gains GitHub Packages support.
  - The out-of-process query host (`DotnetEfCoreMcp.QueryHost`) is bundled inside the server's
    package (`tools/<tfm>/any/queryhost/`) via custom MSBuild targets
    (`PublishQueryHostForBundling` + `IncludeQueryHostInPackage`, hooked through
    `TargetsForTfmSpecificContentInPackage`) so the query host works out-of-the-box after
    `dotnet tool install`/`dnx`, without a separate build/deploy step. See
    [`QueryHostLocator`](../../src/DotnetEfCoreMcp.Server/Querying/QueryHostLocator.cs) and
    [Query execution](./query-execution.md) for the runtime auto-detection side of this.
    Verified end-to-end via a local `dotnet tool install --global --add-source` smoke test:
    the bundled query host DLL is present as a sibling of the server's own binaries under the
    installed tool's directory and executes correctly.
  - CI/CD: [`.github/workflows/publish.yml`](../../.github/workflows/publish.yml), adapted from
    [`dotnet-agent-surface`'s `publish.yml`](https://github.com/sommmen/dotnet-agent-surface/blob/main/.github/workflows/publish.yml).
    Builds, tests, and packs the solution, then pushes to **GitHub Packages** on every push to
    `main` (preview versions) and includes an opt-in NuGet.org publish path (gated on a
    non-prerelease GitHub Release or a manual `workflow_dispatch` input) that is currently a
    no-op until a `NUGET_USER` secret is configured — GitHub Packages is the only active
    publish channel for now, per this task's scope.
- [x] Publish a thin npm wrapper (`dotnet-efcore-mcp` on the public npm registry) so npm/npx-based
  MCP tooling — notably [`vercel-labs/skills`](https://github.com/vercel-labs/skills), which
  installs skills/servers via npm — can pull in the server without a user running
  `dotnet tool install` by hand. Modeled on
  [`MarcelRoozekrans/roslyn-codelens-mcp`](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp),
  a comparable .NET MCP server that ships both a NuGet/dnx tool and an npm shim.
  - [`npm/package.json`](../../npm/package.json), [`npm/bin/dotnet-efcore-mcp.js`](../../npm/bin/dotnet-efcore-mcp.js),
    [`npm/README.md`](../../npm/README.md): the wrapper contains **no server code**. The launcher
    checks for the .NET SDK, installs/updates the matching version of the
    `DotnetEfCoreMcp.Server` global tool on demand (resolved directly from `~/.dotnet/tools`
    since that directory isn't guaranteed to be on `PATH` in the same process that just
    installed into it), then execs straight into it with `stdio: "inherit"`.
    - Judgment call made without user confirmation (user was unavailable to answer): the
      unscoped package name `dotnet-efcore-mcp` was chosen (confirmed available on the public
      npm registry) to match `roslyn-codelens-mcp`'s unscoped convention and repo-name
      discoverability, over a scoped alternative like `@sommmen/dotnet-efcore-mcp`. Flagged
      here for the repository owner to confirm or change.
    - Known limitation carried over from the NuGet package still being GitHub-Packages-only
      (see above): GitHub Packages doesn't support anonymous restore, so `npx -y
      dotnet-efcore-mcp` does not work out-of-the-box without also configuring GitHub Packages
      credentials (a PAT with `read:packages` scope). The launcher exposes a
      `DOTNET_EFCORE_MCP_NUGET_SOURCE` environment variable override and fails with an
      actionable message pointing at it; this is documented as a prerequisite in
      `npm/README.md` rather than solved outright. The real fix is nuget.org publishing (see
      the existing `NUGET_USER`-gated path above), at which point the launcher's default
      source can drop back to the public feed with no extra auth step.
  - [`src/DotnetEfCoreMcp.Server/.mcp/server.json`](../../src/DotnetEfCoreMcp.Server/.mcp/server.json):
    gained a second `packages[]` entry (`registryType: npm`, `registryBaseUrl:
    https://registry.npmjs.org`, `identifier: dotnet-efcore-mcp`, `runtimeHint: npx`) alongside
    the existing nuget/dnx entry — the MCP registry schema supports multiple package entries
    per server, and both entries' `0.0.0-placeholder` versions are substituted uniformly by the
    same Nerdbank.GitVersioning pack-time step (no separate stamping needed for this file).
  - CI/CD: `.github/workflows/publish.yml` gained a version-stamping step (reads the version
    back out of the already-packed `DotnetEfCoreMcp.Server` nupkg filename and writes it into
    `npm/package.json`, since nbgv only versions .NET projects) plus an `npm publish
    --provenance` step using classic `NPM_TOKEN`-secret auth. Both are currently a no-op — like
    the NuGet.org path — until an `NPM_TOKEN` secret (an npm automation token with publish
    access for the `dotnet-efcore-mcp` package) is configured. npm Trusted Publishing/OIDC (no
    secret at all, the approach `roslyn-codelens-mcp` uses) was considered but not chosen here
    since it requires manually configuring a Trusted Publisher on the npmjs.org package
    settings page first — equally manual to set up as an `NPM_TOKEN`, so the simpler classic
    token was picked to keep the two dormant publish paths (NuGet.org, npm) consistent. Revisit
    if/when Trusted Publishing is preferred.
