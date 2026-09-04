using System.Text.Json;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
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
    public void GetSchema_WhenAnUnexpectedExceptionOccurs_ReturnsRedactedErrorByDefault()
    {
        var tools = CreateTools(new ThrowingResultFormatter());
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = Assert.Throws<McpException>(() => tools.GetSchema("SampleAppDbContext"));

        Assert.StartsWith("get_schema failed unexpectedly. Error reference: ", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("formatter failure", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-host", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSchema_WhenAnUnexpectedExceptionOccursAndDiagnosticsAreEnabled_ExposesSafeCategoryOnly()
    {
        var tools = CreateTools(
            new ThrowingResultFormatter(),
            toolDiagnosticsOptions: new ToolDiagnosticsOptions { ExposeSafeErrorDetails = true });
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = Assert.Throws<McpException>(() => tools.GetSchema("SampleAppDbContext"));

        Assert.StartsWith("get_schema failed unexpectedly. Error reference: ", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Failure category: InvalidOperationException", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("formatter failure", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-host", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", exception.Message, StringComparison.Ordinal);
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
    public async Task RunQuery_WhenAnUnexpectedExceptionOccurs_ReturnsRedactedErrorByDefault()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.RunQuery("SampleAppDbContext", "Customers.Select(c => c.Name)"));

        Assert.StartsWith("run_query failed unexpectedly. Error reference: ", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no such table", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SqliteException", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunQuery_WhenAnUnexpectedExceptionOccursAndDiagnosticsAreEnabled_ExposesSafeCategoryOnly()
    {
        var tools = CreateTools(toolDiagnosticsOptions: new ToolDiagnosticsOptions { ExposeSafeErrorDetails = true });
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.RunQuery("SampleAppDbContext", "Customers.Select(c => c.Name)"));

        Assert.StartsWith("run_query failed unexpectedly. Error reference: ", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Failure category: SqliteException", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no such table", exception.Message, StringComparison.OrdinalIgnoreCase);
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

    private static EfCoreMcpTools CreateTools(
        IToolResultFormatter? resultFormatter = null,
        ToolDiagnosticsOptions? toolDiagnosticsOptions = null)
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
            new RoslynQueryExecutor(new QueryExecutionOptions(), new QueryCompiler(new QueryCompilationOptions())),
            new QueryExecutionOptions(),
            rawSqlOptions,
            new SqlQueryExecutor(rawSqlOptions, NullLogger<SqlQueryExecutor>.Instance),
            resultFormatter ?? new JsonToolResultFormatter(),
            toolDiagnosticsOptions ?? new ToolDiagnosticsOptions(),
            NullLogger<EfCoreMcpTools>.Instance);
    }

    private sealed class ThrowingResultFormatter : IToolResultFormatter
    {
        private int formatCallCount;

        public string Format(object value)
        {
            if (Interlocked.Increment(ref formatCallCount) > 1)
            {
                throw new InvalidOperationException("formatter failure; Server=secret-host;Password=top-secret");
            }

            return new JsonToolResultFormatter().Format(value);
        }
    }
}
