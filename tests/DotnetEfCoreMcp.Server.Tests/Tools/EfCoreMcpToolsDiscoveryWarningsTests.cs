using System.Text.Json;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Schema;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using DotnetEfCoreMcp.Server.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetEfCoreMcp.Server.Tests.Tools;

public sealed class EfCoreMcpToolsDiscoveryWarningsTests
{
    [Fact]
    public void LoadAssembly_AssemblyWithoutDbContexts_ReturnsNoContextWarning()
    {
        var tools = CreateTools();

        using var document = JsonDocument.Parse(tools.LoadAssembly(FixturePaths.NoContextAppDllPath));
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Array, root.GetProperty("discoveredDbContexts").ValueKind);
        Assert.Empty(root.GetProperty("discoveredDbContexts").EnumerateArray());
        AssertWarningContains(root, "No DbContext-derived types");
    }

    [Fact]
    public void ListContexts_AssemblyWithoutDbContexts_ReturnsNoContextWarning()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.NoContextAppDllPath);

        using var document = JsonDocument.Parse(tools.ListContexts());
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Array, root.GetProperty("contexts").ValueKind);
        Assert.Empty(root.GetProperty("contexts").EnumerateArray());
        AssertWarningContains(root, "No DbContext-derived types");
    }

    private static EfCoreMcpTools CreateTools()
    {
        var configuration = new ConfigurationBuilder().Build();
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

    private static void AssertWarningContains(JsonElement root, string expected)
    {
        var warnings = root.GetProperty("warnings");
        Assert.Equal(JsonValueKind.Array, warnings.ValueKind);
        var matched = warnings.EnumerateArray()
            .Select(warning => warning.GetString())
            .Any(message => message?.Contains(expected, StringComparison.Ordinal) == true);
        Assert.True(matched, $"Expected warnings to contain '{expected}'. Actual: {string.Join("; ", warnings.EnumerateArray().Select(w => w.GetString()))}");
    }
}
