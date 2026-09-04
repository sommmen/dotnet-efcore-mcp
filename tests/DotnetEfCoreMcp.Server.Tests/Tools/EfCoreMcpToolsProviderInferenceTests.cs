using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Connections;
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

/// <summary>End-to-end coverage of provider inference through the actual MCP tool surface: a
/// connection configured without a <c>Provider</c> should still work against the SampleApp fixture
/// (which references only the SQLite EF Core provider), and a clearly invalid connection name
/// should still surface as a normal <see cref="McpException"/>.</summary>
public sealed class EfCoreMcpToolsProviderInferenceTests
{
    private static EfCoreMcpTools CreateTools(
        SqliteTestDatabase db,
        out AssemblyLoaderService assemblyLoader,
        RawSqlExecutionOptions? rawSqlOptions = null,
        EntityMutationsOptions? entityMutationsOptions = null,
        bool readWrite = false,
        bool production = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Connections:Inferred:ConnectionString"] = db.ConnectionString,
                ["Connections:Inferred:AccessMode"] = readWrite ? "ReadWrite" : "ReadOnly",
                ["Connections:Inferred:Environment"] = production ? "Production" : "Development",
                ["Connections:Inferred:AccessPolicy:AllowContexts:0"] = "SampleApp.SampleAppDbContext",
            })
            .Build();

        var resolvedRawSqlOptions = rawSqlOptions ?? new RawSqlExecutionOptions();
        assemblyLoader = new AssemblyLoaderService();
        var tools = new EfCoreMcpTools(
            assemblyLoader,
            new AssemblyDiscoveryService(),
            new ConnectionRegistry(configuration),
            new SchemaCache(),
            new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance),
            new RoslynQueryExecutor(new QueryExecutionOptions(), new QueryCompiler(new QueryCompilationOptions())),
            new OutOfProcessRoslynQueryExecutor(new QueryExecutionOptions()),
            new QueryExecutionOptions(),
            resolvedRawSqlOptions,
            new SqlQueryExecutor(resolvedRawSqlOptions, NullLogger<SqlQueryExecutor>.Instance),
            new MigrationsOptions(),
            new MigrationInspector(new MigrationsOptions(), NullLogger<MigrationInspector>.Instance),
            new ToonToolResultFormatter(),
            new ToolDiagnosticsOptions(),
            NullLogger<EfCoreMcpTools>.Instance,
            entityMutationsOptions ?? new EntityMutationsOptions(),
            new EntityMutationExecutor(NullLogger<EntityMutationExecutor>.Instance));
        return tools;
    }

    [Fact]
    public void GetSchema_ConnectionWithoutExplicitProvider_InfersSqliteFromLoadedAssembly()
    {
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(db, out var assemblyLoader);
        assemblyLoader.Load(FixturePaths.SampleAppDllPath);

        var output = tools.GetSchema("SampleAppDbContext", "Inferred");

        Assert.Contains("entities[", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"entities\"", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListConnections_ConnectionWithoutExplicitProvider_ReportsInferredMarker()
    {
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(db, out _);

        var json = tools.ListConnections();

        Assert.Contains("(inferred)", json);
    }

    [Theory]
    [InlineData("insert")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task EntityMutation_WhenDisabled_RejectsBeforeQuerying(string operation)
    {
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(db, out var assemblyLoader);
        assemblyLoader.Load(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(() => operation switch
        {
            "insert" => tools.InsertEntity("SampleAppDbContext", "Customer", Values("Name", "Ada", "Age", 37)),
            "update" => tools.UpdateEntity("SampleAppDbContext", "Customer", Values("Id", 1), Values("Age", 38)),
            _ => tools.DeleteEntity("SampleAppDbContext", "Customer", Values("Id", 1))
        });

        Assert.Contains("disabled", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EntityMutations:Enabled", exception.Message, StringComparison.Ordinal);
    }

    private static Dictionary<string, System.Text.Json.JsonElement> Values(params object[] pairs)
    {
        var values = new Dictionary<string, System.Text.Json.JsonElement>();
        for (var index = 0; index < pairs.Length; index += 2)
        {
            values.Add((string)pairs[index], System.Text.Json.JsonSerializer.SerializeToElement(pairs[index + 1]));
        }
        return values;
    }

    [Fact]
    public async Task RunSqlQuery_WhenDisabled_RejectsBeforeQuerying()
    {
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(db, out var assemblyLoader);
        assemblyLoader.Load(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(() => tools.RunSqlQuery("SampleAppDbContext", "SELECT 1", "Inferred"));

        Assert.Contains("disabled", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RawSqlExecution:Enabled", exception.Message, StringComparison.Ordinal);
        Assert.Contains("restart", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunSqlQuery_WithReadOnlyConnection_RejectsEvenWhenEnabled()
    {
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(db, out var assemblyLoader, new RawSqlExecutionOptions { Enabled = true });
        assemblyLoader.Load(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(() => tools.RunSqlQuery("SampleAppDbContext", "SELECT 1", "Inferred"));

        Assert.Contains("ReadWrite", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunSqlQuery_WithProductionConnection_RejectsEvenWhenConfiguredReadWrite()
    {
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(db, out var assemblyLoader, new RawSqlExecutionOptions { Enabled = true }, readWrite: true, production: true);
        assemblyLoader.Load(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(() => tools.RunSqlQuery("SampleAppDbContext", "SELECT 1", "Inferred"));

        Assert.Contains("production", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
