using System.ComponentModel;
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
    RoslynQueryExecutor roslynQueryExecutor,
    QueryExecutionOptions queryExecutionOptions,
    RawSqlExecutionOptions rawSqlExecutionOptions,
    SqlQueryExecutor sqlQueryExecutor,
    IToolResultFormatter resultFormatter,
    ToolDiagnosticsOptions toolDiagnosticsOptions,
    ILogger<EfCoreMcpTools> logger)
{

    [McpServerTool(Name = "list_assembly_candidates"), Description(
        "Discovers compiled project assemblies under a workspace, ordered by the recommended selection: " +
        "assemblies whose metadata suggests they contain a DbContext-derived type are ranked first, then " +
        "Debug outputs are preferred over other configurations and Release, then newest/highest-TFM as " +
        "tie-breakers. Pass any returned assemblyPath to load_assembly to switch targets. In monorepos with " +
        "many projects/build configurations, results are grouped per project by default (one representative " +
        "candidate per project, with an otherBuildsOfThisProject count) - set includeAllBuilds to true to see " +
        "every Configuration/TFM combination individually. Use pathFilter (a case-insensitive substring match " +
        "against each candidate's projectPath) to narrow results to a specific area of the workspace, e.g. " +
        "pathFilter: \"MyApp.Data\" instead of grepping the full output.")]
    public string ListAssemblyCandidates(
        [Description("Absolute path to the workspace or repository to inspect.")] string workspacePath,
        [Description("Optional case-insensitive substring to filter candidates by their projectPath (e.g. a project or folder name). Omit to return all projects.")] string? pathFilter = null,
        [Description("When false (default), only one representative candidate per project is returned (the preferred build, or the best match if pathFilter narrows to that project), with otherBuildsOfThisProject noting how many other Configuration/TFM builds exist. Set to true to list every build of every project individually.")] bool includeAllBuilds = false)
        => Execute("list_assembly_candidates", () => ListAssemblyCandidatesCore(workspacePath, pathFilter, includeAllBuilds));

    private string ListAssemblyCandidatesCore(string workspacePath, string? pathFilter, bool includeAllBuilds)
    {
        try
        {
            var candidates = assemblyDiscovery.Discover(workspacePath);

            if (!string.IsNullOrWhiteSpace(pathFilter))
            {
                candidates = candidates
                    .Where(candidate => candidate.ProjectPath.Contains(pathFilter, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            var totalCandidateCount = candidates.Count;

            if (!includeAllBuilds)
            {
                // Collapse to one representative per project (the discovery service already
                // orders candidates with the most useful build of each project first), so a
                // monorepo with hundreds of Configuration/TFM combinations doesn't force the
                // caller to scroll past dozens of near-duplicate entries for the same project to
                // find the ones that matter.
                var otherBuildCounts = candidates
                    .GroupBy(candidate => candidate.ProjectPath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count() - 1, StringComparer.OrdinalIgnoreCase);

                candidates = candidates
                    .GroupBy(candidate => candidate.ProjectPath, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToArray();

                return resultFormatter.Format(new
                {
                    workspacePath = Path.GetFullPath(workspacePath),
                    pathFilter,
                    totalCandidateCount,
                    candidates = candidates.Select(candidate => new
                    {
                        assemblyPath = candidate.AssemblyPath,
                        projectPath = candidate.ProjectPath,
                        configuration = candidate.Configuration,
                        targetFramework = candidate.TargetFramework,
                        lastWriteTimeUtc = candidate.LastWriteTimeUtc,
                        isPreferred = candidate.IsPreferred,
                        likelyContainsDbContext = candidate.LikelyContainsDbContext,
                        otherBuildsOfThisProject = otherBuildCounts[candidate.ProjectPath],
                    }),
                    hint = "Each project is represented by a single recommended build. Set includeAllBuilds to true to list every Configuration/TFM combination.",
                });
            }

            return resultFormatter.Format(new
            {
                workspacePath = Path.GetFullPath(workspacePath),
                pathFilter,
                totalCandidateCount,
                candidates = candidates.Select(candidate => new
                {
                    assemblyPath = candidate.AssemblyPath,
                    projectPath = candidate.ProjectPath,
                    configuration = candidate.Configuration,
                    targetFramework = candidate.TargetFramework,
                    lastWriteTimeUtc = candidate.LastWriteTimeUtc,
                    isPreferred = candidate.IsPreferred,
                    likelyContainsDbContext = candidate.LikelyContainsDbContext,
                }),
            });
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
        => Execute("load_assembly", () => LoadAssemblyCore(assemblyPath));

    private string LoadAssemblyCore(string assemblyPath)
    {
        try
        {
            var handle = assemblyLoader.Load(assemblyPath);
            var scan = DbContextScanner.FindDbContextTypes(handle.Assembly);
            var warnings = BuildScanWarnings(scan, handle);
            logger.LogInformation(
                "Loaded target assembly. Path={AssemblyPath} DbContextCount={DbContextCount}",
                handle.AssemblyPath, scan.Descriptors.Count);
            if (warnings.Count > 0)
            {
                logger.LogWarning(
                    "Assembly scan produced warnings. Path={AssemblyPath} Warnings={Warnings}",
                    handle.AssemblyPath, string.Join(" | ", warnings));
            }

            return resultFormatter.Format(new
            {
                loadedAssemblyPath = handle.AssemblyPath,
                loadedAtUtc = handle.LoadedAtUtc,
                discoveredDbContexts = scan.Descriptors.Select(c => new { name = c.Name, fullName = c.FullName, constructionKind = c.ConstructionKind.ToString() }),
                defaultContext = scan.Descriptors.Count == 1 ? scan.Descriptors[0].Name : null,
                hint = scan.Descriptors.Count == 1
                    ? $"get_schema may omit contextName; '{scan.Descriptors[0].Name}' will be used by default."
                    : null,
                warnings = warnings.Count > 0 ? warnings : null,
            });
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
        => Execute("list_contexts", ListContextsCore);

    private string ListContextsCore()
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

        return resultFormatter.Format(new
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
        });
    }

    [McpServerTool(Name = "get_schema"), Description(
        "Returns a bounded, paginated page of the EF Core model (entities, properties, keys, foreign keys, navigations) " +
        "for a DbContext in the currently loaded target assembly. Use nextPage when hasMore is true.")]
    public string GetSchema(
        [Description("Optional DbContext short name or fully qualified CLR type name. Omit only when the loaded assembly has exactly one DbContext.")] string? contextName = null,
        [Description("Logical connection name from the server's connection registry, used only to construct the context; no query is executed against the database to build the schema. If omitted, the currently active connection is used.")] string? connectionName = null,
        [Description("One-based entity page number. Defaults to 1.")] int page = 1,
        [Description("Number of entities per page. Defaults to 25 and is capped at 100.")] int pageSize = 25)
        => Execute("get_schema", () => GetSchemaCore(contextName, connectionName, page, pageSize));

    private string GetSchemaCore(string? contextName, string? connectionName, int page, int pageSize)
    {
        if (page < 1)
            throw new McpException("`page` must be at least 1.");
        if (pageSize < 1 || pageSize > 100)
            throw new McpException("`pageSize` must be between 1 and 100.");

        var contextType = ResolveContextType(contextName);
        var entry = ResolveConnection(connectionName);

        logger.LogInformation("get_schema requested. Context={ContextName} Connection={ConnectionName} Page={Page} PageSize={PageSize}", contextType.Name, connectionName, page, pageSize);

        var schema = schemaCache.GetOrBuild(contextType, () =>
        {
            using var context = CreateContext(contextType, entry);
            return Schema.SchemaBuilder.Build(context);
        });

        var totalEntityCount = schema.Entities.Count;
        var offset = (long)(page - 1) * pageSize;
        var entities = offset >= totalEntityCount
            ? []
            : schema.Entities.Skip((int)offset).Take(pageSize).ToArray();
        var hasMore = offset + pageSize < totalEntityCount;
        return resultFormatter.Format(new
        {
            contextName = schema.ContextName,
            totalEntityCount,
            page,
            pageSize,
            entities,
            truncated = hasMore,
            hasMore,
            nextPage = hasMore ? page + 1 : (int?)null,
            hint = hasMore ? $"Call get_schema with page={page + 1} and pageSize={pageSize} to retrieve the next entity page." : null,
        });
    }

    [McpServerTool(Name = "get_entity_schema"), Description(
        "Returns the complete cached schema definition (properties, primary keys, foreign keys, navigations, " +
        "ownership, and inheritance metadata) for one exact entity name on a DbContext already discovered by " +
        "get_schema. Cache-only: never constructs a DbContext, opens a database connection, or rediscovers the " +
        "model. Call get_schema first if the schema has not been built yet for this context.")]
    public string GetEntitySchema(
        [Description("Exact entity name (CLR type name), as returned by get_schema/list_contexts entity names.")] string entityName,
        [Description("Optional DbContext short name or fully qualified CLR type name. Omit only when the loaded assembly has exactly one DbContext.")] string? contextName = null)
        => Execute("get_entity_schema", () => GetEntitySchemaCore(contextName, entityName));

    private string GetEntitySchemaCore(string? contextName, string entityName)
    {
        if (string.IsNullOrWhiteSpace(entityName))
            throw new McpException("`entityName` must not be empty.");

        var contextType = ResolveContextType(contextName);
        var schema = RequireCachedSchema(contextType);

        logger.LogInformation("get_entity_schema requested. Context={ContextName} Entity={EntityName}", contextType.Name, entityName);

        var entity = Schema.SchemaSlicer.FindEntity(schema, entityName, Schema.NoOpSchemaAccessPolicy.Instance);
        if (entity is null)
        {
            var known = schema.Entities.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var choices = known.Length == 0 ? "(none)" : string.Join(", ", known);
            throw new McpException(
                $"No entity named '{entityName}' was found in the cached schema for '{schema.ContextName}'. " +
                $"Known entities: {choices}. Next step: call get_schema to list all entities, then pass an exact entityName.");
        }

        return resultFormatter.Format(new
        {
            contextName = schema.ContextName,
            entity,
        });
    }

    [McpServerTool(Name = "search_schema"), Description(
        "Searches the cached schema for a DbContext already discovered by get_schema, matching entity names, " +
        "property names, and relationship (navigation) names against a case-insensitive substring query. Returns " +
        "compact matches only (not full entity definitions); use get_entity_schema for a complete slice. " +
        "Cache-only: never constructs a DbContext, opens a database connection, or rediscovers the model.")]
    public string SearchSchema(
        [Description("Optional DbContext short name or fully qualified CLR type name. Omit only when the loaded assembly has exactly one DbContext.")] string? contextName = null,
        [Description("Non-empty, case-insensitive substring to match against entity, property, and relationship names.")] string query = "",
        [Description("Maximum number of entity matches to return. Defaults to 10 and is capped at 25.")] int? maxResults = null)
        => Execute("search_schema", () => SearchSchemaCore(contextName, query, maxResults));

    private string SearchSchemaCore(string? contextName, string query, int? maxResults)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new McpException("`query` must not be empty.");

        var effectiveMaxResults = maxResults ?? Schema.SchemaSlicer.DefaultSearchResults;
        if (effectiveMaxResults < 1 || effectiveMaxResults > Schema.SchemaSlicer.MaxSearchResults)
            throw new McpException($"`maxResults` must be between 1 and {Schema.SchemaSlicer.MaxSearchResults}.");

        var contextType = ResolveContextType(contextName);
        var schema = RequireCachedSchema(contextType);

        logger.LogInformation("search_schema requested. Context={ContextName} Query={Query} MaxResults={MaxResults}", contextType.Name, query, effectiveMaxResults);

        var result = Schema.SchemaSlicer.Search(schema, query, effectiveMaxResults, Schema.NoOpSchemaAccessPolicy.Instance);
        var truncated = result.TotalMatchCount > result.Matches.Count;
        return resultFormatter.Format(new
        {
            contextName = schema.ContextName,
            query,
            maxResults = effectiveMaxResults,
            totalMatchCount = result.TotalMatchCount,
            matches = result.Matches,
            truncated,
        });
    }

    [McpServerTool(Name = "run_query"), Description(
        "Executes a safe, read-only LINQPad-style expression rooted at a public DbSet property on the selected DbContext. " +
        "For example: Customers.Where(c => c.Age > 18).Select(c => c.Name). A terminal call like .ToList()/.FirstOrDefault() is never required: " +
        "results are always materialized, deterministically ordered, and capped server-side (terminal scalar aggregates/element operators such as " +
        "Count/FirstOrDefault/Single/Any are not paginated), but adding one still narrows the result as expected. Allowed operators: Where, Select, " +
        "GroupBy, ordering (OrderBy/OrderByDescending/ThenBy/ThenByDescending), Skip, Take, Distinct, Count, LongCount, Sum, Average, Min, Max, First, " +
        "FirstOrDefault, Single, SingleOrDefault, Any, All, and the set operators Concat/Union/Except/Intersect (which may reference another public " +
        "DbSet by name, e.g. Customers.Select(c => c.Name).Union(Orders.Select(o => o.OwnerName))). Join, GroupJoin, SelectMany, and Zip are NOT " +
        "supported (a hard Dynamic LINQ parser limitation) — use a navigation-property predicate instead, e.g. Orders.Where(o => o.Customer.Name == \"Alice\"). " +
        "The response includes hasMoreRows: true when at least one further row exists beyond the returned page (rows/rowCount are always capped at " +
        "effectiveTake); it is false for take:0 and for terminal scalar/element results.")]
    public Task<string> RunQuery(
        [Description("CLR type name of the DbContext, as returned by list_contexts.")] string contextName,
        [Description("LINQPad-style expression rooted at a public DbSet property, e.g. Customers.Where(c => c.Age > 18).Select(c => c.Name). ")] string query,
        [Description("Logical connection name from the server's connection registry. If omitted, the currently active connection is used.")] string? connectionName = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync("run_query", () => RunQueryCore(contextName, query, connectionName, cancellationToken));

    private async Task<QueryResult> ExecuteDynamicLinqAsync(Type contextType, ConnectionRegistryEntry entry, string query, CancellationToken cancellationToken)
    {
        using var context = CreateContext(contextType, entry);
        return await queryExecutor.ExecuteAsync(context, new QueryRequest { Query = query }, entry.CommandTimeoutSeconds, cancellationToken);
    }

    private async Task<string> RunQueryCore(string contextName, string query, string? connectionName, CancellationToken cancellationToken)
    {
        var contextType = ResolveContextType(contextName);
        var entry = ResolveConnection(connectionName);
        try
        {
            var result = queryExecutionOptions.Engine switch
            {
                QueryEngine.DynamicLinq => await ExecuteDynamicLinqAsync(contextType, entry, query, cancellationToken),
                QueryEngine.Roslyn => await roslynQueryExecutor.ExecuteAsync(
                    RequireLoadedAssembly(), contextType, entry, ResolveEffectiveProvider(contextType, entry),
                    new QueryRequest { Query = query }, cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported query engine '{queryExecutionOptions.Engine}'.")
            };
            return resultFormatter.Format(result);
        }
        catch (QueryExecutionException ex)
        {
            throw new McpException(FormatQueryError(ex));
        }
    }
    [McpServerTool(Name = "run_sql_query"), Description(
        "Executes a parameterized raw SQL command against a Development ReadWrite connection. This potentially " +
        "destructive tool is unavailable unless RawSqlExecution:Enabled is explicitly set to true on the server; " +
        "it is always rejected for Production and ReadOnly connections. Use @p0, @p1, ... placeholders for values " +
        "provided through parameters. Result rows are capped server-side.")]
    public Task<string> RunSqlQuery(
        [Description("CLR type name of the DbContext, as returned by list_contexts.")] string contextName,
        [Description("Raw SQL command text. Use @p0, @p1, ... for values rather than embedding them in this string.")] string sql,
        [Description("Logical connection name from the server's connection registry. If omitted, the currently active connection is used.")] string? connectionName = null,
        [Description("Positional parameter values referenced by SQL placeholders @p0, @p1, ...")] object?[]? parameters = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync("run_sql_query", () => RunSqlQueryCore(contextName, sql, connectionName, parameters, cancellationToken));

    private async Task<string> RunSqlQueryCore(string contextName, string sql, string? connectionName, object?[]? parameters, CancellationToken cancellationToken)
    {
        if (!rawSqlExecutionOptions.Enabled)
        {
            throw new McpException(
                "Raw SQL execution is disabled by default as a safety guard. To enable it, set " +
                "RawSqlExecution:Enabled to true in the server's configuration (appsettings.json, an " +
                "environment variable such as DOTNETEFCOREMCP_RawSqlExecution__Enabled=true, or user-secrets) " +
                "and restart the MCP server process - this cannot be toggled per-request or per-session from " +
                "an MCP client. Even when enabled, run_sql_query still refuses Production connections and " +
                "requires a ReadWrite connection, so consider whether run_query (structured, always-on, " +
                "read-only LINQ-style querying) already covers your need before enabling raw SQL.");
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
            return resultFormatter.Format(result);
        }
        catch (QueryExecutionException ex)
        {
            throw new McpException(FormatSqlQueryError(ex));
        }
    }

    [McpServerTool(Name = "list_connections"), Description(
        "Lists the connections currently registered in the server's connection registry, including each " +
        "connection's database provider, access mode, environment (Development/Staging/Production), whether it " +
        "is a production connection (always read-only, swap-protected), and which one is currently active. " +
        "Connection strings are never exposed.")]
    public string ListConnections()
        => Execute("list_connections", ListConnectionsCore);

    private string ListConnectionsCore()
    {
        var infos = connectionRegistry.ListConnections();
        return resultFormatter.Format(new
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
        });
    }

    [McpServerTool(Name = "swap_connection"), Description(
        "Swaps the server's active connection to the named connection, changing which connection subsequent " +
        "get_schema/run_query calls default to when no explicit connectionName is supplied. For production " +
        "connections (environment = Production), this is refused unless allowProduction is set to true.")]
    public string SwapConnection(
        [Description("Logical connection name from the server's connection registry to make active.")] string connectionName,
        [Description("Set to true to allow making a production connection active (for intentionally running diagnostics/read-only queries against production).")] bool allowProduction = false)
        => Execute("swap_connection", () => SwapConnectionCore(connectionName, allowProduction));

    private string SwapConnectionCore(string connectionName, bool allowProduction)
    {
        try
        {
            connectionRegistry.SetActive(connectionName, allowProduction);
            return resultFormatter.Format(new
            {
                activeConnection = connectionRegistry.ActiveConnectionName,
            });
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

    private T Execute<T>(string operation, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateUnexpectedToolException(operation, ex);
        }
    }

    private async Task<T> ExecuteAsync<T>(string operation, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (McpException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw CreateUnexpectedToolException(operation, ex);
        }
    }

    private McpException CreateUnexpectedToolException(string operation, Exception exception)
    {
        var errorId = Guid.NewGuid().ToString("N");
        logger.LogError(exception, "Unexpected error invoking MCP tool {ToolName}. ErrorId={ErrorId}", operation, errorId);

        if (!toolDiagnosticsOptions.ExposeSafeErrorDetails)
        {
            return new McpException(
                $"{operation} failed unexpectedly. Error reference: {errorId}. " +
                "Check the server logs or contact the server operator.");
        }

        var hint = DescribeIfAssemblyIdentitySplit(exception) ?? GenericUnexpectedErrorHint;
        return new McpException(
            $"{operation} failed unexpectedly. Error reference: {errorId}. " +
            $"Failure category: {exception.GetType().Name}. Next step: {hint}");
    }

    /// <summary>Recognizes the small family of exceptions ("field/method not found", "type could not be
    /// loaded", "could not load file or assembly") that .NET throws when code in the isolated target
    /// <see cref="AssemblyLoadContext"/> touches a type from an assembly that is loaded twice - once in
    /// the default context (shared by the server and, transitively, EF Core) and once as a second,
    /// type-identity-incompatible copy inside the target's own context - instead of resolving to a
    /// single shared copy. This is a server-side <c>TargetAssemblyLoadContext.SharedAssemblyNames</c>
    /// configuration gap, not a problem with the target project, but the raw CLR exception message
    /// (e.g. "Field not found: 'Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuting'")
    /// gives no hint of that, sending anyone debugging it down the wrong path entirely. Returns
    /// <see langword="null"/> for exceptions unrelated to this pattern so they receive the generic,
    /// vetted diagnostic hint instead.</summary>
    private static string? DescribeIfAssemblyIdentitySplit(Exception exception)
    {
        if (exception is not (MissingFieldException or MissingMethodException or TypeLoadException
            or TypeInitializationException or FileLoadException))
        {
            return null;
        }

        return "This looks like an assembly load context (ALC) type-identity mismatch inside the MCP " +
            "server, not a problem with the target project: the target assembly was loaded into an " +
            "isolated ALC that ended up with its own separate copy of a dependency the shared EF Core " +
            "assemblies also reference (for example Microsoft.Extensions.Logging.Abstractions), instead " +
            "of sharing the server's copy. That produces two type-incompatible copies of the same type, " +
            "which surfaces as a confusing \"field/method not found\" or \"could not load\" error even " +
            "though the member genuinely exists. If this keeps happening for a specific dependency, it " +
            "usually means that assembly's simple name needs to be added to " +
            "TargetAssemblyLoadContext.SharedAssemblyNames in the MCP server itself; retrying or " +
            "reloading the assembly will not help.";
    }

    private LoadedAssemblyHandle RequireLoadedAssembly()
    {
        return assemblyLoader.Current
            ?? throw new McpException("No target assembly is loaded yet. Call load_assembly with the path to a compiled target project's DLL first.");
    }

    /// <summary>Cache-only schema retrieval for <c>get_entity_schema</c>/<c>search_schema</c>: never
    /// builds a schema (which would require constructing a <c>DbContext</c>), it only reads whatever
    /// <c>get_schema</c> has already cached for <paramref name="contextType"/>.</summary>
    private Schema.SchemaDto RequireCachedSchema(Type contextType)
    {
        if (schemaCache.TryGet(contextType, out var schema) && schema is not null)
            return schema;

        throw new McpException(
            $"No cached schema exists yet for '{contextType.Name}'. Next step: call get_schema for this context first, " +
            "then retry.");
    }

    private Type ResolveContextType(string? contextName)
    {
        var handle = RequireLoadedAssembly();
        var scan = DbContextScanner.FindDbContextTypes(handle.Assembly);
        var contexts = scan.Descriptors;

        if (string.IsNullOrWhiteSpace(contextName))
        {
            if (contexts.Count == 1)
                return contexts[0].ClrType;

            throw new McpException(BuildContextSelectionError(
                contexts,
                contexts.Count == 0
                    ? "No DbContexts were found in the currently loaded assembly."
                    : "`contextName` is required because the loaded assembly contains multiple DbContexts."));
        }

        var matches = contexts
            .Where(c => string.Equals(c.FullName, contextName, StringComparison.Ordinal) ||
                        string.Equals(c.Name, contextName, StringComparison.Ordinal))
            .ToArray();

        if (matches.Length == 1)
            return matches[0].ClrType;

        var reason = matches.Length > 1
            ? $"DbContext name '{contextName}' is ambiguous."
            : $"No DbContext named '{contextName}' was found in the currently loaded assembly.";
        throw new McpException(BuildContextSelectionError(contexts, reason));
    }

    private static string BuildContextSelectionError(IReadOnlyList<DbContextDescriptor> contexts, string reason)
    {
        var choices = contexts.Count == 0 ? "(none)" : string.Join(", ", contexts.Select(c => c.Name));
        return $"{reason} Choose one of these short context names: {choices}. Next step: call list_contexts, then pass contextName using a listed short name or fully qualified name.";
    }

    private static string FormatQueryError(QueryExecutionException exception)
    {
        var cause = exception.InnerException?.Message?.Trim();
        if (string.IsNullOrEmpty(cause))
            return $"{exception.Message} Next step: {GenericQueryRecoveryHint}";

        if (cause.Contains("RowLimitingOperationWithoutOrderByWarning", StringComparison.OrdinalIgnoreCase) ||
            (cause.Contains("Skip", StringComparison.OrdinalIgnoreCase) && cause.Contains("Take", StringComparison.OrdinalIgnoreCase)))
        {
            return $"{exception.Message} Cause: {cause} Next step: Add a deterministic orderBy expression whenever using skip or take, then retry the query.";
        }

        if (cause.Contains("Globalization Invariant Mode is not supported", StringComparison.OrdinalIgnoreCase))
        {
            return $"{exception.Message} Cause: {cause} Next step: Enable ICU/globalization support in the target .NET runtime or container, then retry the query.";
        }

        return $"{exception.Message} Next step: {GenericQueryRecoveryHint}";
    }

    private const string GenericQueryRecoveryHint =
        "verify entity and property names with get_schema, validate Dynamic LINQ syntax, and consult server logs if the problem persists.";

    private const string GenericUnexpectedErrorHint =
        "check the server logs using the error reference; diagnostic messages and stack traces are intentionally not returned to MCP callers.";

    /// <summary>Adds actionable next-step guidance to raw SQL execution failures, similar in
    /// spirit to <see cref="FormatQueryError"/> for the structured run_query tool. Raw SQL errors
    /// are frequently just the provider's native error message (e.g. a SQL syntax error or a
    /// missing table/column), which is easy to misdiagnose as "the tool is broken" without a
    /// pointer back toward get_schema/list_contexts.</summary>
    private static string FormatSqlQueryError(QueryExecutionException exception)
    {
        var cause = exception.InnerException?.Message?.Trim();
        return string.IsNullOrEmpty(cause)
            ? $"{exception.Message} Next step: verify the SQL against get_schema's entity/table names, confirm parameter placeholders (@p0, @p1, ...) match the values supplied, and consult server logs if the problem persists."
            : $"{exception.Message} Cause: {cause} Next step: verify the SQL against get_schema's entity/table names, confirm parameter placeholders (@p0, @p1, ...) match the values supplied, and consult server logs if the problem persists.";
    }

    /// <summary>Turns a <see cref="DbContextScanResult"/>'s type-load diagnostics into
    /// client-facing warning strings, adding an explicit "zero DbContexts found" warning when the
    /// scan came back empty (regardless of whether that was caused by type-load failures) so the
    /// caller never has to infer the problem from an empty list alone.</summary>
    private static List<string> BuildScanWarnings(DbContextScanResult scan, LoadedAssemblyHandle? handle = null)
    {
        var warnings = new List<string>();

        // Dependency diagnostics come first: an unresolvable shared framework is usually the cause
        // of the type-load failures reported below it, so it should not be buried under them.
        if (handle is not null)
        {
            warnings.AddRange(handle.DependencyDiagnostics);
        }

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

    [McpServerTool(Name = "test_connection"), Description(
        "Runs a bounded, read-only connectivity probe against a registered connection: resolves the connection " +
        "registry entry and constructs the requested DbContext, then checks whether the provider accepts a " +
        "connection within the connection's configured command timeout. Never executes user SQL, never changes " +
        "the active connection, and never returns connection strings or provider error details - only a " +
        "redacted status (healthy/failed/timedOut).")]
    public Task<string> TestConnection(
        [Description("CLR type name of the DbContext, as returned by list_contexts.")] string contextName,
        [Description("Logical connection name from the server's connection registry. If omitted, the currently active connection is used.")] string? connectionName = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync("test_connection", () => TestConnectionCore(contextName, connectionName, cancellationToken));

    private async Task<string> TestConnectionCore(string contextName, string? connectionName, CancellationToken cancellationToken)
    {
        var contextType = ResolveContextType(contextName);
        var entry = ResolveConnection(connectionName);
        var provider = ResolveEffectiveProvider(contextType, entry);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ConnectionHealthStatus status;
        using (var context = CreateContext(contextType, entry))
        {
            status = await ConnectionHealthChecker.CheckAsync(context, entry.CommandTimeoutSeconds, queryExecutionOptions.CancellationMargin, cancellationToken);
        }
        stopwatch.Stop();

        logger.LogInformation(
            "test_connection completed. Context={ContextName} Connection={ConnectionName} Provider={Provider} Environment={Environment} Status={Status} ElapsedMs={ElapsedMs}",
            contextType.Name, entry.Name, provider, entry.Environment, status, stopwatch.ElapsedMilliseconds);

        return resultFormatter.Format(new
        {
            contextName = contextType.Name,
            connectionName = entry.Name,
            provider = provider.ToString(),
            environment = entry.Environment.ToString(),
            status = status switch
            {
                ConnectionHealthStatus.Healthy => "healthy",
                ConnectionHealthStatus.Failed => "failed",
                ConnectionHealthStatus.TimedOut => "timedOut",
                _ => throw new InvalidOperationException($"Unsupported connection health status '{status}'."),
            },
        });
    }
}
