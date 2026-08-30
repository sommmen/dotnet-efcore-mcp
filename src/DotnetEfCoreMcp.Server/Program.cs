using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
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

builder.Services.AddSingleton<AssemblyLoaderService>();
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<SchemaCache>();
builder.Services.AddSingleton(new QueryExecutionOptions());
builder.Services.AddSingleton<QueryExecutor>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<EfCoreMcpTools>();

var host = builder.Build();

// Optional startup convenience: if a target assembly path is configured, load it immediately so
// list_contexts/get_schema/run_query work without a separate load_assembly call first. This is
// purely a convenience - load_assembly remains available to (re)point the server at a different
// build without restarting the process.
var configuredAssemblyPath = builder.Configuration["TargetAssemblyPath"];
if (!string.IsNullOrWhiteSpace(configuredAssemblyPath))
{
    var loader = host.Services.GetRequiredService<AssemblyLoaderService>();
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
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
