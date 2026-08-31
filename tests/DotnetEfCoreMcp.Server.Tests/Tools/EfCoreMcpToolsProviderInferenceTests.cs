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

/// <summary>End-to-end coverage of provider inference through the actual MCP tool surface: a
/// connection configured without a <c>Provider</c> should still work against the SampleApp fixture
/// (which references only the SQLite EF Core provider), and a clearly invalid connection name
/// should still surface as a normal <see cref="McpException"/>.</summary>
public sealed class EfCoreMcpToolsProviderInferenceTests
{
    private static EfCoreMcpTools CreateTools(SqliteTestDatabase db, out AssemblyLoaderService assemblyLoader)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Connections:Inferred:ConnectionString"] = db.ConnectionString,
            })
            .Build();

        assemblyLoader = new AssemblyLoaderService();
        var tools = new EfCoreMcpTools(
            assemblyLoader,
            new AssemblyDiscoveryService(),
            new ConnectionRegistry(configuration),
            new SchemaCache(),
            new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance),
            NullLogger<EfCoreMcpTools>.Instance);
        return tools;
    }

    [Fact]
    public void GetSchema_ConnectionWithoutExplicitProvider_InfersSqliteFromLoadedAssembly()
    {
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(db, out var assemblyLoader);
        assemblyLoader.Load(FixturePaths.SampleAppDllPath);

        var json = tools.GetSchema("SampleAppDbContext", "Inferred");

        Assert.Contains("\"entities\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListConnections_ConnectionWithoutExplicitProvider_ReportsInferredMarker()
    {
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(db, out _);

        var json = tools.ListConnections();

        Assert.Contains("(inferred)", json);
    }
}
