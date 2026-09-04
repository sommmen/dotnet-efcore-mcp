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

/// <summary>End-to-end coverage of the <c>test_connection</c> MCP tool: verifies the redacted
/// success/failure payload shape, connection resolution (explicit name vs. active-connection
/// fallback, unknown name), and that cancellation is propagated rather than surfaced as a JSON
/// status.</summary>
public sealed class EfCoreMcpToolsTestConnectionTests
{
    private static EfCoreMcpTools CreateTools(SqliteTestDatabase db, out AssemblyLoaderService assemblyLoader)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Connections:Primary:ConnectionString"] = db.ConnectionString,
                ["Connections:Primary:Provider"] = "Sqlite",
                ["Connections:Primary:AccessMode"] = "ReadOnly",
                ["Connections:Primary:Environment"] = "Development",
                ["Connections:Primary:AccessPolicy:AllowContexts:0"] = "SampleApp.SampleAppDbContext",
            })
            .Build();
        var rawSqlOptions = new RawSqlExecutionOptions();
        var migrationsOptions = new MigrationsOptions();

        assemblyLoader = new AssemblyLoaderService();
        return new EfCoreMcpTools(
            assemblyLoader,
            new AssemblyDiscoveryService(),
            new ConnectionRegistry(configuration),
            new SchemaCache(),
            new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance),
            new RoslynQueryExecutor(new QueryExecutionOptions(), new QueryCompiler(new QueryCompilationOptions())),
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

    [Fact]
    public async Task TestConnection_AgainstAHealthyConnection_ReturnsRedactedHealthyPayload()
    {
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(db, out var assemblyLoader);
        var handle = assemblyLoader.Load(FixturePaths.SampleAppDllPath);
        var contextType = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors
            .Single(d => d.Name == "SampleAppDbContext").ClrType;

        using (var setupContext = DbContextActivator.CreateInstance(contextType, db.ToRegistryEntry("Primary"), DatabaseProvider.Sqlite))
        {
            setupContext.Database.EnsureCreated();
        }

        using var document = JsonDocument.Parse(await tools.TestConnection("SampleAppDbContext", "Primary"));
        var root = document.RootElement;

        Assert.Equal("SampleAppDbContext", root.GetProperty("contextName").GetString());
        Assert.Equal("Primary", root.GetProperty("connectionName").GetString());
        Assert.Equal("Sqlite", root.GetProperty("provider").GetString());
        Assert.Equal("Development", root.GetProperty("environment").GetString());
        Assert.Equal("healthy", root.GetProperty("status").GetString());

        var raw = document.RootElement.GetRawText();
        Assert.DoesNotContain(db.ConnectionString, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Data Source", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnection_WhenConnectionNameIsOmitted_UsesTheActiveConnection()
    {
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(db, out var assemblyLoader);
        var handle = assemblyLoader.Load(FixturePaths.SampleAppDllPath);
        var contextType = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors
            .Single(d => d.Name == "SampleAppDbContext").ClrType;

        using (var setupContext = DbContextActivator.CreateInstance(contextType, db.ToRegistryEntry("Primary"), DatabaseProvider.Sqlite))
        {
            setupContext.Database.EnsureCreated();
        }

        using var document = JsonDocument.Parse(await tools.TestConnection("SampleAppDbContext"));

        Assert.Equal("Primary", document.RootElement.GetProperty("connectionName").GetString());
        Assert.Equal("healthy", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task TestConnection_WithAnUnknownConnectionName_ThrowsMcpException()
    {
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(db, out var assemblyLoader);
        assemblyLoader.Load(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.TestConnection("SampleAppDbContext", "DoesNotExist"));

        Assert.Contains("DoesNotExist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnection_WhenNoConnectionIsActiveAndNoneIsSpecified_ThrowsMcpException()
    {
        using var db = new SqliteTestDatabase();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Connections:ProdOnly:ConnectionString"] = db.ConnectionString,
                ["Connections:ProdOnly:Provider"] = "Sqlite",
                ["Connections:ProdOnly:Environment"] = "Production",
                ["Connections:ProdOnly:AccessPolicy:AllowContexts:0"] = "SampleApp.SampleAppDbContext",
            })
            .Build();
        var rawSqlOptions = new RawSqlExecutionOptions();
        var migrationsOptions = new MigrationsOptions();
        var assemblyLoader = new AssemblyLoaderService();
        var tools = new EfCoreMcpTools(
            assemblyLoader,
            new AssemblyDiscoveryService(),
            new ConnectionRegistry(configuration),
            new SchemaCache(),
            new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance),
            new RoslynQueryExecutor(new QueryExecutionOptions(), new QueryCompiler(new QueryCompilationOptions())),
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
        assemblyLoader.Load(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(() => tools.TestConnection("SampleAppDbContext"));

        Assert.Contains("No connection is active", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnection_AgainstADatabaseFileThatDoesNotExistYet_ReturnsRedactedFailedPayload()
    {
        // A file-based SQLite connection whose database file has never been created behaves like an
        // unreachable/failed connection under EF Core's CanConnectAsync semantics (it performs a
        // read-only existence probe that cannot create the file), so this exercises the "failed"
        // branch without needing an actually-invalid connection string.
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(db, out var assemblyLoader);
        assemblyLoader.Load(FixturePaths.SampleAppDllPath);

        using var document = JsonDocument.Parse(await tools.TestConnection("SampleAppDbContext", "Primary"));

        Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain(db.ConnectionString, document.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnection_WhenCalledWithAnAlreadyCancelledToken_PropagatesCancellationRatherThanAStatus()
    {
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(db, out var assemblyLoader);
        assemblyLoader.Load(FixturePaths.SampleAppDllPath);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tools.TestConnection("SampleAppDbContext", "Primary", cts.Token));
    }
}
