using System.Text.Json;
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

/// <summary>Covers <c>connectionName</c> resolution across MCP tools: when omitted with exactly
/// one registered connection it must resolve silently (unambiguous), but when omitted with two or
/// more registered connections it must throw a helpful disambiguation error listing every
/// registered connection name - mirroring the existing <c>contextName</c> disambiguation behavior
/// (see <see cref="EfCoreMcpToolsSchemaSelectionTests"/>) - rather than silently falling back to
/// whichever connection happens to be "active".</summary>
public sealed class EfCoreMcpToolsConnectionSelectionTests
{
    [Fact]
    public void GetSchema_WhenConnectionNameIsOmittedWithMultipleConnections_ListsConnectionNames()
    {
        var tools = CreateTools(new Dictionary<string, string?>
        {
            ["Connections:Alpha:ConnectionString"] = "Data Source=:memory:",
            ["Connections:Alpha:Provider"] = "Sqlite",
            ["Connections:Alpha:Environment"] = "Development",
            ["Connections:Alpha:AccessPolicy:AllowContexts:0"] = "SampleApp.SampleAppDbContext",
            ["Connections:Beta:ConnectionString"] = "Data Source=:memory:",
            ["Connections:Beta:Provider"] = "Sqlite",
            ["Connections:Beta:Environment"] = "Development",
            ["Connections:Beta:AccessPolicy:AllowContexts:0"] = "SampleApp.SampleAppDbContext",
        });
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = Assert.Throws<McpException>(() => tools.GetSchema("SampleAppDbContext"));

        Assert.Contains("Alpha", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Beta", exception.Message, StringComparison.Ordinal);
        Assert.Contains("connectionName", exception.Message, StringComparison.Ordinal);
        Assert.Contains("list_connections", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunQuery_WhenConnectionNameIsOmittedWithMultipleConnections_ListsConnectionNames()
    {
        var tools = CreateTools(new Dictionary<string, string?>
        {
            ["Connections:Alpha:ConnectionString"] = "Data Source=:memory:",
            ["Connections:Alpha:Provider"] = "Sqlite",
            ["Connections:Alpha:Environment"] = "Development",
            ["Connections:Alpha:AccessPolicy:AllowContexts:0"] = "SampleApp.SampleAppDbContext",
            ["Connections:Beta:ConnectionString"] = "Data Source=:memory:",
            ["Connections:Beta:Provider"] = "Sqlite",
            ["Connections:Beta:Environment"] = "Development",
            ["Connections:Beta:AccessPolicy:AllowContexts:0"] = "SampleApp.SampleAppDbContext",
        });
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.RunQuery("SampleAppDbContext", "Customers.Select(c => c.Name)"));

        Assert.Contains("Alpha", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Beta", exception.Message, StringComparison.Ordinal);
        Assert.Contains("more than one connection is registered", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSchema_WhenConnectionNameIsOmittedWithExactlyOneConnection_ResolvesSilently()
    {
        var tools = CreateTools(new Dictionary<string, string?>
        {
            ["Connections:Only:ConnectionString"] = "Data Source=:memory:",
            ["Connections:Only:Provider"] = "Sqlite",
            ["Connections:Only:Environment"] = "Development",
            ["Connections:Only:AccessPolicy:AllowContexts:0"] = "SampleApp.SampleAppDbContext",
        });
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        using var document = JsonDocument.Parse(tools.GetSchema("SampleAppDbContext"));

        Assert.Equal("SampleAppDbContext", document.RootElement.GetProperty("contextName").GetString());
    }

    [Fact]
    public void GetSchema_WithMultipleConnections_StillHonorsAnExplicitConnectionName()
    {
        var tools = CreateTools(new Dictionary<string, string?>
        {
            ["Connections:Alpha:ConnectionString"] = "Data Source=:memory:",
            ["Connections:Alpha:Provider"] = "Sqlite",
            ["Connections:Alpha:Environment"] = "Development",
            ["Connections:Alpha:AccessPolicy:AllowContexts:0"] = "SampleApp.SampleAppDbContext",
            ["Connections:Beta:ConnectionString"] = "Data Source=:memory:",
            ["Connections:Beta:Provider"] = "Sqlite",
            ["Connections:Beta:Environment"] = "Development",
            ["Connections:Beta:AccessPolicy:AllowContexts:0"] = "SampleApp.SampleAppDbContext",
        });
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        using var document = JsonDocument.Parse(tools.GetSchema("SampleAppDbContext", "Alpha"));

        Assert.Equal("SampleAppDbContext", document.RootElement.GetProperty("contextName").GetString());
    }

    private static EfCoreMcpTools CreateTools(Dictionary<string, string?> connectionSettings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(connectionSettings)
            .Build();
        var rawSqlOptions = new RawSqlExecutionOptions();
        var migrationsOptions = new MigrationsOptions();

        return new EfCoreMcpTools(
            new AssemblyLoaderService(),
            new AssemblyDiscoveryService(),
            new ConnectionRegistry(configuration),
            new SchemaCache(),
            new RoslynQueryExecutor(new QueryExecutionOptions(), new QueryCompiler(new QueryCompilationOptions())),
            new OutOfProcessRoslynQueryExecutor(new QueryExecutionOptions()),
            new QueryExecutionOptions(),
            rawSqlOptions,
            new SqlQueryExecutor(rawSqlOptions, NullLogger<SqlQueryExecutor>.Instance),
            migrationsOptions,
            new MigrationInspector(migrationsOptions, NullLogger<MigrationInspector>.Instance),
            new JsonToolResultFormatter(),
            new ToolDiagnosticsOptions(),
            NullLogger<EfCoreMcpTools>.Instance,
            new EntityMutationsOptions(),
            new EntityMutationExecutor(NullLogger<EntityMutationExecutor>.Instance));
    }
}
