using System.ComponentModel;
using System.Text.Json;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Migrations;
using DotnetEfCoreMcp.Server.Mutations;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DotnetEfCoreMcp.Server.Tools;

/// <summary>The MCP tool surface for this server. Tools operate against one or more loaded target
/// assemblies (see <see cref="LoadAssembly"/>), selected by an optional <c>targetName</c> parameter;
/// omitting the name resolves to the current default target for backward compatibility with single-target
/// callers. All tools resolve connection strings from the server-side <see cref="ConnectionRegistry"/>
/// by logical name - an MCP client can never supply a raw connection string.</summary>
[McpServerToolType]
public sealed class EfCoreMcpTools(
    AssemblyLoaderService assemblyLoader,
    AssemblyDiscoveryService assemblyDiscovery,
    ConnectionRegistry connectionRegistry,
    SchemaCache schemaCache,
    RoslynQueryExecutor roslynQueryExecutor,
    OutOfProcessRoslynQueryExecutor outOfProcessRoslynQueryExecutor,
    PooledOutOfProcessRoslynQueryExecutor pooledOutOfProcessRoslynQueryExecutor,
    QueryExecutionOptions queryExecutionOptions,
    RawSqlExecutionOptions rawSqlExecutionOptions,
    SqlQueryExecutor sqlQueryExecutor,
    MigrationsOptions migrationsOptions,
    MigrationInspector migrationInspector,
    IToolResultFormatter resultFormatter,
    ToolDiagnosticsOptions toolDiagnosticsOptions,
    ILogger<EfCoreMcpTools> logger,
    EntityMutationsOptions entityMutationsOptions,
    EntityMutationExecutor entityMutationExecutor)
{
    internal EfCoreMcpTools(
        AssemblyLoaderService assemblyLoader,
        AssemblyDiscoveryService assemblyDiscovery,
        ConnectionRegistry connectionRegistry,
        SchemaCache schemaCache,
        RoslynQueryExecutor roslynQueryExecutor,
        OutOfProcessRoslynQueryExecutor outOfProcessRoslynQueryExecutor,
        QueryExecutionOptions queryExecutionOptions,
        RawSqlExecutionOptions rawSqlExecutionOptions,
        SqlQueryExecutor sqlQueryExecutor,
        MigrationsOptions migrationsOptions,
        MigrationInspector migrationInspector,
        IToolResultFormatter resultFormatter,
        ToolDiagnosticsOptions toolDiagnosticsOptions,
        ILogger<EfCoreMcpTools> logger,
        EntityMutationsOptions entityMutationsOptions,
        EntityMutationExecutor entityMutationExecutor)
        : this(
            assemblyLoader,
            assemblyDiscovery,
            connectionRegistry,
            schemaCache,
            roslynQueryExecutor,
            outOfProcessRoslynQueryExecutor,
            new PooledOutOfProcessRoslynQueryExecutor(
                new QueryHostPool(queryExecutionOptions, outOfProcessRoslynQueryExecutor, NullLogger<QueryHostPool>.Instance)),
            queryExecutionOptions,
            rawSqlExecutionOptions,
            sqlQueryExecutor,
            migrationsOptions,
            migrationInspector,
            resultFormatter,
            toolDiagnosticsOptions,
            logger,
            entityMutationsOptions,
            entityMutationExecutor)
    {
    }


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
        "into an isolated, collectible AssemblyLoadContext, replacing any previously loaded assembly under the same " +
        "target. Returns warnings when no DbContexts are found or when assembly types could not load. " +
        "Call this before list_contexts/get_schema/run_query, or again after rebuilding the target project. " +
        "Pass targetName to register/replace an additional named target instead of the default one, so multiple " +
        "compiled assemblies can be loaded and addressed simultaneously; omit it to preserve today's single-target " +
        "behavior.")]
    public string LoadAssembly(
        [Description("Absolute or relative path to the target project's compiled assembly DLL.")] string assemblyPath,
        [Description("Optional logical name to register this assembly under, so it can be addressed later via targetName without disturbing other loaded targets. Omit to load/replace the default target (today's behavior).")] string? targetName = null)
        => Execute("load_assembly", () => LoadAssemblyCore(assemblyPath, targetName));

    private string LoadAssemblyCore(string assemblyPath, string? targetName = null)
    {
        try
        {
            var handle = assemblyLoader.Load(assemblyPath, targetName);
            var scan = DbContextScanner.FindDbContextTypes(handle.Assembly);
            var warnings = BuildScanWarnings(scan, handle);
            logger.LogInformation(
                "Loaded target assembly. Path={AssemblyPath} TargetName={TargetName} DbContextCount={DbContextCount}",
                handle.AssemblyPath, targetName, scan.Descriptors.Count);
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
                targetName,
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
        "including warnings when none are found or when assembly types could not load. Pass targetName to inspect a " +
        "non-default named target loaded via load_assembly; omit it to use the current default target (today's behavior).")]
    public string ListContexts(
        [Description("Optional name of a target registered via load_assembly's targetName parameter. Omit to use the current default target.")] string? targetName = null)
        => Execute("list_contexts", () => ListContextsCore(targetName));

    private string ListContextsCore(string? targetName = null)
    {
        var handle = RequireLoadedAssembly(targetName);
        var scan = DbContextScanner.FindDbContextTypes(handle.Assembly);
        var warnings = BuildScanWarnings(scan);
        if (warnings.Count > 0)
        {
            logger.LogWarning(
                "Assembly scan produced warnings. Path={AssemblyPath} Warnings={Warnings}",
                handle.AssemblyPath, string.Join(" | ", warnings));
        }

        // list_contexts must filter to the active connection's AccessPolicy (P0 #9) when a
        // connection is active, but must not fail (or invoke the throwing ResolveConnection) when
        // none is - e.g. immediately after load_assembly, before any connection has been selected -
        // since discovering available contexts is a prerequisite for picking one.
        var activeConnection = connectionRegistry.ActiveConnection;
        var visibleDescriptors = activeConnection is null
            ? scan.Descriptors
            : scan.Descriptors.Where(c => activeConnection.AccessPolicy.IsContextReachable(c.FullName)).ToArray();

        return resultFormatter.Format(new
        {
            assemblyPath = handle.AssemblyPath,
            isStale = assemblyLoader.IsTargetStale(targetName),
            contexts = visibleDescriptors.Select(c => new
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
        [Description("Logical connection name from the server's connection registry, used only to construct the context; no query is executed against the database to build the schema. Required whenever more than one connection is registered; if omitted and exactly one connection is registered, that connection is used.")] string? connectionName = null,
        [Description("One-based entity page number. Defaults to 1.")] int page = 1,
        [Description("Number of entities per page. Defaults to 25 and is capped at 100.")] int pageSize = 25,
        [Description("Optional name of a target registered via load_assembly's targetName parameter. Omit to use the current default target.")] string? targetName = null)
        => Execute("get_schema", () => GetSchemaCore(contextName, connectionName, page, pageSize, targetName));

    private string GetSchemaCore(string? contextName, string? connectionName, int page, int pageSize, string? targetName = null)
    {
        if (page < 1)
            throw new McpException("`page` must be at least 1.");
        if (pageSize < 1 || pageSize > 100)
            throw new McpException("`pageSize` must be between 1 and 100.");

        var entry = ResolveConnection(connectionName);
        var contextType = ResolveContextType(contextName, entry, targetName);
        EnsureContextReachable(contextType, entry);

        logger.LogInformation("get_schema requested. Context={ContextName} Connection={ConnectionName} Page={Page} PageSize={PageSize}", contextType.Name, connectionName, page, pageSize);

        // The cache stores the full, unfiltered schema (it is keyed only by contextType and shared
        // across connections/callers), so the per-connection AccessPolicy filter is applied to a
        // fresh, non-mutating view every call rather than being baked into the cached value.
        var cachedSchema = schemaCache.GetOrBuild(contextType, () =>
        {
            using var context = CreateContext(contextType, entry);
            return Schema.SchemaBuilder.Build(context);
        });
        var policy = new Schema.ConnectionSchemaAccessPolicy(entry.AccessPolicy, contextType.FullName);
        var schema = policy.Apply(cachedSchema);

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

        var entry = ResolveConnection(null);
        var contextType = ResolveContextType(contextName, entry);
        EnsureContextReachable(contextType, entry);
        var schema = RequireCachedSchema(contextType);
        var policy = new Schema.ConnectionSchemaAccessPolicy(entry.AccessPolicy, contextType.FullName);

        logger.LogInformation("get_entity_schema requested. Context={ContextName} Entity={EntityName}", contextType.Name, entityName);

        var entity = Schema.SchemaSlicer.FindEntity(schema, entityName, policy);
        if (entity is null)
        {
            // The known-entities list below is drawn from the policy-filtered view only, so a
            // denied/unknown entityName can never be distinguished from one that genuinely does not
            // exist in the model (P0 #9 non-disclosure requirement).
            var known = policy.Apply(schema).Entities.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
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

        var entry = ResolveConnection(null);
        var contextType = ResolveContextType(contextName, entry);
        EnsureContextReachable(contextType, entry);
        var schema = RequireCachedSchema(contextType);
        var policy = new Schema.ConnectionSchemaAccessPolicy(entry.AccessPolicy, contextType.FullName);

        logger.LogInformation("search_schema requested. Context={ContextName} Query={Query} MaxResults={MaxResults}", contextType.Name, query, effectiveMaxResults);

        var result = Schema.SchemaSlicer.Search(schema, query, effectiveMaxResults, policy);
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
        "Executes a safe, read-only LINQPad-style C# expression rooted at a public DbSet property on the selected DbContext. " +
        "For example: Customers.Where(c => c.Age > 18).Select(c => c.Name). A terminal call like .ToList()/.FirstOrDefault() is never required: " +
        "IQueryable results are materialized and capped server-side at 50 rows by default, up to a configured maximum of 200 (scalar aggregates/element operators such as " +
        "Count/FirstOrDefault/Single/Any return single values; already-materialized results like .ToList() return as-is without capping). Add an explicit OrderBy() when using Skip()/Take() to ensure stable ordering. " +
        "The full LINQPad surface is supported: Where, Select, GroupBy, ordering (OrderBy/OrderByDescending/ThenBy/ThenByDescending), Skip, Take, " +
        "Distinct, Count, LongCount, Sum, Average, Min, Max, First, FirstOrDefault, Single, SingleOrDefault, Any, All, Join, GroupJoin, SelectMany, " +
        "Zip, and the set operators Concat/Union/Except/Intersect (which may reference another public DbSet by name, e.g. " +
        "Customers.Select(c => c.Name).Union(Orders.Select(o => o.OwnerName))). " +
        "The response includes hasMoreRows: true when at least one further row exists beyond the returned page (rows/rowCount are capped at " +
        "effectiveTake for IQueryable results); it is false for take:0, scalar results, and already-materialized collections.")]
    public Task<string> RunQuery(
        [Description("CLR type name of the DbContext, as returned by list_contexts.")] string contextName,
        [Description("LINQPad-style expression rooted at a public DbSet property, e.g. Customers.Where(c => c.Age > 18).Select(c => c.Name). ")] string query,
        [Description("Logical connection name from the server's connection registry. Required whenever more than one connection is registered; if omitted and exactly one connection is registered, that connection is used.")] string? connectionName = null,
        [Description("Optional name of a target registered via load_assembly's targetName parameter. Omit to use the current default target.")] string? targetName = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync("run_query", () => RunQueryCore(contextName, query, connectionName, targetName, cancellationToken));

    private async Task<string> RunQueryCore(string contextName, string query, string? connectionName, string? targetName, CancellationToken cancellationToken)
    {
        var entry = ResolveConnection(connectionName);
        var contextType = ResolveContextType(contextName, entry, targetName);

        // Entity-level access policy (P0 #9) is enforced here because the Roslyn engine constructs its
        // own DbContext subclass and never calls CreateContext (which only enforces context-level
        // reachability). The root DbSet plus any other DbSet referenced via set operators
        // (Union/Concat/...) are resolved to their entity type names up front so this single
        // enforcement point uses the same names the policy is configured with.
        EnsureContextReachable(contextType, entry);
        try
        {
            // TODO P0 #9: NormalizeAndGetRoot enforces single-expression mode (strips trailing ';' but rejects
            // multi-statement and top-level blocks) and requires root DbSet name at start, which breaks the documented
            // statement-mode queries. Access-policy enforcement must be refactored to parse statement-mode syntax for
            // root DbSet name extraction without requiring single-expression constraint, or to analyze compiled results post-binding.
            var (rootName, expressionText) = QueryExecutor.NormalizeAndGetRoot(query, queryExecutionOptions.MaxQueryLength);
            foreach (var entityName in QueryExecutor.ResolveReferencedEntityNames(contextType, rootName, expressionText))
            {
                EnsureEntityAllowed(contextType, entry, entityName);
            }

            var result = await ExecuteRoslynAsync(contextType, entry, expressionText, targetName, cancellationToken);
            return resultFormatter.Format(result);
        }
        catch (QueryExecutionException ex)
        {
            throw new McpException(FormatQueryError(ex));
        }
    }
    private Task<QueryResult> ExecuteRoslynAsync(Type contextType, ConnectionRegistryEntry entry, string query, string? targetName, CancellationToken cancellationToken)
    {
        var target = RequireLoadedAssembly(targetName);
        var provider = ResolveEffectiveProvider(contextType, entry);
        var request = new QueryRequest { Query = query };
        return queryExecutionOptions.Mode switch
        {
            QueryExecutionMode.InProcess => roslynQueryExecutor.ExecuteAsync(target, contextType, entry, provider, request, cancellationToken),
            QueryExecutionMode.Pooled => pooledOutOfProcessRoslynQueryExecutor.ExecuteAsync(target, contextType, entry, provider, request, cancellationToken),
            QueryExecutionMode.OutOfProcess or QueryExecutionMode.Auto => outOfProcessRoslynQueryExecutor.ExecuteAsync(target, contextType, entry, provider, request, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported query execution mode '{queryExecutionOptions.Mode}'."),
        };
    }

    [McpServerTool(Name = "preview_query_sql"), Description(
        "Previews the SQL a run_query expression would issue, without executing it: no database connection is opened, no command " +
        "is run, and no rows are read or written. However, a caller-supplied expression may force enumeration or side effects before that point. " +
        "Accepts the exact same LINQPad-style expression syntax as run_query, e.g. " +
        "Customers.Where(c => c.Age > 18).Select(c => c.Name). Only queries whose final value is an unexecuted IQueryable have SQL " +
        "to preview; scalar/element results (Count, FirstOrDefault, Sum, ...), already-materialized results (.ToList()), and " +
        "operators with no SQL translation (Zip) are rejected - use run_query for those instead; also rejected when the server's " +
        "QueryExecution:Mode is not InProcess, since previewing requires compiling and evaluating the query locally.")]
    public Task<string> PreviewQuerySql(
        [Description("CLR type name of the DbContext, as returned by list_contexts.")] string contextName,
        [Description("LINQPad-style expression rooted at a public DbSet property, e.g. Customers.Where(c => c.Age > 18).Select(c => c.Name). ")] string query,
        [Description("Logical connection name from the server's connection registry. Required whenever more than one connection is registered; if omitted and exactly one connection is registered, that connection is used.")] string? connectionName = null,
        [Description("Optional name of a target registered via load_assembly's targetName parameter. Omit to use the current default target.")] string? targetName = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync("preview_query_sql", () => PreviewQuerySqlCore(contextName, query, connectionName, targetName, cancellationToken));

    private async Task<string> PreviewQuerySqlCore(string contextName, string query, string? connectionName, string? targetName, CancellationToken cancellationToken)
    {
        var entry = ResolveConnection(connectionName);
        var contextType = ResolveContextType(contextName, entry, targetName);

        // Shares the exact same access-policy/validation pipeline as run_query (see RunQueryCore):
        // context-level reachability, then entity-level allow-listing for the root DbSet and any
        // other DbSet referenced via set operators (Union/Concat/...).
        EnsureContextReachable(contextType, entry);
        try
        {
            var (rootName, expressionText) = QueryExecutor.NormalizeAndGetRoot(query, queryExecutionOptions.MaxQueryLength);
            foreach (var entityName in QueryExecutor.ResolveReferencedEntityNames(contextType, rootName, expressionText))
            {
                EnsureEntityAllowed(contextType, entry, entityName);
            }

            // preview_query_sql must compile and evaluate the query's C# expression locally to build
            // the IQueryable for ToQueryString(). It only works when QueryExecution:Mode is InProcess
            // because the out-of-process/pooled wire protocol only carries materialized QueryResultWire,
            // never an unexecuted IQueryable. Reject if the operator has configured isolation.
            if (queryExecutionOptions.Mode != QueryExecutionMode.InProcess)
            {
                throw new QueryExecutionException(
                    "preview_query_sql requires QueryExecution:Mode to be InProcess because it must compile " +
                    "and evaluate the query's C# expression locally to build the IQueryable for ToQueryString(); " +
                    "the current mode ('" + queryExecutionOptions.Mode + "') isolates user-authored query " +
                    "execution in a separate process, which preview_query_sql does not use.");
            }

            var target = RequireLoadedAssembly(targetName);
            var provider = ResolveEffectiveProvider(contextType, entry);
            var request = new QueryRequest { Query = expressionText };
            var result = await roslynQueryExecutor.PreviewSqlAsync(target, contextType, entry, provider, request, cancellationToken);
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
        [Description("Logical connection name from the server's connection registry. Required whenever more than one connection is registered; if omitted and exactly one connection is registered, that connection is used.")] string? connectionName = null,
        [Description("Positional parameter values referenced by SQL placeholders @p0, @p1, ...")] object?[]? parameters = null,
        [Description("Optional name of a target registered via load_assembly's targetName parameter. Omit to use the current default target.")] string? targetName = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync("run_sql_query", () => RunSqlQueryCore(contextName, sql, connectionName, parameters, targetName, cancellationToken));

    private async Task<string> RunSqlQueryCore(string contextName, string sql, string? connectionName, object?[]? parameters, string? targetName, CancellationToken cancellationToken)
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

        var entry = ResolveConnection(connectionName);
        var contextType = ResolveContextType(contextName, entry, targetName);
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

    [McpServerTool(Name = "list_migrations"), Description(
        "Inspects Entity Framework Core migration state for a DbContext: which migrations are known to the " +
        "migration assembly, which are applied (per __EFMigrationsHistory), and which are pending. Always " +
        "available (read-only, no DDL/DML) except for Production connections, which are rejected. When the " +
        "target database is unreachable, databaseExists is false and every known migration is reported pending " +
        "with appliedStateAvailable set to false rather than presenting metadata as applied state.")]
    public Task<string> ListMigrations(
        [Description("CLR type name of the DbContext, as returned by list_contexts.")] string contextName,
        [Description("Logical connection name from the server's connection registry. Required whenever more than one connection is registered; if omitted and exactly one connection is registered, that connection is used.")] string? connectionName = null,
        [Description("Simple name or DLL path of the assembly containing the migrations, when they live in a different assembly than the DbContext type. Omit when migrations are in the same assembly as the DbContext (the default). A simple name is resolved as a dependency of the currently loaded target assembly; a path is loaded explicitly, subject to the same AssemblyLoader:AllowedRoots restriction as load_assembly.")] string? migrationsAssembly = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync("list_migrations", () => ListMigrationsCore(contextName, connectionName, migrationsAssembly, cancellationToken));

    private async Task<string> ListMigrationsCore(string contextName, string? connectionName, string? migrationsAssembly, CancellationToken cancellationToken)
    {
        var entry = ResolveConnection(connectionName);
        var contextType = ResolveContextType(contextName, entry);
        if (entry.IsProduction)
        {
            throw new McpException("Migration inspection is not permitted for production connections.");
        }

        var resolvedMigrationsAssembly = ResolveMigrationsAssembly(migrationsAssembly);
        var provider = ResolveEffectiveProvider(contextType, entry);
        using var context = CreateContext(contextType, entry, resolvedMigrationsAssembly);
        try
        {
            var result = await migrationInspector.InspectAsync(context, entry, provider, cancellationToken);
            return resultFormatter.Format(new
            {
                contextName = contextType.Name,
                connectionName = entry.Name,
                appliedMigrations = result.AppliedMigrations,
                pendingMigrations = result.PendingMigrations,
                databaseExists = result.DatabaseExists,
                appliedStateAvailable = result.AppliedStateAvailable,
            });
        }
        catch (MigrationInspectionException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "generate_migration_script"), Description(
        "Generates a preview SQL migration script between two migration IDs via IMigrator.GenerateScript. This " +
        "is a preview only: the script is never executed, no transaction is opened, and the database is never " +
        "mutated. Unavailable unless Migrations:Enabled is explicitly set to true on the server; always rejected " +
        "for Production and ReadOnly connections. The generated SQL is capped server-side and truncated at a " +
        "best-effort statement boundary when it exceeds the cap.")]
    public Task<string> GenerateMigrationScript(
        [Description("CLR type name of the DbContext, as returned by list_contexts.")] string contextName,
        [Description("Logical connection name from the server's connection registry. Required whenever more than one connection is registered; if omitted and exactly one connection is registered, that connection is used.")] string? connectionName = null,
        [Description("Migration ID to script from (exclusive). Omit or pass \"0\" to script from the beginning of history.")] string? fromMigration = null,
        [Description("Migration ID to script to (inclusive). Omit to script through the latest known migration.")] string? toMigration = null,
        [Description("If true (default), generate a script safe to run on an already-applied database (__EFMigrationsHistory-guarded). Not every provider supports idempotent scripts.")] bool idempotent = true,
        [Description("Simple name or DLL path of the assembly containing the migrations, when they live in a different assembly than the DbContext type. Omit when migrations are in the same assembly as the DbContext (the default). A simple name is resolved as a dependency of the currently loaded target assembly; a path is loaded explicitly, subject to the same AssemblyLoader:AllowedRoots restriction as load_assembly.")] string? migrationsAssembly = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync("generate_migration_script", () => GenerateMigrationScriptCore(contextName, connectionName, fromMigration, toMigration, idempotent, migrationsAssembly, cancellationToken));

    private async Task<string> GenerateMigrationScriptCore(string contextName, string? connectionName, string? fromMigration, string? toMigration, bool idempotent, string? migrationsAssembly, CancellationToken cancellationToken)
    {
        if (!migrationsOptions.Enabled)
        {
            throw new McpException(
                "Migration script generation is disabled by default as a safety guard. To enable it, set " +
                "Migrations:Enabled to true in the server's configuration (appsettings.json, an environment " +
                "variable such as DOTNETEFCOREMCP_Migrations__Enabled=true, or user-secrets) and restart the MCP " +
                "server process - this cannot be toggled per-request or per-session from an MCP client. Even " +
                "when enabled, generate_migration_script still refuses Production and ReadOnly connections, so " +
                "consider whether list_migrations (always-on, read-only inspection) already covers your need " +
                "before enabling script generation.");
        }

        var entry = ResolveConnection(connectionName);
        var contextType = ResolveContextType(contextName, entry);
        if (entry.IsProduction)
        {
            throw new McpException("Migration script generation is not permitted for production connections.");
        }

        if (entry.AccessMode != ConnectionAccessMode.ReadWrite)
        {
            throw new McpException("Migration script generation requires a ReadWrite connection.");
        }

        var resolvedMigrationsAssembly = ResolveMigrationsAssembly(migrationsAssembly);
        using var context = CreateContext(contextType, entry, resolvedMigrationsAssembly);
        try
        {
            var request = new MigrationScriptRequest { FromMigration = fromMigration, ToMigration = toMigration, Idempotent = idempotent };
            var result = await migrationInspector.GenerateScriptAsync(context, entry, request, cancellationToken);
            return resultFormatter.Format(new
            {
                contextName = contextType.Name,
                connectionName = entry.Name,
                fromMigration,
                toMigration,
                idempotent,
                sql = result.Sql,
                truncated = result.Truncated,
                migrationCount = result.MigrationCount,
            });
        }
        catch (MigrationInspectionException ex)
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

    [McpServerTool(Name = "list_loaded_assemblies"), Description(
        "Lists every target assembly currently registered on the server, including each target's logical name, " +
        "source path, load timestamp, and whether it is the current default target used when targetName is " +
        "omitted from load_assembly/list_contexts/get_schema/run_query/run_sql_query.")]
    public string ListLoadedAssemblies()
        => Execute("list_loaded_assemblies", ListLoadedAssembliesCore);

    private string ListLoadedAssembliesCore()
    {
        var targets = assemblyLoader.ListTargets();
        return resultFormatter.Format(new
        {
            defaultTargetName = assemblyLoader.CurrentDefaultTargetName == AssemblyLoaderService.DefaultTargetName
                ? null
                : assemblyLoader.CurrentDefaultTargetName,
            targets = targets.Select(t => new
            {
                targetName = t.Name == AssemblyLoaderService.DefaultTargetName ? null : t.Name,
                assemblyPath = t.Handle.AssemblyPath,
                loadedAtUtc = t.Handle.LoadedAtUtc,
                isDefault = t.IsDefault,
            }),
        });
    }

    [McpServerTool(Name = "select_target"), Description(
        "Selects which registered target assembly resolves as the default when load_assembly/list_contexts/" +
        "get_schema/run_query/run_sql_query calls omit targetName. Does not unload or otherwise affect any " +
        "other registered target.")]
    public string SelectTarget(
        [Description("Logical target name previously registered via load_assembly's targetName parameter.")] string targetName)
        => Execute("select_target", () => SelectTargetCore(targetName));

    private string SelectTargetCore(string targetName)
    {
        try
        {
            assemblyLoader.SelectDefault(targetName);
            return resultFormatter.Format(new
            {
                defaultTargetName = targetName,
            });
        }
        catch (UnknownAssemblyTargetException ex)
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
        catch (Exception ex) when (ex is AccessPolicyDeniedException or ConnectionRegistryConfigurationException)
        {
            // These carry deliberately non-disclosing, already-safe messages (P0 #9) - surface them
            // directly rather than routing through CreateUnexpectedToolException's opaque, generic
            // "failed unexpectedly" path, which would obscure the actionable denial/misconfiguration
            // reason without adding any further safety.
            throw new McpException(ex.Message);
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
        catch (Exception ex) when (ex is AccessPolicyDeniedException or ConnectionRegistryConfigurationException)
        {
            throw new McpException(ex.Message);
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

    private LoadedAssemblyHandle RequireLoadedAssembly(string? targetName = null)
    {
        try
        {
            return assemblyLoader.Get(targetName)
                ?? throw new McpException("No target assembly is loaded yet. Call load_assembly with the path to a compiled target project's DLL first.");
        }
        catch (UnknownAssemblyTargetException ex)
        {
            throw new McpException(ex.Message);
        }
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

    /// <summary>Resolves a DbContext by name within the currently loaded assembly. Requires the
    /// already-resolved <paramref name="entry"/> (see <see cref="ResolveConnection"/>) so that,
    /// on a no-match/ambiguous-match error, the disclosed list of "choose one of these" context
    /// names can be restricted to <paramref name="entry"/>'s <c>AccessPolicy</c>-reachable contexts
    /// only (mirroring <c>ListContextsCore</c>'s <c>visibleDescriptors</c> filter). Every call site
    /// therefore calls <see cref="ResolveConnection"/> before this method, not after, so a caller
    /// who passes a denied or nonexistent contextName is never told which real context names
    /// exist beyond what their connection's policy already permits them to see (P0 #9
    /// non-disclosure requirement). A denied-but-real match is still returned here and rejected
    /// uniformly afterward by <see cref="EnsureContextReachable"/>, so it produces the exact same
    /// outward message shape as a genuinely nonexistent name.</summary>
    private Type ResolveContextType(string? contextName, ConnectionRegistryEntry entry, string? targetName = null)
    {
        var handle = RequireLoadedAssembly(targetName);
        var scan = DbContextScanner.FindDbContextTypes(handle.Assembly);
        var contexts = scan.Descriptors;
        var visibleContexts = contexts.Where(c => entry.AccessPolicy.IsContextReachable(c.FullName)).ToArray();

        if (string.IsNullOrWhiteSpace(contextName))
        {
            if (contexts.Count == 1)
                return contexts[0].ClrType;

            throw new McpException(BuildContextSelectionError(
                visibleContexts,
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
        throw new McpException(BuildContextSelectionError(visibleContexts, reason));
    }

    private static string BuildContextSelectionError(IReadOnlyList<DbContextDescriptor> contexts, string reason)
    {
        var choices = contexts.Count == 0 ? "(none)" : string.Join(", ", contexts.Select(c => c.Name));
        return $"{reason} Choose one of these short context names: {choices}. Next step: call list_contexts, then pass contextName using a listed short name or fully qualified name.";
    }

    private static string FormatQueryError(QueryExecutionException exception)
    {
        var message = exception.Message;

        if (message.Contains("could not be compiled", StringComparison.OrdinalIgnoreCase))
        {
            return $"{message} Next step: Fix the reported compile error(s) in the query expression (Roslyn execution mode accepts full C# syntax rooted at a DbSet property) and retry.";
        }

        if (message.Contains("compilation timed out", StringComparison.OrdinalIgnoreCase))
        {
            return $"{message} Next step: Simplify the query or increase the server's QueryExecution:CompileTimeoutSeconds setting, then retry.";
        }

        if (message.Contains("cannot be used with the Roslyn query engine", StringComparison.OrdinalIgnoreCase))
        {
            return $"{message} Next step: Add a public parameterless constructor or a DbContextOptions<T> constructor to the DbContext.";
        }

        if (message.Contains("is not an IQueryable and has no SQL to preview", StringComparison.OrdinalIgnoreCase))
        {
            return $"{message} Next step: Rewrite the query so it ends on a translatable IQueryable operator (e.g. Where/Select/OrderBy), or call run_query instead if you actually need the scalar/materialized result.";
        }

        if (message.Contains("QueryExecution:OutOfProcessHostPath", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("out-of-process query host was not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("out-of-process query host is missing its dependency file", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("runtime configuration file for out-of-process query execution", StringComparison.OrdinalIgnoreCase))
        {
            return $"{message} Next step: This is a server-side configuration problem with the out-of-process query host, not a problem with the query itself; ask the server operator to check the QueryExecution:OutOfProcessHostPath setting and the target project's build output.";
        }

        if (message.Contains("out-of-process query host failed to execute the query", StringComparison.OrdinalIgnoreCase))
        {
            return $"{message} Next step: The query host process exited unexpectedly while executing the query; the detail above (if any) is the host's captured stderr - consult server logs for the full stack trace if it is not conclusive.";
        }

        if (message.Contains("out-of-process query host returned an invalid response", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("out-of-process query host did not return a result", StringComparison.OrdinalIgnoreCase))
        {
            return $"{message} Next step: This indicates a protocol-level failure between the server and the query host rather than a problem with the query; consult server logs and retry.";
        }

        var cause = exception.InnerException?.Message?.Trim();
        if (string.IsNullOrEmpty(cause))
            return $"{message} Next step: {GenericQueryRecoveryHint}";

        if (cause.Contains("RowLimitingOperationWithoutOrderByWarning", StringComparison.OrdinalIgnoreCase) ||
            (cause.Contains("Skip", StringComparison.OrdinalIgnoreCase) && cause.Contains("Take", StringComparison.OrdinalIgnoreCase)))
        {
            return $"{message} Cause: {cause} Next step: Add a deterministic orderBy expression whenever using skip or take, then retry the query.";
        }

        if (cause.Contains("Globalization Invariant Mode is not supported", StringComparison.OrdinalIgnoreCase))
        {
            return $"{message} Cause: {cause} Next step: Enable ICU/globalization support in the target .NET runtime or container, then retry the query.";
        }

        return $"{message} Next step: {GenericQueryRecoveryHint}";
    }

    private const string GenericQueryRecoveryHint =
        "verify entity and property names with get_schema, validate LINQ query syntax, and consult server logs if the problem persists.";

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
        ConnectionRegistryEntry entry;
        try
        {
            if (string.IsNullOrWhiteSpace(connectionName))
            {
                // Mirrors ResolveContextType's disambiguation behavior: silently resolving to
                // "whichever connection happens to be active" is safe only when there is exactly
                // one candidate. With two or more registered connections, guessing risks silently
                // running against the wrong database/environment, so require an explicit choice.
                if (connectionRegistry.ConnectionNames.Count > 1)
                {
                    throw new McpException(BuildConnectionSelectionError(
                        connectionRegistry.ConnectionNames,
                        "`connectionName` is required because more than one connection is registered."));
                }

                var active = connectionRegistry.ActiveConnection;
                if (active is null)
                {
                    throw new McpException("No connection is active yet. Call swap_connection, or pass an explicit connectionName. (Connections available via list_connections.)");
                }
                entry = active;
            }
            else
            {
                entry = connectionRegistry.Get(connectionName);
            }
        }
        catch (UnknownConnectionException ex)
        {
            throw new McpException(ex.Message);
        }

        // AccessPolicy selectors can only be checked against the actual discovered model once a
        // target assembly is loaded (see ConnectionRegistry.LoadAccessPolicy); every tool that
        // resolves a connection to use it does so through here, so this is the single place that
        // enforces "reject invalid policy before serving the connection" for that deferred check.
        var handle = assemblyLoader.Current;
        if (handle is not null)
        {
            var scan = DbContextScanner.FindDbContextTypes(handle.Assembly);
            entry.AccessPolicy.EnsureResolvable(entry.Name, scan.Descriptors);
        }

        return entry;
    }

    private static string BuildConnectionSelectionError(IReadOnlyCollection<string> connectionNames, string reason)
    {
        var choices = connectionNames.Count == 0
            ? "(none)"
            : string.Join(", ", connectionNames.OrderBy(name => name, StringComparer.Ordinal));
        return $"{reason} Choose one of these connection names: {choices}. Next step: call list_connections, then pass connectionName using a listed name.";
    }

    private static Microsoft.EntityFrameworkCore.DbContext CreateContext(Type contextType, ConnectionRegistryEntry entry, System.Reflection.Assembly? migrationsAssembly = null)
    {
        EnsureContextReachable(contextType, entry);

        var provider = ResolveEffectiveProvider(contextType, entry);
        try
        {
            return DbContextActivator.CreateInstance(contextType, entry, provider, migrationsAssembly);
        }
        catch (DbContextActivationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    /// <summary>Resolves the optional <c>migrationsAssembly</c> tool parameter (a simple assembly
    /// name or a path to a compiled DLL) into a loaded <see cref="System.Reflection.Assembly"/>
    /// suitable for <see cref="DbContextActivator.CreateInstance"/>'s <c>migrationsAssembly</c>
    /// parameter - used by <see cref="ListMigrations"/> and
    /// <see cref="GenerateMigrationScript"/> to support migrations that live in a different
    /// assembly than the <see cref="Microsoft.EntityFrameworkCore.DbContext"/> type. Returns
    /// <c>null</c> unchanged when <paramref name="migrationsAssembly"/> is omitted, preserving the
    /// existing same-assembly behavior.</summary>
    private System.Reflection.Assembly? ResolveMigrationsAssembly(string? migrationsAssembly)
    {
        if (string.IsNullOrWhiteSpace(migrationsAssembly))
        {
            return null;
        }

        var handle = RequireLoadedAssembly();
        try
        {
            return assemblyLoader.ResolveMigrationsAssembly(handle, migrationsAssembly);
        }
        catch (AssemblyLoadFailedException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    /// <summary>Enforces the connection's <see cref="ConnectionAccessPolicy"/> at the context level
    /// before a <c>DbContext</c> is constructed or a query is parsed against it (P0 #9). Denied and
    /// unreachable contexts are rejected through the same <see cref="AccessPolicyDeniedException"/>
    /// path so callers cannot distinguish "denied" from "does not exist".</summary>
    private static void EnsureContextReachable(Type contextType, ConnectionRegistryEntry entry)
    {
        var contextFullName = contextType.FullName ?? contextType.Name;
        if (!entry.AccessPolicy.IsContextReachable(contextFullName))
        {
            throw AccessPolicyDeniedException.ForContext(entry.Name, contextFullName);
        }
    }

    /// <summary>Enforces the connection's <see cref="ConnectionAccessPolicy"/> at the entity level
    /// (P0 #9), in addition to the context-level check already performed by
    /// <see cref="EnsureContextReachable"/>. Used wherever a single target entity/DbSet name is known
    /// before a query is parsed or a mutation is executed.</summary>
    private static void EnsureEntityAllowed(Type contextType, ConnectionRegistryEntry entry, string entityName)
    {
        var contextFullName = contextType.FullName ?? contextType.Name;
        if (!entry.AccessPolicy.IsEntityAllowed(contextFullName, entityName))
        {
            throw AccessPolicyDeniedException.ForEntity(entry.Name, contextFullName, entityName);
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
        [Description("Logical connection name from the server's connection registry. Required whenever more than one connection is registered; if omitted and exactly one connection is registered, that connection is used.")] string? connectionName = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync("test_connection", () => TestConnectionCore(contextName, connectionName, cancellationToken));

    private async Task<string> TestConnectionCore(string contextName, string? connectionName, CancellationToken cancellationToken)
    {
        var entry = ResolveConnection(connectionName);
        var contextType = ResolveContextType(contextName, entry);
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

    [McpServerTool(Name = "insert_entity"), Description(
        "Inserts one metadata-validated entity into a Development ReadWrite connection. This destructive tool is " +
        "disabled unless EntityMutations:Enabled is explicitly true; it always rejects Production and ReadOnly connections.")]
    public Task<string> InsertEntity(
        [Description("CLR type name of the DbContext, as returned by list_contexts.")] string contextName,
        [Description("CLR type name of the entity to insert.")] string entity,
        [Description("Values for writable scalar properties.")] Dictionary<string, JsonElement> values,
        [Description("Logical connection name from the server registry. Required whenever more than one connection is registered; if omitted and exactly one connection is registered, that connection is used.")] string? connectionName = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync("insert_entity", () => MutateEntityCore(contextName, entity, EntityMutationOperation.Insert, null, values, null, connectionName, cancellationToken));

    [McpServerTool(Name = "update_entity"), Description(
        "Updates one metadata-validated entity in a Development ReadWrite connection. This destructive tool is " +
        "disabled unless EntityMutations:Enabled is explicitly true; it always rejects Production and ReadOnly connections.")]
    public Task<string> UpdateEntity(
        [Description("CLR type name of the DbContext, as returned by list_contexts.")] string contextName,
        [Description("CLR type name of the entity to update.")] string entity,
        [Description("Complete primary-key values by exact property name.")] Dictionary<string, JsonElement> key,
        [Description("Non-empty values for writable scalar properties to change.")] Dictionary<string, JsonElement> values,
        [Description("Original values for every concurrency-token property, when required.")] Dictionary<string, JsonElement>? concurrency = null,
        [Description("Logical connection name from the server registry. Required whenever more than one connection is registered; if omitted and exactly one connection is registered, that connection is used.")] string? connectionName = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync("update_entity", () => MutateEntityCore(contextName, entity, EntityMutationOperation.Update, key, values, concurrency, connectionName, cancellationToken));

    [McpServerTool(Name = "delete_entity"), Description(
        "Deletes one metadata-validated entity from a Development ReadWrite connection. This destructive tool is " +
        "disabled unless EntityMutations:Enabled is explicitly true; it always rejects Production and ReadOnly connections.")]
    public Task<string> DeleteEntity(
        [Description("CLR type name of the DbContext, as returned by list_contexts.")] string contextName,
        [Description("CLR type name of the entity to delete.")] string entity,
        [Description("Complete primary-key values by exact property name.")] Dictionary<string, JsonElement> key,
        [Description("Original values for every concurrency-token property, when required.")] Dictionary<string, JsonElement>? concurrency = null,
        [Description("Logical connection name from the server registry. Required whenever more than one connection is registered; if omitted and exactly one connection is registered, that connection is used.")] string? connectionName = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync("delete_entity", () => MutateEntityCore(contextName, entity, EntityMutationOperation.Delete, key, null, concurrency, connectionName, cancellationToken));

    private async Task<string> MutateEntityCore(
        string contextName,
        string entity,
        EntityMutationOperation operation,
        Dictionary<string, JsonElement>? key,
        Dictionary<string, JsonElement>? values,
        Dictionary<string, JsonElement>? concurrency,
        string? connectionName,
        CancellationToken cancellationToken)
    {
        if (!entityMutationsOptions.Enabled)
        {
            throw new McpException("Entity mutations are disabled by default as a safety guard. To enable them, set EntityMutations:Enabled to true in the server's configuration and restart the MCP server process. Even when enabled, entity mutations refuse Production connections and require a ReadWrite connection.");
        }

        var entry = ResolveConnection(connectionName);
        var contextType = ResolveContextType(contextName, entry);
        if (entry.IsProduction)
        {
            throw new McpException("Entity mutations are not permitted for production connections.");
        }
        if (entry.AccessMode != ConnectionAccessMode.ReadWrite)
        {
            throw new McpException("Entity mutations require a ReadWrite connection.");
        }

        EnsureEntityAllowed(contextType, entry, entity);

        using var context = CreateContext(contextType, entry);
        try
        {
            var result = await entityMutationExecutor.ExecuteAsync(
                context,
                new EntityMutationRequest(operation, entity, key, values, concurrency),
                cancellationToken);
            if (result.IsConflict)
            {
                return resultFormatter.Format(new
                {
                    contextName,
                    connectionName = entry.Name,
                    entity = result.Entity,
                    operation = "not-found-or-concurrency-conflict",
                    affectedRows = 0
                });
            }

            return resultFormatter.Format(new
            {
                contextName,
                connectionName = entry.Name,
                entity = result.Entity,
                operation = result.Operation,
                affectedRows = result.AffectedRows,
                values = result.Values
            });
        }
        catch (MutationExecutionException ex) when (ex.IsConflict)
        {
            return resultFormatter.Format(new
            {
                contextName,
                connectionName = entry.Name,
                entity,
                operation = "not-found-or-concurrency-conflict",
                affectedRows = 0
            });
        }
        catch (MutationExecutionException ex)
        {
            throw new McpException($"{ex.Message} Next step: correct the request using the DbContext schema and retry.");
        }
    }
}
