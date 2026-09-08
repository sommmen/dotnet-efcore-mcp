# dotnet-efcore-mcp

npm launcher for [dotnet-efcore-mcp](https://github.com/sommmen/dotnet-efcore-mcp) — an MCP
server that lets an AI agent query an existing Entity Framework Core application's database
without writing app code.

```bash
npx -y dotnet-efcore-mcp
```

This package contains no server code. It is a thin shim that ensures the
[`DotnetEfCoreMcp.Server`](https://github.com/sommmen/dotnet-efcore-mcp) .NET global tool is
installed at the matching version, then execs it. **The .NET 10 SDK must be on `PATH`.**

> The underlying package is currently published to **GitHub Packages only** (not yet on
> nuget.org). GitHub Packages does not support anonymous restore, so the first run needs an
> authenticated source: set `DOTNET_EFCORE_MCP_NUGET_SOURCE` to an authenticated feed URL (a
> GitHub PAT with `read:packages` scope works as the password half of a source URL, or configure
> credentials in your `NuGet.Config`), or pre-install the tool yourself — see below.

## MCP client config

```json
{
  "mcpServers": {
    "dotnet-efcore": {
      "type": "stdio",
      "command": "npx",
      "args": ["-y", "dotnet-efcore-mcp"]
    }
  }
}
```

For VS Code's `.vscode/mcp.json` (which uses a `servers` key instead of `mcpServers`) and the
full set of connection-string/environment-variable configuration options, see the
[repository README](https://github.com/sommmen/dotnet-efcore-mcp#visual-studio-code-setup).

If you already have the .NET SDK and prefer no npm indirection, install the tool directly:

```bash
dotnet tool install --global DotnetEfCoreMcp.Server --add-source https://nuget.pkg.github.com/sommmen/index.json
```

See the [full documentation](https://github.com/sommmen/dotnet-efcore-mcp) for connection
configuration, the MCP tool contract, and query execution details.

MIT licensed.
