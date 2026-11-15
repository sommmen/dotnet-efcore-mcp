using DotnetEfCoreMcp.Server.Cli;

// `hooks`, `onboard` and `version` are host verbs that run before any host is built (including the
// short-lived processes the installed agent hooks invoke on every tool call). `cli` runs one tool
// operation as a plain command. Launching with no arguments, or with only configuration arguments,
// starts the stdio MCP server exactly as before.
return await EfCoreSurface.RunAsync(args);
