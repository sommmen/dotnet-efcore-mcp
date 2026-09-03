# Visual Studio Code integration

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: [`src/DotnetEfCoreMcp.Server/AssemblyLoading/AssemblyDiscoveryService.cs`](../../src/DotnetEfCoreMcp.Server/AssemblyLoading/AssemblyDiscoveryService.cs) ·
Tests: [`tests/DotnetEfCoreMcp.Server.Tests/AssemblyLoading`](../../tests/DotnetEfCoreMcp.Server.Tests/AssemblyLoading)

See also the [README "Visual Studio Code setup"](../../README.md#visual-studio-code-setup) section
for user-facing `.vscode/mcp.json` configuration instructions.

- [x] Document a first-class `.vscode/mcp.json` setup using the stdio transport and `${workspaceFolder}`
- [x] Prompt for connection strings with a password-masked `${input:...}` variable so secrets are not committed
- [x] Discover C# project output assemblies beneath a configured `WorkspacePath`
  - Candidate discovery matches each `.csproj` output name, including explicit `AssemblyName`, and excludes dependency, `ref`, and `refint` DLLs.
  - Ranking puts assemblies whose metadata suggests a `DbContext`-derived type first, then prefers Debug over custom configurations over Release, then newest output and highest target framework.
  - `list_assembly_candidates` returns one preferred build per project by default, with `otherBuildsOfThisProject` recording hidden variants. Agents can pass `includeAllBuilds: true` to inspect every configuration/TFM output or `pathFilter` to restrict candidates by a case-insensitive project-path substring.
- [x] Automatically load the preferred candidate at startup when `TargetAssemblyPath` is unset
  - An explicit target remains the highest-priority override; no candidates or discovery failures are non-fatal.
- [x] Expose `list_assembly_candidates` so agents can inspect alternatives and switch with `load_assembly`
- [x] Cover ranking, filtering, custom assembly names, empty output, and invalid workspace behavior with focused tests
