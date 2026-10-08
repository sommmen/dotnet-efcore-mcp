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

/// <summary>Tool-layer enforcement coverage for <see cref="ConnectionSource.ApplicationFactory"/>
/// connections (startup-derived connections, see docs/development/startup-derived-connections.md):
/// the target application's own <c>IDesignTimeDbContextFactory&lt;TContext&gt;</c>/startup logic must
/// never run inside the MCP server process, so every tool that would otherwise construct a
/// <c>DbContext</c> in-process (directly or via <see cref="EfCoreMcpTools"/>'s shared
/// <c>CreateContext</c> helper) must reject an <c>ApplicationFactory</c> connection before doing so.
/// <c>run_query</c> and <c>preview_query_sql</c> are the exceptions: both are permitted when
/// <c>QueryExecution:Mode</c> routes compilation and execution out-of-process (see
/// <see cref="DotnetEfCoreMcp.Server.Tests.Querying.OutOfProcessRoslynQueryExecutorTests"/> for the corresponding success paths).</summary>
[Collection(ApplicationFactoryEnvironmentCollection.Name)]
public sealed class EfCoreMcpToolsApplicationFactoryTests
{
    [Fact]
    public async Task RunQuery_ApplicationFactoryConnection_InProcessMode_ThrowsMcpException()
    {
        var tools = CreateTools(QueryExecutionMode.InProcess);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.RunQuery("ApplicationFactoryDbContext", "Customers.Select(c => c.Name)"));

        Assert.Contains("ApplicationFactory", exception.Message, StringComparison.Ordinal);
        Assert.Contains("OutOfProcess", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewQuerySql_ApplicationFactoryConnection_InProcessMode_ThrowsMcpException()
    {
        var tools = CreateTools(QueryExecutionMode.InProcess);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.PreviewQuerySql("ApplicationFactoryDbContext", "Customers.Select(c => c.Name)"));

        Assert.Contains("ApplicationFactory", exception.Message, StringComparison.Ordinal);
        Assert.Contains("OutOfProcess", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(QueryExecutionMode.OutOfProcess)]
    [InlineData(QueryExecutionMode.Pooled)]
    [InlineData(QueryExecutionMode.Auto)]
    public async Task PreviewQuerySql_ApplicationFactoryConnection_IsolatedModes_ReturnsSql(QueryExecutionMode mode)
    {
        using var db = new SqliteTestDatabase();
        var previous = Environment.GetEnvironmentVariable("DOTNET_EFCORE_MCP_APPLICATION_FACTORY_CONNECTION");
        Environment.SetEnvironmentVariable("DOTNET_EFCORE_MCP_APPLICATION_FACTORY_CONNECTION", db.ConnectionString);
        try
        {
            var tools = CreateTools(mode, outOfProcessHostPath: FixturePaths.QueryHostDllPath);
            tools.LoadAssembly(FixturePaths.SampleAppDllPath);

            var json = await tools.PreviewQuerySql("ApplicationFactoryDbContext", "Customers.Select(c => c.Name)");

            Assert.Contains("SELECT", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Customers", json, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_EFCORE_MCP_APPLICATION_FACTORY_CONNECTION", previous);
        }
    }

    [Fact]
    public void GetSchema_ApplicationFactoryConnection_ThrowsMcpExceptionBeforeConstructingContext()
    {
        var tools = CreateTools(QueryExecutionMode.OutOfProcess);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = Assert.Throws<McpException>(() => tools.GetSchema("ApplicationFactoryDbContext"));

        Assert.Contains("ApplicationFactory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetEntitySchema_ApplicationFactoryConnection_ThrowsMcpExceptionBeforeConstructingContext()
    {
        var tools = CreateTools(QueryExecutionMode.OutOfProcess);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = Assert.Throws<McpException>(() => tools.GetEntitySchema("Customer", "ApplicationFactoryDbContext"));

        Assert.Contains("ApplicationFactory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSqlQuery_ApplicationFactoryConnection_ThrowsMcpExceptionBeforeConstructingContext()
    {
        var tools = CreateTools(QueryExecutionMode.OutOfProcess, readWrite: true, rawSqlEnabled: true);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.RunSqlQuery("ApplicationFactoryDbContext", "SELECT 1"));

        Assert.Contains("ApplicationFactory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListMigrations_ApplicationFactoryConnection_ThrowsMcpExceptionBeforeConstructingContext()
    {
        var tools = CreateTools(QueryExecutionMode.OutOfProcess);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.ListMigrations("ApplicationFactoryDbContext"));

        Assert.Contains("ApplicationFactory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateMigrationScript_ApplicationFactoryConnection_ThrowsMcpExceptionBeforeConstructingContext()
    {
        var tools = CreateTools(QueryExecutionMode.OutOfProcess, readWrite: true);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.GenerateMigrationScript("ApplicationFactoryDbContext"));

        Assert.Contains("ApplicationFactory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ListConnections_ApplicationFactoryConnection_ReportsSourceWithoutConnectionString()
    {
        var tools = CreateTools(QueryExecutionMode.OutOfProcess);

        var json = tools.ListConnections();

        Assert.Contains("ApplicationFactory", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Data Source", json, StringComparison.Ordinal);
    }

    private static EfCoreMcpTools CreateTools(
        QueryExecutionMode mode,
        bool readWrite = false,
        bool rawSqlEnabled = false,
        string? outOfProcessHostPath = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Connections:PolicyTests:Source"] = "ApplicationFactory",
            ["Connections:PolicyTests:Provider"] = "Sqlite",
            ["Connections:PolicyTests:AccessMode"] = readWrite ? "ReadWrite" : "ReadOnly",
            ["Connections:PolicyTests:Environment"] = "Development",
            ["Connections:PolicyTests:AccessPolicy:AllowContexts:0"] = "SampleApp.ApplicationFactoryDbContext",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var rawSqlOptions = new RawSqlExecutionOptions { Enabled = rawSqlEnabled };
        var queryExecutionOptions = new QueryExecutionOptions { Mode = mode, OutOfProcessHostPath = outOfProcessHostPath };
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
            new MigrationsOptions { Enabled = true },
            new MigrationInspector(new MigrationsOptions { Enabled = true }, NullLogger<MigrationInspector>.Instance),
            new JsonToolResultFormatter(),
            new ToolDiagnosticsOptions(),
            NullLogger<EfCoreMcpTools>.Instance,
            new EntityMutationsOptions(),
            new EntityMutationExecutor(NullLogger<EntityMutationExecutor>.Instance));
    }
}
