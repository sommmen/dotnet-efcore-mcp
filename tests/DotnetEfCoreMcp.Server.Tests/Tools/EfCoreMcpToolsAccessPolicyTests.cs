using System.Text.Json;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Migrations;
using DotnetEfCoreMcp.Server.Mutations;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Schema;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using DotnetEfCoreMcp.Server.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace DotnetEfCoreMcp.Server.Tests.Tools;

/// <summary>End-to-end enforcement coverage for the P0 #9 per-connection <c>AccessPolicy</c> across
/// the MCP tool surface: denied/unknown contexts and entities are rejected before a
/// <c>DbContext</c> is constructed or a query is parsed, `list_contexts`/`get_schema`/
/// `get_entity_schema`/`search_schema` never disclose a denied name, and a denied request is
/// indistinguishable from a request for a name that does not exist in the model at all.</summary>
public sealed class EfCoreMcpToolsAccessPolicyTests
{
    [Fact]
    public void ListContexts_FiltersOutContextsNotReachableByTheActiveConnectionsPolicy()
    {
        var tools = CreateTools(allowContexts: ["SampleApp.SampleAppDbContext"]);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        using var document = JsonDocument.Parse(tools.ListContexts());
        var names = document.RootElement.GetProperty("contexts")
            .EnumerateArray()
            .Select(c => c.GetProperty("name").GetString())
            .ToArray();

        Assert.Contains("SampleAppDbContext", names);
        Assert.DoesNotContain("FactoryOnlyDbContext", names);
    }

    [Fact]
    public void ListContexts_WithNoActiveConnection_DoesNotFilterAndDoesNotThrow()
    {
        // No Connections: section at all means no active connection - list_contexts must still
        // succeed unfiltered (discovering contexts is a prerequisite for picking a connection).
        var configuration = new ConfigurationBuilder().Build();
        var tools = CreateToolsWithConfiguration(configuration);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        using var document = JsonDocument.Parse(tools.ListContexts());
        var names = document.RootElement.GetProperty("contexts")
            .EnumerateArray()
            .Select(c => c.GetProperty("name").GetString())
            .ToArray();

        Assert.Contains("SampleAppDbContext", names);
        Assert.Contains("FactoryOnlyDbContext", names);
    }

