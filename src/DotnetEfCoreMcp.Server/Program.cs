using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Schema;
using DotnetEfCoreMcp.Server.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Server-side secrets: .NET user-secrets for local/dev (always loaded, not just when
// DOTNET_ENVIRONMENT=Development, so behavior is predictable regardless of how the server is
// launched), then environment variables as an override layer on top - env vars naturally win in
// IConfiguration's default provider ordering since they're added last. Use the
// DOTNETEFCOREMCP_CONNECTIONS__<Name>__<Field> pattern, e.g.
// DOTNETEFCOREMCP_CONNECTIONS__MyApp.Context__ConnectionString=... (double underscore is the
// standard IConfiguration section-separator convention for environment variables).
builder.Configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);
builder.Configuration.AddEnvironmentVariables(prefix: "DOTNETEFCOREMCP_");

// stdio is the MCP transport, so stdout is reserved for the JSON-RPC stream - all logs must go to
// stderr.
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// `load_assembly` accepts an arbitrary path from the MCP client and loads it into this process, so
// `AssemblyLoader:AllowedRoots` (unset by default, i.e. unrestricted - see AssemblyLoaderOptions)
// is the primary control for constraining that surface in less-trusted deployments.
builder.Services.AddSingleton(builder.Configuration.GetSection("AssemblyLoader").Get<AssemblyLoaderOptions>() ?? new AssemblyLoaderOptions());
builder.Services.AddSingleton<AssemblyDiscoveryService>();
builder.Services.AddSingleton<AssemblyLoaderService>();
builder.Services.AddHostedService<AssemblyReloadWatcher>();
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<SchemaCache>();
// Query safety limits (row/width caps, timeout margin) are configurable from the "QueryExecution"
// section (env vars, user secrets, etc., same layering as "Connections" above) so they can be
// tightened per-deployment without a code change, while still defaulting to safe values if unset.
builder.Services.AddSingleton(builder.Configuration.GetSection("QueryExecution").Get<QueryExecutionOptions>() ?? new QueryExecutionOptions());
builder.Services.AddSingleton<QueryExecutor>();
builder.Services.AddSingleton(builder.Configuration.GetSection("QueryCompilation").Get<QueryCompilationOptions>() ?? new QueryCompilationOptions());
builder.Services.AddSingleton<QueryCompiler>();
builder.Services.AddSingleton<RoslynQueryExecutor>();
builder.Services.AddSingleton(builder.Configuration.GetSection("RawSqlExecution").Get<RawSqlExecutionOptions>() ?? new RawSqlExecutionOptions());
builder.Services.AddSingleton<SqlQueryExecutor>();

var configuredToolOutputFormat = builder.Configuration["ToolOutput:Format"];
var toolResultFormat = string.IsNullOrWhiteSpace(configuredToolOutputFormat)
    ? ToolResultFormat.Toon
    : Enum.TryParse<ToolResultFormat>(configuredToolOutputFormat, ignoreCase: true, out var parsedToolResultFormat)
        && Enum.IsDefined(parsedToolResultFormat)
        ? parsedToolResultFormat
        : throw new InvalidOperationException($"Unsupported ToolOutput:Format value '{configuredToolOutputFormat}'. Use 'toon' or 'json'.");

builder.Services.AddSingleton<IToolResultFormatter>(_ => toolResultFormat switch
{
    ToolResultFormat.Toon => new ToonToolResultFormatter(),
    ToolResultFormat.Json => new JsonToolResultFormatter(),
    _ => throw new InvalidOperationException($"Unsupported tool result format '{toolResultFormat}'."),
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<EfCoreMcpTools>();

var host = builder.Build();

// Optional startup convenience: if a target assembly path is configured, load it immediately so
// list_contexts/get_schema/run_query work without a separate load_assembly call first. This is
// purely a convenience - load_assembly remains available to (re)point the server at a different
// build without restarting the process.
var logger = host.Services.GetRequiredService<ILogger<Program>>();
var configuredAssemblyPath = builder.Configuration["TargetAssemblyPath"];
var workspacePath = builder.Configuration["WorkspacePath"];

if (string.IsNullOrWhiteSpace(configuredAssemblyPath) && !string.IsNullOrWhiteSpace(workspacePath))
{
    try
    {
        configuredAssemblyPath = host.Services.GetRequiredService<AssemblyDiscoveryService>()
            .Discover(workspacePath)
            .FirstOrDefault()?.AssemblyPath;

        if (configuredAssemblyPath is null)
        {
            logger.LogInformation(
                "No target assemblies found under workspace {WorkspacePath}. Build a project or use load_assembly explicitly.",
                Path.GetFullPath(workspacePath));
        }
        else
        {
            logger.LogInformation(
                "Automatically selected target assembly {AssemblyPath} from workspace {WorkspacePath}.",
                configuredAssemblyPath, Path.GetFullPath(workspacePath));
        }
    }
    catch (AssemblyDiscoveryException ex)
    {
        logger.LogWarning(ex, "Could not discover target assemblies under workspace {WorkspacePath}.", workspacePath);
    }
}

if (!string.IsNullOrWhiteSpace(configuredAssemblyPath))
{
    var loader = host.Services.GetRequiredService<AssemblyLoaderService>();
    try
    {
        loader.Load(configuredAssemblyPath);
        logger.LogInformation("Loaded target assembly from configured TargetAssemblyPath: {Path}", configuredAssemblyPath);
    }
    catch (AssemblyLoadFailedException ex)
    {
        logger.LogWarning(ex, "Failed to load configured TargetAssemblyPath '{Path}' at startup; use the load_assembly tool once the issue is resolved.", configuredAssemblyPath);
    }
}

await host.RunAsync();
