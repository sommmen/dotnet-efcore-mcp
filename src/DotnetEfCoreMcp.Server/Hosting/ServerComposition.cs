using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.Migrations;
using DotnetEfCoreMcp.Server.Mutations;
using DotnetEfCoreMcp.Server.Onboarding;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Schema;
using DotnetEfCoreMcp.Server.Telemetry;
using DotnetEfCoreMcp.Server.Tools;
using DotnetEfCoreMcp.Server.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DotNetAgentSurface.Mcp;
using ModelContextProtocol.Protocol;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace DotnetEfCoreMcp.Server.Hosting;

/// <summary>Composes the server's services, MCP wiring and startup target preloading, so the
/// same registrations can be reused outside <c>Program.cs</c>.</summary>
public static class ServerComposition
{
    /// <summary>Registers configuration layering, logging, options and services.
    /// <paramref name="includeHostedServices"/> only controls the <see cref="IHostedService"/>
    /// registrations (assembly reload watcher, query host pool, update notification); the
    /// singletons behind them are always registered. <paramref name="overrideConfiguration"/> runs after
    /// the secrets and environment layers so a caller can pin values that must beat them (the CLI's
    /// explicit flags). <paramref name="cursorSigningKeyFactory"/> supplies a stable
    /// <c>run_query</c> cursor key when <c>QueryExecution:CursorSigningKey</c> is not configured.</summary>
    public static void AddEfCoreServices(
        HostApplicationBuilder builder,
        bool includeHostedServices = true,
        Action<ConfigurationManager>? overrideConfiguration = null,
        Func<IServiceProvider, string>? cursorSigningKeyFactory = null)
    {
        // Server-side secrets: .NET user-secrets for local/dev (always loaded, not just when
        // DOTNET_ENVIRONMENT=Development, so behavior is predictable regardless of how the server is
        // launched), then environment variables as an override layer on top - env vars naturally win in
        // IConfiguration's default provider ordering since they're added last. Use the
        // DOTNETEFCOREMCP_CONNECTIONS__<Name>__<Field> pattern, e.g.
        // DOTNETEFCOREMCP_CONNECTIONS__MyApp.Context__ConnectionString=... (double underscore is the
        // standard IConfiguration section-separator convention for environment variables).
        //
        // The directory is created first because AddUserSecrets silently roots its PhysicalFileProvider at
        // the *app base directory* when the secrets directory does not exist, which permanently blinds the
        // provider to that file: the secrets store could then never be re-read while the server runs, not
        // even by reload_connections (issue #121). This is exactly the fresh-install case - a user who
        // configures their first connection after starting the server.
        UserSecretsStore.EnsureDirectoryExists(typeof(ServerComposition).Assembly);
        builder.Configuration.AddUserSecrets(typeof(ServerComposition).Assembly, optional: true);
        builder.Configuration.AddEnvironmentVariables(prefix: "DOTNETEFCOREMCP_");
        overrideConfiguration?.Invoke(builder.Configuration);

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
        if (includeHostedServices)
        {
            builder.Services.AddHostedService<AssemblyReloadWatcher>();
        }

        builder.Services.AddSingleton<ConnectionRegistry>();
        builder.Services.AddSingleton<SchemaCache>();
        // Query safety limits (row/width caps, timeout margin) are configurable from the "QueryExecution"
        // section (env vars, user secrets, etc., same layering as "Connections" above) so they can be
        // tightened per-deployment without a code change, while still defaulting to safe values if unset.
        // OutOfProcessHostPath defaults to the query host bundled alongside this server (see
        // QueryHostLocator) so out-of-process/pooled/Auto modes work out-of-the-box for anyone who just
        // installs the packaged tool, without requiring manual configuration.
        var queryExecutionOptions = builder.Configuration.GetSection("QueryExecution").Get<QueryExecutionOptions>() ?? new QueryExecutionOptions();
        if (string.IsNullOrWhiteSpace(queryExecutionOptions.OutOfProcessHostPath))
        {
            queryExecutionOptions = queryExecutionOptions with { OutOfProcessHostPath = QueryHostLocator.TryGetBundledHostPath() };
        }
        if (cursorSigningKeyFactory is not null && string.IsNullOrWhiteSpace(builder.Configuration["QueryExecution:CursorSigningKey"]))
        {
            var configuredQueryExecutionOptions = queryExecutionOptions;
            builder.Services.AddSingleton(services => configuredQueryExecutionOptions with { CursorSigningKey = cursorSigningKeyFactory(services) });
        }
        else
        {
            builder.Services.AddSingleton(queryExecutionOptions);
        }
        builder.Services.AddSingleton(builder.Configuration.GetSection("QueryCompilation").Get<QueryCompilationOptions>() ?? new QueryCompilationOptions());
        builder.Services.AddSingleton<QueryCompiler>();
        builder.Services.AddSingleton<RoslynQueryExecutor>();
        builder.Services.AddSingleton<OutOfProcessRoslynQueryExecutor>();
        builder.Services.AddSingleton<QueryHostPool>();

        if (includeHostedServices)
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<QueryHostPool>());
        }

        builder.Services.AddSingleton<PooledOutOfProcessRoslynQueryExecutor>();
        builder.Services.AddSingleton(builder.Configuration.GetSection("RawSqlExecution").Get<RawSqlExecutionOptions>() ?? new RawSqlExecutionOptions());
        builder.Services.AddSingleton<SqlQueryExecutor>();
        // Reporting onboarding status over MCP is always allowed (it only reads); installing is opt-in,
        // because an installed hook adds a command the agent's CLI runs on every later tool call.
        builder.Services.AddSingleton(builder.Configuration.GetSection("Onboarding").Get<OnboardingOptions>() ?? new OnboardingOptions());
        builder.Services.AddSingleton(builder.Configuration.GetSection("Migrations").Get<MigrationsOptions>() ?? new MigrationsOptions());
        builder.Services.AddSingleton<MigrationInspector>();
        builder.Services.AddSingleton<SchemaComparer>();

        // Update check: notify only, never self-update. Registered as a hosted service so the lookup runs
        // in the background after startup rather than on the initialize path - a slow or unreachable feed
        // must never delay the MCP handshake.
        var updateCheckOptions = builder.Configuration.GetSection("UpdateCheck").Get<UpdateCheckOptions>() ?? new UpdateCheckOptions();
        builder.Services.AddSingleton(updateCheckOptions);
        builder.Services.AddSingleton<INuGetVersionLookup>(_ => new FlatContainerVersionLookup(
            // A short timeout on the client itself, in addition to UpdateChecker's overall budget: this is
            // a cosmetic lookup, and HashiCorp's framing applies - not critical enough to block on.
            new HttpClient { Timeout = TimeSpan.FromSeconds(3) },
            updateCheckOptions.Source));
        builder.Services.AddSingleton<UpdateCheckCache>();
        builder.Services.AddSingleton(_ => new UpdateCheckGate(updateCheckOptions.Enabled));
        builder.Services.AddSingleton<InstallMethodDetector>();
        builder.Services.AddSingleton<UpdateChecker>();
        builder.Services.AddSingleton<UpdateNotificationService>();

        if (includeHostedServices)
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<UpdateNotificationService>());
        }

        builder.Services.AddSingleton(builder.Configuration.GetSection("EntityMutations").Get<EntityMutationsOptions>() ?? new EntityMutationsOptions());
        builder.Services.AddSingleton<EntityMutationExecutor>();
        // Diagnostic metadata is an explicit development-only opt-in. ToolDiagnosticsOptions additionally
        // refuses to enable it under any other host environment.
        builder.Services.AddSingleton(_ => ToolDiagnosticsOptions.CreateEffective(
            builder.Configuration, builder.Environment.IsDevelopment()));

        // Telemetry is disabled by default (TelemetryOptions.Enabled == false), in which case no
        // OpenTelemetry SDK component is registered at all - not even a no-op exporter - so there is zero
        // added startup/runtime overhead unless a deployment explicitly opts in via configuration/env vars.
        var telemetryOptions = TelemetryOptions.CreateEffective(builder.Configuration, builder.Environment.IsDevelopment());
        builder.Services.AddSingleton(telemetryOptions);
        if (telemetryOptions.Enabled)
        {
            builder.Services.AddSingleton<IMcpMetrics, McpMetrics>();
            builder.Services
                .AddOpenTelemetry()
                .WithTracing(tracing =>
                {
                    // No trace exporter is configured (export is metrics-only, per scope). This pipeline
                    // exists solely to give the shared McpActivitySource a listener so its parent-based
                    // sampling decision is real: a root request is sampled at TelemetryOptions.SamplingRatio
                    // (a low, production-safe default), and any child activity inherits its parent's
                    // decision rather than being independently re-sampled. The resulting sampled/unsampled
                    // Activity also correlates the request ID with metric exemplars below.
                    tracing
                        .AddSource(McpActivitySource.Name)
                        .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(telemetryOptions.SamplingRatio)));
                })
                .WithMetrics(metrics =>
                {
                    metrics
                        .AddMeter(McpMetrics.MeterName)
                        .SetExemplarFilter(ExemplarFilterType.TraceBased)
                        .AddOtlpExporter((exporterOptions, readerOptions) =>
                        {
                            if (!string.IsNullOrWhiteSpace(telemetryOptions.OtlpEndpoint))
                            {
                                exporterOptions.Endpoint = new Uri(telemetryOptions.OtlpEndpoint);
                            }

                            exporterOptions.Protocol = OtlpExportProtocol.HttpProtobuf;
                            exporterOptions.TimeoutMilliseconds = telemetryOptions.ExportTimeoutMilliseconds;

                            // Periodic export is inherently asynchronous and off the request path: instruments
                            // aggregate in-process between cycles, and a slow/unreachable collector only delays
                            // (or, past the timeout above, drops) one export cycle rather than blocking or
                            // failing any MCP tool request.
                            readerOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = telemetryOptions.ExportIntervalMilliseconds;
                            readerOptions.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds = telemetryOptions.ExportTimeoutMilliseconds;
                        });
                });
        }
        else
        {
            builder.Services.AddSingleton<IMcpMetrics>(NullMcpMetrics.Instance);
        }

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
    }

    /// <summary>Registers the MCP server with its stdio transport and tool set.</summary>
    public static void AddEfCoreMcpServer(HostApplicationBuilder builder) => AddEfCoreMcpServer(builder.Services);

    /// <inheritdoc cref="AddEfCoreMcpServer(HostApplicationBuilder)"/>
    public static void AddEfCoreMcpServer(IServiceCollection services)
    {
        services
            .AddMcpServer(options =>
            {
                options.ServerInstructions =
                    """
                    Use dotnet-efcore-mcp for running LINQ queries and schema discovery against the
                    target application's EF Core model, instead of guessing at entity shapes or writing
                    one-off inspection code. When adding or changing LINQ queries, always validate them
                    using dotnet-efcore-mcp at the end of the session.

                    This server also ships optional agent integrations that are off until installed:
                    lifecycle hooks that re-deliver the instruction above at the moment a LINQ query is
                    written, and a feedback skill for filing a usable issue about this server without
                    leaking private code. Call the read-only `onboard` tool to see what is available and
                    what is already set up, and mention it if the user is hitting rough edges with this
                    server. Never install anything without the user asking for it.

                    The `onboard` tool also reports this server's version and whether a newer one has been
                    published. If it reports an update, pass the notice along to the user; do not run the
                    upgrade yourself.
                    """;
            })
            .WithStdioServerTransport()
            .WithOperations(
                catalog => catalog.AddMcpServerTools(
                    typeof(EfCoreMcpTools).Assembly,
                    targetFactory: null,
                    options =>
                    {
                        options.SafetyMapping = McpToolSafetyMapping.None;
                        options.ResolveTargetsFromServices = true;
                    }));

        // WithStdioServerTransport() above wires up the single-session hosted service that actually runs
        // the server; this replaces only the ITransport it resolves. The SDK's stock stream transport
        // disconnects the moment stdin reports end-of-stream and then silently drops every message sent
        // afterwards, so a client that writes a request and immediately closes stdin - a scripted or
        // short-lived session - never receives the response it is waiting for. See
        // FlushOnDisconnectStdioTransport.
        services.AddSingleton<ITransport>(provider => new FlushOnDisconnectStdioTransport(
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            "DotnetEfCoreMcp.Server",
            provider.GetService<ILoggerFactory>()));
    }

    /// <summary>Loads the configured target assemblies after the host is built. With
    /// <paramref name="throwOnConfiguredAssemblyFailure"/> a <c>TargetAssemblyPath</c> that cannot be
    /// loaded throws instead of only logging, so a one-shot caller can fail fast.</summary>
    public static void PreloadTargets(IServiceProvider services, IConfiguration configuration, ILogger logger, bool throwOnConfiguredAssemblyFailure = false)
    {
        // Optional startup convenience: if a target assembly path is configured, load it immediately so
        // list_contexts/get_schema/run_query work without a separate load_assembly call first. This is
        // purely a convenience - load_assembly remains available to (re)point the server at a different
        // build without restarting the process.

        // ConnectionRegistry never throws from its constructor (see its ConfigurationError doc comment) so
        // that a malformed "Connections" section surfaces through each tool call's own error handling
        // instead of a generic MCP-framework invocation failure. Resolving it here, eagerly, at startup -
        // and logging its failure loudly if present - still gives an operator a fail-fast signal in the
        // server's own logs before any MCP client ever makes a call, without preventing the tool-call-time
        // diagnostics from working.
        var connectionRegistry = services.GetRequiredService<ConnectionRegistry>();
        if (connectionRegistry.ConfigurationError is { } configurationError)
        {
            logger.LogError(
                configurationError,
                "Connections configuration is invalid; every MCP tool call will fail until this is fixed: {Message}",
                configurationError.Message);
        }

        var configuredAssemblyPath = configuration["TargetAssemblyPath"];
        var workspacePath = configuration["WorkspacePath"];

        if (string.IsNullOrWhiteSpace(configuredAssemblyPath) && !string.IsNullOrWhiteSpace(workspacePath))
        {
            try
            {
                var discovered = services.GetRequiredService<AssemblyDiscoveryService>()
                    .Discover(workspacePath);
                configuredAssemblyPath = discovered.Count > 0 ? discovered[0].AssemblyPath : null;

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
            var loader = services.GetRequiredService<AssemblyLoaderService>();
            try
            {
                loader.Load(configuredAssemblyPath);
                logger.LogInformation("Loaded target assembly from configured TargetAssemblyPath: {Path}", configuredAssemblyPath);
            }
            catch (AssemblyLoadFailedException) when (throwOnConfiguredAssemblyFailure)
            {
                throw;
            }
            catch (AssemblyLoadFailedException ex)
            {
                logger.LogWarning(ex, "Failed to load configured TargetAssemblyPath '{Path}' at startup; use the load_assembly tool once the issue is resolved.", configuredAssemblyPath);
            }
        }

        // Optional startup convenience: seed any additional named targets configured under
        // "AssemblyLoader:Targets" (see AssemblyLoaderOptions.Targets), so multiple compiled assemblies can
        // be available for `targetName`-qualified tool calls immediately without separate load_assembly
        // calls. Purely additive - existing single-target callers that never configure `Targets` see no
        // change.
        var assemblyLoaderOptions = services.GetRequiredService<AssemblyLoaderOptions>();
        foreach (var (targetName, targetOptions) in assemblyLoaderOptions.Targets)
        {
            if (string.IsNullOrWhiteSpace(targetOptions.Path))
            {
                logger.LogWarning("Skipping AssemblyLoader:Targets entry '{TargetName}': no Path configured.", targetName);
                continue;
            }

            try
            {
                var namedLoader = services.GetRequiredService<AssemblyLoaderService>();
                namedLoader.Load(targetOptions.Path, targetName, targetOptions);
                logger.LogInformation("Loaded target assembly '{TargetName}' from configured AssemblyLoader:Targets entry: {Path}", targetName, targetOptions.Path);
            }
            catch (AssemblyLoadFailedException ex)
            {
                logger.LogWarning(ex, "Failed to load configured AssemblyLoader:Targets entry '{TargetName}' at startup ('{Path}'); use the load_assembly tool once the issue is resolved.", targetName, targetOptions.Path);
            }
        }

    }
}
