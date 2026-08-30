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
    ConnectionRegistry connectionRegistry,
    SchemaCache schemaCache,
    QueryExecutor queryExecutor,
    ILogger<EfCoreMcpTools> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool(Name = "load_assembly"), Description(
        "Loads (or reloads) a compiled target .NET project's assembly (its bin/<Configuration>/<TFM>/*.dll output) " +
        "into an isolated, collectible AssemblyLoadContext, replacing any previously loaded assembly. " +
        "Call this before list_contexts/get_schema/run_query, or again after rebuilding the target project.")]
    public string LoadAssembly(
        [Description("Absolute or relative path to the target project's compiled assembly DLL.")] string assemblyPath)
    {
        try
        {
            var handle = assemblyLoader.Load(assemblyPath);
            var contexts = DbContextScanner.FindDbContextTypes(handle.Assembly);
            logger.LogInformation(
                "Loaded target assembly. Path={AssemblyPath} DbContextCount={DbContextCount}",
                handle.AssemblyPath, contexts.Count);
            return JsonSerializer.Serialize(new
            {
                loadedAssemblyPath = handle.AssemblyPath,
                loadedAtUtc = handle.LoadedAtUtc,
                discoveredDbContexts = contexts.Select(c => new { name = c.Name, fullName = c.FullName, constructionKind = c.ConstructionKind.ToString() }),
            }, JsonOptions);
        }
        catch (AssemblyLoadFailedException ex)
        {
            logger.LogWarning(ex, "Failed to load target assembly. Path={AssemblyPath}", assemblyPath);
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "list_contexts"), Description(
        "Lists the Microsoft.EntityFrameworkCore.DbContext-derived types discovered in the currently loaded target assembly.")]
    public string ListContexts()
    {
        var handle = RequireLoadedAssembly();
        var contexts = DbContextScanner.FindDbContextTypes(handle.Assembly);

        return JsonSerializer.Serialize(new
        {
            assemblyPath = handle.AssemblyPath,
            isStale = assemblyLoader.IsCurrentAssemblyStale(),
            contexts = contexts.Select(c => new
            {
                name = c.Name,
                fullName = c.FullName,
                constructionKind = c.ConstructionKind.ToString(),
            }),
        }, JsonOptions);
    }

    [McpServerTool(Name = "get_schema"), Description(
        "Returns the EF Core model (entities, properties, keys, foreign keys, navigations) for a DbContext " +
        "in the currently loaded target assembly, as discovered via reflection over the real compiled model.")]
    public string GetSchema(
        [Description("CLR type name of the DbContext, as returned by list_contexts.")] string contextName,
        [Description("Logical connection name from the server's connection registry, used only to construct the context; no query is executed against the database to build the schema.")] string connectionName)
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
        [Description("Logical connection name from the server's connection registry.")] string connectionName,
        [Description("CLR type name of the entity to query, as returned by get_schema.")] string entity,
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

    private LoadedAssemblyHandle RequireLoadedAssembly()
    {
        return assemblyLoader.Current
            ?? throw new McpException("No target assembly is loaded yet. Call load_assembly with the path to a compiled target project's DLL first.");
    }

    private Type ResolveContextType(string contextName)
    {
        var handle = RequireLoadedAssembly();
        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly)
            .FirstOrDefault(c => string.Equals(c.Name, contextName, StringComparison.Ordinal));

        if (descriptor is null)
        {
            var known = DbContextScanner.FindDbContextTypes(handle.Assembly).Select(c => c.Name);
            throw new McpException($"No DbContext named '{contextName}' was found in the currently loaded assembly. Known contexts: {string.Join(", ", known)}.");
        }

        return descriptor.ClrType;
    }

    private ConnectionRegistryEntry ResolveConnection(string connectionName)
    {
        try
        {
            return connectionRegistry.Get(connectionName);
        }
        catch (UnknownConnectionException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    private static Microsoft.EntityFrameworkCore.DbContext CreateContext(Type contextType, ConnectionRegistryEntry entry)
    {
        try
        {
            return DbContextActivator.CreateInstance(contextType, entry);
        }
        catch (DbContextActivationException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