    [Fact]
    public void GetSchema_DeniedContext_RejectsBeforeConstructingTheContext()
    {
        var tools = CreateTools(allowContexts: ["SampleApp.FactoryOnlyDbContext"]);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = Assert.Throws<McpException>(() => tools.GetSchema("SampleAppDbContext"));

        Assert.Contains("not permitted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetEntitySchema_DeniedEntity_RejectsAndDoesNotDiscloseKnownEntities()
    {
        var tools = CreateTools(allowEntities: [new EntitySelector("SampleApp.SampleAppDbContext", "Order")]);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        tools.GetSchema("SampleAppDbContext");

        var exception = Assert.Throws<McpException>(() => tools.GetEntitySchema("Customer", "SampleAppDbContext"));

        // The "known entities" hint must be drawn from the policy-filtered view only - "Order" (the
        // sole allowed entity) may appear, but no other real entity name from the underlying model.
        Assert.Contains("Known entities: Order.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetEntitySchema_DeniedEntityLooksIdenticalToAnUnknownEntity()
    {
        var tools = CreateTools(allowEntities: [new EntitySelector("SampleApp.SampleAppDbContext", "Order")]);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        tools.GetSchema("SampleAppDbContext");

        // "Customer" is a real, denied entity; "TotallyMadeUp" does not exist in the model at all.
        // Both must fail with the identical "Known entities" suffix - the only allowed entity, and
        // nothing that reveals whether the requested name genuinely exists in the model.
        var deniedException = Assert.Throws<McpException>(() => tools.GetEntitySchema("Customer", "SampleAppDbContext"));
        var unknownException = Assert.Throws<McpException>(() => tools.GetEntitySchema("TotallyMadeUp", "SampleAppDbContext"));

        Assert.Contains("Known entities: Order.", deniedException.Message, StringComparison.Ordinal);
        Assert.Contains("Known entities: Order.", unknownException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetEntitySchema_AllowedEntity_Succeeds()
    {
        var tools = CreateTools(
            allowContexts: ["SampleApp.SampleAppDbContext"],
            allowEntities: [new EntitySelector("SampleApp.SampleAppDbContext", "Order")]);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        tools.GetSchema("SampleAppDbContext");

        using var document = JsonDocument.Parse(tools.GetEntitySchema("Order", "SampleAppDbContext"));

        Assert.Equal("Order", document.RootElement.GetProperty("entity").GetProperty("Name").GetString());
    }

    [Fact]
    public void SearchSchema_OnlyReturnsMatchesForAllowedEntities()
    {
        var tools = CreateTools(allowEntities: [new EntitySelector("SampleApp.SampleAppDbContext", "Order")]);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        tools.GetSchema("SampleAppDbContext");

        // "Age" only appears on the denied Customer entity, not on Order (Id/CustomerId/Customer/
        // Amount/CreatedAtUtc) - a match here would mean Customer leaked into the visible schema.
        using var document = JsonDocument.Parse(tools.SearchSchema("SampleAppDbContext", "Age"));

        Assert.Equal(0, document.RootElement.GetProperty("totalMatchCount").GetInt32());
    }

    [Fact]
    public void SearchSchema_DeniedContext_RejectsBeforeSearching()
    {
        var tools = CreateTools(allowContexts: ["SampleApp.FactoryOnlyDbContext"]);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = Assert.Throws<McpException>(() => tools.SearchSchema("SampleAppDbContext", "Customer"));

        Assert.Contains("not permitted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunQuery_DeniedContext_RejectsBeforeParsingTheQuery()
    {
        var tools = CreateTools(allowContexts: ["SampleApp.FactoryOnlyDbContext"]);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.RunQuery("SampleAppDbContext", "Customers.Select(c => c.Name)"));

        Assert.Contains("not permitted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunQuery_DeniedRootEntity_RejectsBeforeExecutingAgainstTheDatabase()
    {
        var tools = CreateTools(allowEntities: [new EntitySelector("SampleApp.SampleAppDbContext", "Order")]);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.RunQuery("SampleAppDbContext", "Customers.Select(c => c.Name)"));

        Assert.Contains("Customer", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not permitted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InsertEntity_DeniedEntity_RejectsBeforeConstructingTheContext()
    {
        var tools = CreateTools(
            allowEntities: [new EntitySelector("SampleApp.SampleAppDbContext", "Order")],
            entityMutationsOptions: new EntityMutationsOptions { Enabled = true },
            readWrite: true);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.InsertEntity("SampleAppDbContext", "Customer", Values("Name", "Ada", "Age", 37)));

        Assert.Contains("not permitted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSchema_AllowedContextViaEntityLevelAllowOnly_Succeeds()
    {
        // A narrower entity-level allow (no blanket context allow) still makes the context
        // reachable for get_schema, per IsContextReachable semantics.
        var tools = CreateTools(allowEntities: [new EntitySelector("SampleApp.SampleAppDbContext", "Order")]);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        using var document = JsonDocument.Parse(tools.GetSchema("SampleAppDbContext"));

        // The schema itself is not filtered by get_schema page contents beyond entity-level policy
        // - only the entities actually permitted show up when paged through fully.
        Assert.True(document.RootElement.GetProperty("totalEntityCount").GetInt32() >= 1);
    }

    [Fact]
    public void GetSchema_MissingContextName_ChoiceListNeverDisclosesADeniedButRealContext()
    {
        // Regression for a review finding where ResolveContextType built its "choose one of these"
        // hint from every DbContext discovered in the assembly - including ones the connection's
        // AccessPolicy denies - before the policy was ever consulted. The hint must be built from
        // only the policy-reachable contexts, so a denied-but-real context name never leaks to a
        // caller who omitted `contextName`.
        var tools = CreateTools(allowContexts: ["SampleApp.SampleAppDbContext"]);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        // The fixture assembly exposes more than one DbContext, so omitting contextName forces the
        // "multiple DbContexts" selection-error path that lists candidates.
        var exception = Assert.Throws<McpException>(() => tools.GetSchema());

        Assert.Contains("SampleAppDbContext", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("FactoryOnlyDbContext", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunQuery_AllowedEntityReferencedOnlyByItsDbSetName_Succeeds()
    {
        // Regression for a review finding where RunQueryCore checked the DbSet property name
        // ("Customers") against the AccessPolicy instead of the resolved entity name ("Customer"),
        // so a policy naming the entity by its actual EF entity name (as EntitySelector requires)
        // would incorrectly deny a query against its own DbSet.
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(
            allowEntities: [new EntitySelector("SampleApp.SampleAppDbContext", "Customer")],
            connectionString: db.ConnectionString);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        // Ensure the schema exists before the tool tries to execute a real query against it.
        var handle = new AssemblyLoaderService().Load(FixturePaths.SampleAppDllPath);
        var contextType = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors
            .Single(d => d.Name == "SampleAppDbContext").ClrType;
        using (var setupContext = DbContextActivator.CreateInstance(contextType, db.ToRegistryEntry(), DatabaseProvider.Sqlite))
        {
            setupContext.Database.EnsureCreated();
        }

        var result = await tools.RunQuery("SampleAppDbContext", "Customers.Select(c => c.Name)");

        Assert.DoesNotContain("not permitted", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunQuery_DeniedEntityReferencedViaUnion_RejectsBeforeExecutingAgainstTheDatabase()
    {
        // Regression for the same DbSet-name-vs-entity-name mismatch, exercised through the
        // "other referenced DbSet" path (QueryExecutor.ResolveReferencedEntityNames): the root
        // entity ("Order") is allowed, but the query also references the "Customers" DbSet via
        // Union, whose entity ("Customer") is denied - this must be rejected before any query
        // executes, using the entity name rather than the DbSet name.
        var tools = CreateTools(allowEntities: [new EntitySelector("SampleApp.SampleAppDbContext", "Order")]);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.RunQuery("SampleAppDbContext", "Orders.Select(o => o.Id).Union(Customers.Select(c => c.Id))"));

        Assert.Contains("Customer", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not permitted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveConnection_UnresolvableAccessPolicySelector_ThrowsConfigurationErrorNotDenial()
    {
        // A configured selector that cannot resolve against the actual loaded model is a server
        // misconfiguration (P0 #9 "reject invalid policy before serving the connection"), distinct
        // from a runtime denial.
        var tools = CreateTools(allowContexts: ["SampleApp.DoesNotExistDbContext"]);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = Assert.Throws<McpException>(() => tools.GetSchema("SampleAppDbContext"));

        Assert.Contains("does not resolve", exception.Message, StringComparison.Ordinal);
    }

    private static Dictionary<string, JsonElement> Values(params object[] pairs)
    {
        var values = new Dictionary<string, JsonElement>();
        for (var index = 0; index < pairs.Length; index += 2)
        {
            values.Add((string)pairs[index], JsonSerializer.SerializeToElement(pairs[index + 1]));
        }
        return values;
    }

    private static EfCoreMcpTools CreateTools(
        IReadOnlyList<string>? allowContexts = null,
        IReadOnlyList<EntitySelector>? allowEntities = null,
        EntityMutationsOptions? entityMutationsOptions = null,
        bool readWrite = false,
        string? connectionString = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Connections:PolicyTests:ConnectionString"] = connectionString ?? "Data Source=:memory:",
            ["Connections:PolicyTests:Provider"] = "Sqlite",
            ["Connections:PolicyTests:AccessMode"] = readWrite ? "ReadWrite" : "ReadOnly",
            ["Connections:PolicyTests:Environment"] = "Development",
        };
        for (var i = 0; i < (allowContexts?.Count ?? 0); i++)
        {
            values[$"Connections:PolicyTests:AccessPolicy:AllowContexts:{i}"] = allowContexts![i];
        }
        for (var i = 0; i < (allowEntities?.Count ?? 0); i++)
        {
            values[$"Connections:PolicyTests:AccessPolicy:AllowEntities:{i}"] = allowEntities![i].ToString();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return CreateToolsWithConfiguration(configuration, entityMutationsOptions);
    }

    private static EfCoreMcpTools CreateToolsWithConfiguration(
        IConfiguration configuration,
        EntityMutationsOptions? entityMutationsOptions = null)
    {
        var rawSqlOptions = new RawSqlExecutionOptions();
        var queryExecutionOptions = new QueryExecutionOptions { Mode = QueryExecutionMode.InProcess };
        return new EfCoreMcpTools(
            new AssemblyLoaderService(),
            new AssemblyDiscoveryService(),
            new ConnectionRegistry(configuration),
            new SchemaCache(),
            new RoslynQueryExecutor(queryExecutionOptions, new QueryCompiler(new QueryCompilationOptions())),
            new OutOfProcessRoslynQueryExecutor(queryExecutionOptions),
            queryExecutionOptions,
            rawSqlOptions,
            new SqlQueryExecutor(rawSqlOptions, NullLogger<SqlQueryExecutor>.Instance),
            new MigrationsOptions(),
            new MigrationInspector(new MigrationsOptions(), NullLogger<MigrationInspector>.Instance),
            new JsonToolResultFormatter(),
            new ToolDiagnosticsOptions(),
            NullLogger<EfCoreMcpTools>.Instance,
            entityMutationsOptions ?? new EntityMutationsOptions(),
            new EntityMutationExecutor(NullLogger<EntityMutationExecutor>.Instance));
    }
}
