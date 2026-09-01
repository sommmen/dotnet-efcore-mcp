using System.Text.Json;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Schema;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using DotnetEfCoreMcp.Server.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace DotnetEfCoreMcp.Server.Tests.Tools;

public sealed class EfCoreMcpToolsSchemaSelectionTests
{
    [Fact]
    public void GetSchema_AcceptsShortAndFullyQualifiedContextNames()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        using var shortNameDocument = JsonDocument.Parse(tools.GetSchema("SampleAppDbContext"));
        using var fullNameDocument = JsonDocument.Parse(tools.GetSchema("SampleApp.SampleAppDbContext"));

        Assert.Equal("SampleAppDbContext", shortNameDocument.RootElement.GetProperty("contextName").GetString());
        Assert.Equal("SampleAppDbContext", fullNameDocument.RootElement.GetProperty("contextName").GetString());
    }

    [Fact]
    public void GetSchema_WhenContextNameIsOmittedWithMultipleContexts_ListsShortNames()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = Assert.Throws<McpException>(() => tools.GetSchema());

        Assert.Contains("SampleAppDbContext", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FactoryOnlyDbContext", exception.Message, StringComparison.Ordinal);
        Assert.Contains("list_contexts", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSchema_ReturnsBoundedEntityPageAndContinuationMetadata()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        using var document = JsonDocument.Parse(tools.GetSchema("SampleAppDbContext", pageSize: 1));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(1, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, root.GetProperty("entities").GetArrayLength());
        Assert.True(root.GetProperty("totalEntityCount").GetInt32() > 1);
        Assert.True(root.GetProperty("hasMore").GetBoolean());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, root.GetProperty("nextPage").GetInt32());
        Assert.Contains("page=2", root.GetProperty("hint").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAssembly_WhenExactlyOneContextExists_ReportsDefaultContextHint()
    {
        var tools = CreateTools();

        using var document = JsonDocument.Parse(tools.LoadAssembly(FixturePaths.PackageDependencyAppDllPath));
        var root = document.RootElement;

        Assert.Equal("PackageDependencyDbContext", root.GetProperty("defaultContext").GetString());
        Assert.Contains("omit contextName", root.GetProperty("hint").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    private static EfCoreMcpTools CreateTools()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Connections:SchemaTests:ConnectionString"] = "Data Source=:memory:",
                ["Connections:SchemaTests:Provider"] = "Sqlite",
                ["Connections:SchemaTests:AccessMode"] = "ReadOnly",
                ["Connections:SchemaTests:Environment"] = "Development",
            })
            .Build();
        var rawSqlOptions = new RawSqlExecutionOptions();

        return new EfCoreMcpTools(
            new AssemblyLoaderService(),
            new AssemblyDiscoveryService(),
            new ConnectionRegistry(configuration),
            new SchemaCache(),
            new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance),
            rawSqlOptions,
            new SqlQueryExecutor(rawSqlOptions, NullLogger<SqlQueryExecutor>.Instance),
            new JsonToolResultFormatter(),
            NullLogger<EfCoreMcpTools>.Instance);
    }
}
