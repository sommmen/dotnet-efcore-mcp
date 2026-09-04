using System.Text.Json;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.Migrations;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Schema;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using DotnetEfCoreMcp.Server.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace DotnetEfCoreMcp.Server.Tests.Migrations;

/// <summary>Covers the <c>list_migrations</c>/<c>generate_migration_script</c> MCP tool surface:
/// parameter binding/response shape, the <c>Migrations:Enabled</c> gate, Production/ReadOnly
/// connection rejection, and that a failed call never mutates the target database.</summary>
public sealed class EfCoreMcpToolsMigrationsTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private EfCoreMcpTools CreateTools(
        MigrationsOptions? migrationsOptions = null,
        ConnectionAccessMode accessMode = ConnectionAccessMode.ReadWrite,
        EnvironmentType environment = EnvironmentType.Development)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Connections:MigrationsTests:ConnectionString"] = _db.ConnectionString,
                ["Connections:MigrationsTests:Provider"] = "Sqlite",
                ["Connections:MigrationsTests:AccessMode"] = accessMode.ToString(),
                ["Connections:MigrationsTests:Environment"] = environment.ToString(),
            })
            .Build();
        var rawSqlOptions = new RawSqlExecutionOptions();

        var tools = new EfCoreMcpTools(
            new AssemblyLoaderService(),
            new AssemblyDiscoveryService(),
            new ConnectionRegistry(configuration),
            new SchemaCache(),
            new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance),
            new RoslynQueryExecutor(new QueryExecutionOptions(), new QueryCompiler(new QueryCompilationOptions())),
            new QueryExecutionOptions(),
            rawSqlOptions,
            new SqlQueryExecutor(rawSqlOptions, NullLogger<SqlQueryExecutor>.Instance),
            migrationsOptions ?? new MigrationsOptions(),
            new MigrationInspector(migrationsOptions ?? new MigrationsOptions(), NullLogger<MigrationInspector>.Instance),
            new JsonToolResultFormatter(),
            new ToolDiagnosticsOptions(),
            NullLogger<EfCoreMcpTools>.Instance);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        return tools;
    }

    [Fact]
    public async Task ListMigrations_AgainstFreshDatabase_ReturnsExpectedShapeWithEveryMigrationPending()
    {
        var tools = CreateTools();

        var json = await tools.ListMigrations("SampleAppDbContext", "MigrationsTests");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("SampleAppDbContext", root.GetProperty("contextName").GetString());
        Assert.Equal("MigrationsTests", root.GetProperty("connectionName").GetString());
        Assert.False(root.GetProperty("databaseExists").GetBoolean());
        Assert.False(root.GetProperty("appliedStateAvailable").GetBoolean());
        Assert.Equal(0, root.GetProperty("appliedMigrations").GetArrayLength());
        Assert.Equal(1, root.GetProperty("pendingMigrations").GetArrayLength());
    }

    [Fact]
    public async Task ListMigrations_WithProductionConnection_IsRejected()
    {
        var tools = CreateTools(environment: EnvironmentType.Production);

        var exception = await Assert.ThrowsAsync<McpException>(() => tools.ListMigrations("SampleAppDbContext", "MigrationsTests"));

        Assert.Contains("production", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListMigrations_WithReadOnlyConnection_IsStillAllowed()
    {
        var tools = CreateTools(accessMode: ConnectionAccessMode.ReadOnly);

        var json = await tools.ListMigrations("SampleAppDbContext", "MigrationsTests");

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("pendingMigrations", out _));
    }

    [Fact]
    public async Task GenerateMigrationScript_WhenDisabled_RejectsBeforeGeneratingAndDoesNotTouchDatabase()
    {
        var tools = CreateTools(new MigrationsOptions { Enabled = false });

        var exception = await Assert.ThrowsAsync<McpException>(() => tools.GenerateMigrationScript("SampleAppDbContext", "MigrationsTests"));

        Assert.Contains("disabled", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Migrations:Enabled", exception.Message, StringComparison.Ordinal);
        Assert.Contains("restart", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(_db.ConnectionString.Replace("Data Source=", string.Empty)));
    }

    [Fact]
    public async Task GenerateMigrationScript_WithProductionConnection_RejectsEvenWhenEnabled()
    {
        var tools = CreateTools(new MigrationsOptions { Enabled = true }, environment: EnvironmentType.Production);

        var exception = await Assert.ThrowsAsync<McpException>(() => tools.GenerateMigrationScript("SampleAppDbContext", "MigrationsTests"));

        Assert.Contains("production", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateMigrationScript_WithReadOnlyConnection_RejectsEvenWhenEnabled()
    {
        var tools = CreateTools(new MigrationsOptions { Enabled = true }, accessMode: ConnectionAccessMode.ReadOnly);

        var exception = await Assert.ThrowsAsync<McpException>(() => tools.GenerateMigrationScript("SampleAppDbContext", "MigrationsTests"));

        Assert.Contains("ReadWrite", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateMigrationScript_WhenEnabledWithReadWriteConnection_ReturnsScriptAndDoesNotMutateDatabase()
    {
        var tools = CreateTools(new MigrationsOptions { Enabled = true });

        var json = await tools.GenerateMigrationScript("SampleAppDbContext", "MigrationsTests", idempotent: false);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("SampleAppDbContext", root.GetProperty("contextName").GetString());
        Assert.Equal("MigrationsTests", root.GetProperty("connectionName").GetString());
        Assert.False(root.GetProperty("idempotent").GetBoolean());
        Assert.Contains("CREATE TABLE", root.GetProperty("sql").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, root.GetProperty("migrationCount").GetInt32());

        // Non-mutation proof: previewing a script must never create the database file itself.
        var dbFilePath = _db.ConnectionString["Data Source=".Length..];
        Assert.False(File.Exists(dbFilePath));
    }

    [Fact]
    public async Task GenerateMigrationScript_ScriptExceedingConfiguredCap_IsTruncatedWithFlagSet()
    {
        var tools = CreateTools(new MigrationsOptions { Enabled = true, MaxScriptLength = 50 });

        var json = await tools.GenerateMigrationScript("SampleAppDbContext", "MigrationsTests", idempotent: false);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.True(root.GetProperty("sql").GetString()!.Length <= 50);
    }

    [Fact]
    public async Task GenerateMigrationScript_Idempotent_SurfacesRedactedSqliteLimitationMessage()
    {
        var tools = CreateTools(new MigrationsOptions { Enabled = true });

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.GenerateMigrationScript("SampleAppDbContext", "MigrationsTests", idempotent: true));

        Assert.Contains("idempotent: false", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("NotSupportedException", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateMigrationScript_WithUnknownFromMigration_SurfacesRedactedActionableMessage()
    {
        var tools = CreateTools(new MigrationsOptions { Enabled = true });

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.GenerateMigrationScript("SampleAppDbContext", "MigrationsTests", fromMigration: "does-not-exist", idempotent: false));

        Assert.Contains("list_migrations", exception.Message, StringComparison.Ordinal);
    }
}
