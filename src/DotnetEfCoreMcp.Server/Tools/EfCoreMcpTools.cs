using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Schema;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DotnetEfCoreMcp.Server.Tools;

/// <summary>The MCP tool surface for this server. All tools operate against a single, currently
/// loaded target assembly (see <see cref="LoadAssembly"/>) and always resolve connection strings
/// from the server-side <see cref="ConnectionRegistry"/> by logical name - an MCP client can never
/// supply a raw connection string.</summary>
[McpServerToolType]
public sealed class EfCoreMcpTools(
    AssemblyLoaderService assemblyLoader,
    AssemblyDiscoveryService assemblyDiscovery,
    ConnectionRegistry connectionRegistry,
    SchemaCache schemaCache,
    QueryExecutor queryExecutor,
    RawSqlExecutionOptions rawSqlExecutionOptions,
    SqlQueryExecutor sqlQueryExecutor,
    ILogger<EfCoreMcpTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool(Name = "list_assembly_candidates"), Description(
        "Discovers compiled project assemblies under a workspace, ordered by the recommended selection. " +
        "Debug outputs are preferred over other configurations and Release. Pass any returned assemblyPath to load_assembly to switch targets.")]
    public string ListAssemblyCandidates(
        [Description("Absolute path to the workspace or repository to inspect.")] string workspacePath)
    {
        try
        {
            var candidates = assemblyDiscovery.Discover(workspacePath);
            return JsonSerializer.Serialize(new
            {
                workspacePath = Path.GetFullPath(workspacePath),
                candidates = candidates.Select(candidate => new
                {
                    assemblyPath = candidate.AssemblyPath,
                    projectPath = candidate.ProjectPath,
                    configuration = candidate.Configuration,
                    targetFramework = candidate.TargetFramework,
                    lastWriteTimeUtc = candidate.LastWriteTimeUtc,
                    isPreferred = candidate.IsPreferred,
                }),
            }, JsonOptions);
        }
        catch (AssemblyDiscoveryException ex)
        {
            logger.LogWarning(ex, "Failed to discover target assemblies. WorkspacePath={WorkspacePath}", workspacePath);
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "load_assembly"), Description(
        "Loads (or reloads) a compiled target .NET project's assembly (its bin/<Configuration>/<TFM>/*.dll output) " +
        "into an isolated, collectible AssemblyLoadContext, replacing any previously loaded assembly. " +
        "Returns warnings when no DbContexts are found or when assembly types could not load. " +
        "Call this before list_contexts/get_schema/run_query, or again after rebuilding the target project.")]
    public string LoadAssembly(
        [Description("Absolute or relative path to the target project's compiled assembly DLL.")] string assemblyPath)
    {
        try
        {
            var handle = assemblyLoader.Load(assemblyPath);
            var scan = DbContextScanner.FindDbContextTypes(handle.Assembly);
            var warnings = BuildScanWarnings(scan);
            logger.LogInformation(
                "Loaded target assembly. Path={AssemblyPath} DbContextCount={DbContextCount}",
                handle.AssemblyPath, scan.Descriptors.Count);
            if (warnings.Count > 0)
            {
                logger.LogWarning(
                    "Assembly scan produced warnings. Path={AssemblyPath} Warnings={Warnings}",
                    handle.AssemblyPath, string.Join(" | ", warnings));
            }

            return JsonSerializer.Serialize(new
            {
                loadedAssemblyPath = handle.AssemblyPath,
                loadedAtUtc = handle.LoadedAtUtc,
                discoveredDbContexts = scan.Descriptors.Select(c => new { name = c.Name, fullName = c.FullName, constructionKind = c.ConstructionKind.ToString() }),
                warnings = warnings.Count > 0 ? warnings : null,
            }, JsonOptions);
        }
        catch (AssemblyLoadFailedException ex)
        {
            logger.LogWarning(ex, "Failed to load target assembly. Path={AssemblyPath}", assemblyPath);
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "list_contexts"), Description(
        "Lists the Microsoft.EntityFrameworkCore.DbContext-derived types discovered in the currently loaded target assembly, " +
        "including warnings when none are found or when assembly types could not load.")]
    public string ListContexts()
    {
        var handle = RequireLoadedAssembly();
        var scan = DbContextScanner.FindDbContextTypes(handle.Assembly);
        var warnings = BuildScanWarnings(scan);
        if (warnings.Count > 0)
        {
            logger.LogWarning(
                "Assembly scan produced warnings. Path={AssemblyPath} Warnings={Warnings}",
                handle.AssemblyPath, string.Join(" | ", warnings));
        }

        return JsonSerializer.Serialize(new
        {
            assemblyPath = handle.AssemblyPath,
            isStale = assemblyLoader.IsCurrentAssemblyStale(),
            contexts = scan.Descriptors.Select(c => new
            {
                name = c.Name,
                fullName = c.FullName,
                constructionKind = c.ConstructionKind.ToString(),
            }),
            warnings = warnings.Count > 0 ? warnings : null,
        }, JsonOptions);
    }

    [McpServerTool(Name = "get_schema"), Description(
        "Returns the EF Core model (entities, properties, keys, foreign keys, navigations) for a DbContext " +
        "in the currently loaded target assembly, as discovered via reflection over the real compiled model.")]
    public string GetSchema(
        [Description("CLR type name of the DbContext, as returned by list_contexts.")] string contextName,
        [Description("Logical connection name from the server's connection registry, used only to construct the context; no query is executed against the database to build the schema. If omitted, the currently active connection is used.")] string? connectionName = null)
    {
        var contextType = ResolveContextType(contextName);
        var entry = ResolveConnection(connectionName);

        logger.LogInformation("get_schema requested. Context={ContextName} Connection={ConnectionName}", contextName, connectionName);

        var schema = schemaCache.GetOrBuild(contextType, () =>
        {
            using var context = CreateContext(contextType, entry);
            return Schema.SchemaBuilder.Build(context);
        });

        return JsonSerializer.Serialize(schema, JsonOptions);
    }

    [McpServerTool(Name = "run_query"), Description(
        "Executes a safe, read-only, capped-row query against a single entity's DbSet on a DbContext in the " +
        "currently loaded target assembly, using a Dynamic LINQ `where`/`orderBy` expression with positional " +
        "parameters (@0, @1, ...). Always runs with AsNoTracking(); row count is always capped server-side " +
        "even if `take` is omitted or exceeds the server's configured maximum.")]
    public async Task<string> RunQuery(
        [Description("CLR type name of the DbContext, as returned by list_contexts.")] string contextName,
        [Description("CLR type name of the entity to query, as returned by get_schema.")] string entity,
        [Description("Logical connection name from the server's connection registry. If omitted, the currently active connection is used.")] string? connectionName = null,
        [Description("Optional Dynamic LINQ predicate, e.g. \"Age > 18 and Name.Contains(@0)\". Parameters are always passed positionally via `parameters`, never concatenated into this string.")] string? where = null,
        [Description("Positional parameters referenced from `where`/`orderBy` as @0, @1, ...")] object?[]? parameters = null,
        [Description("Optional Dynamic LINQ order-by expression, e.g. \"Age desc\".")] string? orderBy = null,
        [Description("Number of rows to skip.")] int? skip = null,
        [Description("Number of rows to return; capped server-side regardless of the value requested.")] int? take = null,
        [Description("Navigation property names to eager-load; each must be an actual navigation property on `entity` (validated against the model) or the request is rejected.")] string[]? include = null,
        CancellationToken cancellationToken = default)
    {
        var contextType = ResolveContextType(contextName);
        var entry = ResolveConnection(connectionName);

        using var context = CreateContext(contextType, entry);

        var request = new QueryRequest
        {
            Entity = entity,
            Where = where,
            Parameters = parameters,
            OrderBy = orderBy,
            Skip = skip,
            Take = take,
            Include = include,
        };

        try
        {
            var result = await queryExecutor.ExecuteAsync(context, request, entry.CommandTimeoutSeconds, cancellationToken);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (QueryExecutionException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "run_sql_query"), Description(
        "Executes a parameterized raw SQL command against a Development ReadWrite connection. This potentially " +
        "destructive tool is unavailable unless RawSqlExecution:Enabled is explicitly set to true on the server; " +
        "it is always rejected for Production and ReadOnly connections. Use @p0, @p1, ... placeholders for values " +
        "provided through parameters. Result rows are capped server-side.")]
    public async Task<string> RunSqlQuery(
        [Description("CLR type name of the DbContext, as returned by list_contexts.")] string contextName,
        [Description("Raw SQL command text. Use @p0, @p1, ... for values rather than embedding them in this string.")] string sql,
        [Description("Logical connection name from the server's connection registry. If omitted, the currently active connection is used.")] string? connectionName = null,
        [Description("Positional parameter values referenced by SQL placeholders @p0, @p1, ...")] object?[]? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (!rawSqlExecutionOptions.Enabled)
        {
            throw new McpException("Raw SQL execution is disabled. Enable RawSqlExecution:Enabled in server-side configuration to use this tool.");
        }

        var contextType = ResolveContextType(contextName);
        var entry = ResolveConnection(connectionName);
        if (entry.IsProduction)
        {
            throw new McpException("Raw SQL execution is not permitted for production connections.");
        }

        if (entry.AccessMode != ConnectionAccessMode.ReadWrite)
        {
            throw new McpException("Raw SQL execution requires a ReadWrite connection.");
        }

        using var context = CreateContext(contextType, entry);
        try
        {
            var result = await sqlQueryExecutor.ExecuteAsync(
                context,
                new SqlQueryRequest { Sql = sql, Parameters = parameters },
                entry.CommandTimeoutSeconds,
                cancellationToken);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (QueryExecutionException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "list_connections"), Description(
        "Lists the connections currently registered in the server's connection registry, including each " +
        "connection's database provider, access mode, environment (Development/Staging/Production), whether it " +
        "is a production connection (always read-only, swap-protected), and which one is currently active. " +
        "Connection strings are never exposed.")]
    public string ListConnections()
    {
        var infos = connectionRegistry.ListConnections();
        return JsonSerializer.Serialize(new
        {
            activeConnection = connectionRegistry.ActiveConnectionName,
            connections = infos.Select(i => new
            {
                name = i.Name,
                provider = i.Provider?.ToString() ?? "(inferred)",
                accessMode = i.AccessMode.ToString(),
                environment = i.Environment.ToString(),
                isProduction = i.IsProduction,
                isActive = i.IsActive,
            }),
        }, JsonOptions);
    }

    [McpServerTool(Name = "swap_connection"), Description(
        "Swaps the server's active connection to the named connection, changing which connection subsequent " +
        "get_schema/run_query calls default to when no explicit connectionName is supplied. For production " +
        "connections (environment = Production), this is refused unless allowProduction is set to true.")]
    public string SwapConnection(
        [Description("Logical connection name from the server's connection registry to make active.")] string connectionName,
        [Description("Set to true to allow making a production connection active (for intentionally running diagnostics/read-only queries against production).")] bool allowProduction = false)
    {
        try
        {
            connectionRegistry.SetActive(connectionName, allowProduction);
            return JsonSerializer.Serialize(new
            {
                activeConnection = connectionRegistry.ActiveConnectionName,
            }, JsonOptions);
        }
        catch (ProductionProtectedException ex)
        {
            logger.LogWarning(ex, "Refused swap to production connection. Connection={ConnectionName}", ex.ConnectionName);
            throw new McpException(ex.Message);
        }
        catch (UnknownConnectionException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    private LoadedAssemblyHandle RequireLoadedAssembly()
    {
        return assemblyLoader.Current
            ?? throw new McpException("No target assembly is loaded yet. Call load_assembly with the path to a compiled target project's DLL first.");
    }

    private Type ResolveContextType(string contextName)
    {
        var handle = RequireLoadedAssembly();
        var scan = DbContextScanner.FindDbContextTypes(handle.Assembly);
        var descriptor = scan.Descriptors
            .FirstOrDefault(c => string.Equals(c.Name, contextName, StringComparison.Ordinal));

        if (descriptor is null)
        {
            var known = scan.Descriptors.Select(c => c.Name);
            var message = $"No DbContext named '{contextName}' was found in the currently loaded assembly. Known contexts: {string.Join(", ", known)}.";
            if (scan.Descriptors.Count == 0 && scan.TypeLoadWarnings.Count > 0)
            {
                message += " " + string.Join(" ", scan.TypeLoadWarnings);
            }

            throw new McpException(message);
        }

        return descriptor.ClrType;
    }

    /// <summary>Turns a <see cref="DbContextScanResult"/>'s type-load diagnostics into
    /// client-facing warning strings, adding an explicit "zero DbContexts found" warning when the
    /// scan came back empty (regardless of whether that was caused by type-load failures) so the
    /// caller never has to infer the problem from an empty list alone.</summary>
    private static List<string> BuildScanWarnings(DbContextScanResult scan)
    {
        var warnings = new List<string>();

        if (scan.Descriptors.Count == 0)
        {
            warnings.Add(
                "No DbContext-derived types were found in the loaded assembly. If a DbContext is expected here, " +
                "check that the assembly path points at the project actually declaring it, and that all of its " +
                "runtime dependencies (including any ASP.NET Core shared framework or transitive NuGet packages) " +
                "are present in the output folder.");
        }

        warnings.AddRange(scan.TypeLoadWarnings);
        return warnings;
    }

    private ConnectionRegistryEntry ResolveConnection(string? connectionName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(connectionName))
            {
                var active = connectionRegistry.ActiveConnection;
                if (active is null)
                {
                    throw new McpException("No connection is active yet. Call swap_connection, or pass an explicit connectionName. (Connections available via list_connections.)");
                }
                return active;
            }

            return connectionRegistry.Get(connectionName);        }
        catch (UnknownConnectionException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    private static Microsoft.EntityFrameworkCore.DbContext CreateContext(Type contextType, ConnectionRegistryEntry entry)
    {
        var provider = ResolveEffectiveProvider(contextType, entry);
        try
        {
            return DbContextActivator.CreateInstance(contextType, entry, provider);
        }
        catch (DbContextActivationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    /// <summary>Resolves the provider to configure a context with: an explicit
    /// <see cref="ConnectionRegistryEntry.Provider"/> always wins, otherwise it is inferred from the
    /// EF Core provider package referenced by the loaded target assembly that declares
    /// <paramref name="contextType"/>.</summary>
    private static DatabaseProvider ResolveEffectiveProvider(Type contextType, ConnectionRegistryEntry entry)
    {
        if (entry.Provider is { } configured)
        {
            return configured;
        }

        if (!ProviderInference.TryInfer(contextType.Assembly, out var inferred, out var error))
        {
            throw new McpException($"Connection '{entry.Name}' has no configured provider. {error}");
        }

        return inferred;
    }
}
