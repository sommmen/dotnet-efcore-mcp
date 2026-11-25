# dotnet-efcore-mcp

npm launcher for [dotnet-efcore-mcp](https://github.com/sommmen/dotnet-efcore-mcp) — an MCP
server that lets an AI agent query an existing Entity Framework Core application's database
without writing app code.

```bash
npx -y dotnet-efcore-mcp
```

The same command also runs the plain CLI, with no MCP client needed: `npx -y dotnet-efcore-mcp cli --help`,
`npx -y dotnet-efcore-mcp cli run-query --assembly ./bin/Debug/net10.0/MyApp.dll ...`. See the
[Choose MCP or CLI](https://github.com/sommmen/dotnet-efcore-mcp#choose-mcp-or-cli) section of the repository README.

This package contains no server code. It is a thin shim that ensures the
[`DotnetEfCoreMcp.Server`](https://github.com/sommmen/dotnet-efcore-mcp) .NET global tool is
installed at the matching version, then execs it. **The .NET 10 SDK must be on `PATH`.**

The underlying .NET tool is installed from NuGet.org by default. To use a different package
source, set `DOTNET_EFCORE_MCP_NUGET_SOURCE` to its feed URL or configure it in your
`NuGet.Config`.

Preview builds are also published to GitHub Packages as `@sommmen/dotnet-efcore-mcp` for testing;
see [Testing the npm wrapper from GitHub Packages](https://github.com/sommmen/dotnet-efcore-mcp#testing-the-npm-wrapper-from-github-packages).

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
