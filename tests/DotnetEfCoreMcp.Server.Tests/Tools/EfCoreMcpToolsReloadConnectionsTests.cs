using System.Text.Json;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.Migrations;
using DotnetEfCoreMcp.Server.Mutations;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Schema;
using DotnetEfCoreMcp.Server.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace DotnetEfCoreMcp.Server.Tests.Tools;

/// <summary>Covers issue #121 through the MCP tool surface: <c>list_connections</c> must pick up a
/// connection added to the configuration source after startup, and <c>reload_connections</c> must
/// provide an explicit refresh that reports what changed - so an agent never has to conclude a
/// missing connection "was never configured" or ask for a client restart it cannot perform.</summary>
public sealed class EfCoreMcpToolsReloadConnectionsTests : IDisposable
{
    private readonly string _secretsPath = Path.Combine(
        Path.GetTempPath(), $"efcore-mcp-tools-connections-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_secretsPath))
        {
            File.Delete(_secretsPath);
        }
    }

    [Fact]
    public void ListConnections_WhenAConnectionIsAddedAfterStartup_ReportsItWithoutAClientRestart()
    {
        WriteConnections("Alpha");
        var tools = CreateTools();
        Assert.Single(ConnectionNames(tools.ListConnections()));

        WriteConnections("Alpha", "Beta");

        Assert.Equal(["Alpha", "Beta"], ConnectionNames(tools.ListConnections()));
    }

    [Fact]
    public void ListConnections_ReportsWhenTheRegistryWasLastLoaded()
    {
        WriteConnections("Alpha");
        var tools = CreateTools();

        using var document = JsonDocument.Parse(tools.ListConnections());

        Assert.True(document.RootElement.TryGetProperty("registryLoadedAtUtc", out var loadedAt));
        Assert.True(DateTimeOffset.TryParse(loadedAt.GetString(), out _));
    }

    [Fact]
    public void ReloadConnections_ReportsAddedAndRemovedNamesAndTheRefreshedList()
    {
        WriteConnections("Alpha", "Gone");
        var tools = CreateTools();
        _ = tools.ListConnections();

        WriteConnections("Alpha", "Beta");
        using var document = JsonDocument.Parse(tools.ReloadConnections());

        Assert.Equal(["Beta"], document.RootElement.GetProperty("addedConnections").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["Gone"], document.RootElement.GetProperty("removedConnections").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(
            ["Alpha", "Beta"],
            document.RootElement.GetProperty("connections").EnumerateArray()
                .Select(e => e.GetProperty("name").GetString())
                .OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void ReloadConnections_NeverExposesAConnectionString()
    {
        WriteConnections("Alpha");
        var tools = CreateTools();

        var output = tools.ReloadConnections();

        Assert.DoesNotContain("Data Source", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ReloadConnections_WhenTheReloadedConfigurationIsMalformed_SurfacesTheActionableMessage()
    {
        WriteConnections("Alpha");
        var tools = CreateTools();
        _ = tools.ListConnections();

        File.WriteAllText(_secretsPath, JsonSerializer.Serialize(new
        {
            Connections = new Dictionary<string, object>
            {
                ["Broken"] = new Dictionary<string, object> { ["Provider"] = "Sqlite" },
            },
        }));

        var exception = Assert.Throws<McpException>(tools.ReloadConnections);

        Assert.Contains("ConnectionString", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunQuery_WithAConnectionAddedAfterStartup_NoLongerRejectsItAsUnknown()
    {
        WriteConnections("Alpha");
        var tools = CreateTools();
        tools.LoadAssembly(TestSupport.FixturePaths.SampleAppDllPath);

        var beforeAdding = await Assert.ThrowsAsync<McpException>(
            () => tools.RunQuery("SampleAppDbContext", "Customers.Select(c => c.Name)", "Beta"));
        Assert.Contains("No connection named 'Beta'", beforeAdding.Message, StringComparison.Ordinal);
        Assert.Contains("reload_connections", beforeAdding.Message, StringComparison.Ordinal);

        WriteConnections("Alpha", "Beta");

        // The AccessPolicy written by WriteConnections deliberately does not allow SampleApp's
        // context, so the connection now resolves and is rejected by policy instead of being
        // reported as a name that does not exist.
        var afterAdding = await Assert.ThrowsAsync<McpException>(
            () => tools.RunQuery("SampleAppDbContext", "Customers.Select(c => c.Name)", "Beta"));
        Assert.DoesNotContain("No connection named", afterAdding.Message, StringComparison.Ordinal);
    }

    private static IEnumerable<string?> ConnectionNames(string listConnectionsOutput)
    {
        using var document = JsonDocument.Parse(listConnectionsOutput);
        return document.RootElement.GetProperty("connections").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }

    private void WriteConnections(params string[] names)
    {
        var connections = names.ToDictionary(
            name => name,
            name => new Dictionary<string, object>
            {
                ["Provider"] = "Sqlite",
                ["ConnectionString"] = "Data Source=:memory:",
                ["Environment"] = "Development",
                ["AccessPolicy"] = new Dictionary<string, object>
                {
                    ["AllowContexts"] = new[] { "SampleApp.SampleAppDbContext" },
                },
            });

        File.WriteAllText(_secretsPath, JsonSerializer.Serialize(new { Connections = connections }));
    }

    private EfCoreMcpTools CreateTools()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(_secretsPath, optional: true, reloadOnChange: false)
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
