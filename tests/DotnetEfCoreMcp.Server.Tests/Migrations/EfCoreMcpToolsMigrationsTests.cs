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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace DotnetEfCoreMcp.Server.Tests.Migrations;

/// <summary>Covers the <c>list_migrations</c>/<c>generate_migration_script</c> MCP tool surface:
/// parameter binding/response shape, the <c>Migrations:Enabled</c> gate, Production/ReadOnly
/// connection handling, and that a failed call never mutates the target database.</summary>
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
                ["Connections:MigrationsTests:AccessPolicy:AllowContexts:0"] = "SampleApp.SampleAppDbContext",
            })
            .Build();
        var rawSqlOptions = new RawSqlExecutionOptions();
        var effectiveMigrationsOptions = migrationsOptions ?? new MigrationsOptions();

        var tools = new EfCoreMcpTools(
            new AssemblyLoaderService(),
            new AssemblyDiscoveryService(),
            new ConnectionRegistry(configuration),
            new SchemaCache(),
            new RoslynQueryExecutor(new QueryExecutionOptions(), new QueryCompiler(new QueryCompilationOptions())),
            new OutOfProcessRoslynQueryExecutor(new QueryExecutionOptions()),
            new QueryExecutionOptions(),
            rawSqlOptions,
            new SqlQueryExecutor(rawSqlOptions, NullLogger<SqlQueryExecutor>.Instance),
            effectiveMigrationsOptions,
            new MigrationInspector(effectiveMigrationsOptions, NullLogger<MigrationInspector>.Instance),
            new JsonToolResultFormatter(),
            new ToolDiagnosticsOptions(),
            NullLogger<EfCoreMcpTools>.Instance,
            new EntityMutationsOptions(),
            new EntityMutationExecutor(NullLogger<EntityMutationExecutor>.Instance));
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
        Assert.Equal(2, root.GetProperty("pendingMigrations").GetArrayLength());
        Assert.Equal(2, root.GetProperty("knownMigrationCount").GetInt32());
        Assert.All(root.GetProperty("migrations").EnumerateArray(), m =>
        {
            Assert.True(m.TryGetProperty("MigrationId", out _) || m.TryGetProperty("migrationId", out _));
            Assert.False(m.TryGetProperty("Applied", out _) || m.TryGetProperty("applied", out _));
        });
        Assert.False(root.TryGetProperty("lastAppliedMigration", out _));
        Assert.Equal(0, root.GetProperty("unknownAppliedMigrations").GetArrayLength());
        Assert.False(root.GetProperty("hasPendingModelChanges").GetBoolean());
    }

    [Fact]
    public async Task ListMigrations_WithProductionConnection_ReportsAppliedAndPending()
    {
        var tools = CreateTools(environment: EnvironmentType.Production);

        var pendingJson = await tools.ListMigrations("SampleAppDbContext", "MigrationsTests", cancellationToken: TestContext.Current.CancellationToken);
        using (var pendingDocument = JsonDocument.Parse(pendingJson))
        {
            Assert.Equal(0, pendingDocument.RootElement.GetProperty("appliedMigrations").GetArrayLength());
            Assert.Equal(2, pendingDocument.RootElement.GetProperty("pendingMigrations").GetArrayLength());
        }

        await MigrateDatabaseAsync();

        var appliedJson = await tools.ListMigrations("SampleAppDbContext", "MigrationsTests", cancellationToken: TestContext.Current.CancellationToken);
        using var appliedDocument = JsonDocument.Parse(appliedJson);
        var root = appliedDocument.RootElement;
        Assert.True(root.GetProperty("databaseExists").GetBoolean());
        Assert.True(root.GetProperty("appliedStateAvailable").GetBoolean());
        Assert.Equal(2, root.GetProperty("appliedMigrations").GetArrayLength());
        Assert.Equal(0, root.GetProperty("pendingMigrations").GetArrayLength());
        Assert.EndsWith("_AddCustomerEmail", root.GetProperty("lastAppliedMigration").GetString(), StringComparison.Ordinal);
    }

    private async Task MigrateDatabaseAsync(string? targetMigration = null, string? extraSql = null)
    {
        var handle = new AssemblyLoaderService().Load(FixturePaths.SampleAppDllPath);
        var contextType = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors.Single(d => d.Name == "SampleAppDbContext").ClrType;
        using var context = DbContextActivator.CreateInstance(contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
        if (extraSql is not null)
        {
            await context.Database.ExecuteSqlRawAsync(extraSql);
        }
    }

    [Fact]
    public async Task CompareSchema_WithOnlyInitialCreateApplied_ReportsMissingEmailAttributedToPendingMigration()
    {
        await MigrateDatabaseAsync("20260906122510_InitialCreate");
        var tools = CreateTools();

        var json = await tools.CompareSchema("SampleAppDbContext", "MigrationsTests", cancellationToken: TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("SampleAppDbContext", root.GetProperty("contextName").GetString());
        Assert.Equal("MigrationsTests", root.GetProperty("connectionName").GetString());
        Assert.True(root.GetProperty("hasDifferences").GetBoolean());
        var missing = Assert.Single(root.GetProperty("notInDatabase").EnumerateArray());
        Assert.Equal("Column", missing.GetProperty("kind").GetString());
        Assert.Equal("Customers", missing.GetProperty("table").GetString());
        Assert.Equal("Email", missing.GetProperty("column").GetString());
        Assert.Equal("Customer", missing.GetProperty("entity").GetString());
        Assert.Equal("Email", missing.GetProperty("property").GetString());
        Assert.Equal("20261113093414_AddCustomerEmail", missing.GetProperty("introducedBy").GetString());
        Assert.True(missing.GetProperty("introducedByIsPending").GetBoolean());
        Assert.Equal(0, root.GetProperty("different").GetArrayLength());
        Assert.Equal(0, root.GetProperty("extraInDatabase").GetArrayLength());
        Assert.Equal(3, root.GetProperty("comparedTableCount").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task CompareSchema_FullyMigrated_ReportsNoDifferences()
    {
        await MigrateDatabaseAsync();
        var tools = CreateTools();

        var json = await tools.CompareSchema("SampleAppDbContext", "MigrationsTests", includeExtras: true, cancellationToken: TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.False(root.GetProperty("hasDifferences").GetBoolean(), json);
        Assert.Equal(0, root.GetProperty("notInDatabase").GetArrayLength());
        Assert.Equal(0, root.GetProperty("different").GetArrayLength());
        Assert.Equal(0, root.GetProperty("extraInDatabase").GetArrayLength());
    }

    [Fact]
    public async Task CompareSchema_TablesFilter_ExcludesUnlistedTablesCaseInsensitively()
    {
        await MigrateDatabaseAsync("20260906122510_InitialCreate");
        var tools = CreateTools();

        var ordersJson = await tools.CompareSchema("SampleAppDbContext", "MigrationsTests", tables: ["orders"], cancellationToken: TestContext.Current.CancellationToken);
        var customersJson = await tools.CompareSchema("SampleAppDbContext", "MigrationsTests", tables: ["main.CUSTOMERS"], cancellationToken: TestContext.Current.CancellationToken);

        using var orders = JsonDocument.Parse(ordersJson);
        Assert.False(orders.RootElement.GetProperty("hasDifferences").GetBoolean());
        Assert.Equal(1, orders.RootElement.GetProperty("comparedTableCount").GetInt32());
        using var customers = JsonDocument.Parse(customersJson);
        Assert.Single(customers.RootElement.GetProperty("notInDatabase").EnumerateArray());
    }

    [Fact]
    public async Task CompareSchema_OnProductionReadOnlyConnection_Succeeds()
    {
        await MigrateDatabaseAsync("20260906122510_InitialCreate");
        var tools = CreateTools(accessMode: ConnectionAccessMode.ReadOnly, environment: EnvironmentType.Production);

        var json = await tools.CompareSchema("SampleAppDbContext", "MigrationsTests", cancellationToken: TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("hasDifferences").GetBoolean());
        Assert.DoesNotContain("Data Source", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareSchema_IncludeExtras_ReportsColumnAddedOutsideTheModel()
    {
        await MigrateDatabaseAsync(extraSql: "ALTER TABLE \"Customers\" ADD COLUMN \"LegacyCode\" TEXT NULL");
        var tools = CreateTools();

        var withoutExtras = await tools.CompareSchema("SampleAppDbContext", "MigrationsTests", cancellationToken: TestContext.Current.CancellationToken);
        var withExtras = await tools.CompareSchema("SampleAppDbContext", "MigrationsTests", includeExtras: true, cancellationToken: TestContext.Current.CancellationToken);

        using (var document = JsonDocument.Parse(withoutExtras))
        {
            Assert.False(document.RootElement.GetProperty("hasDifferences").GetBoolean());
        }

        using var extrasDocument = JsonDocument.Parse(withExtras);
        Assert.True(extrasDocument.RootElement.GetProperty("hasDifferences").GetBoolean());
        var extra = Assert.Single(extrasDocument.RootElement.GetProperty("extraInDatabase").EnumerateArray());
        Assert.Equal("Column", extra.GetProperty("kind").GetString());
        Assert.Equal("Customers", extra.GetProperty("table").GetString());
        Assert.Equal("LegacyCode", extra.GetProperty("column").GetString());
    }

    [Fact]
    public async Task CompareSchema_DriftBeyondCap_IsTruncated()
    {
        await MigrateDatabaseAsync(extraSql: "ALTER TABLE \"Customers\" ADD COLUMN \"A\" TEXT NULL; ALTER TABLE \"Customers\" ADD COLUMN \"B\" TEXT NULL");
        var tools = CreateTools(new MigrationsOptions { MaxDriftEntries = 1 });

        var json = await tools.CompareSchema("SampleAppDbContext", "MigrationsTests", includeExtras: true, cancellationToken: TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, document.RootElement.GetProperty("extraInDatabase").GetArrayLength());
    }

    [Fact]
    public async Task CompareSchema_WhenDatabaseDoesNotExist_ReportsActionableErrorWithoutCreatingIt()
    {
        var tools = CreateTools();
        var dbFilePath = _db.ConnectionString["Data Source=".Length..];

        var exception = await Assert.ThrowsAsync<McpException>(() => tools.CompareSchema("SampleAppDbContext", "MigrationsTests", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("list_migrations", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(dbFilePath));
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
    public async Task DescribeMigration_BySuffix_ReturnsAddColumnWithoutTouchingDatabase()
    {
        var tools = CreateTools();

        var json = await tools.DescribeMigration("SampleAppDbContext", "addcustomeremail", "MigrationsTests", cancellationToken: TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("SampleAppDbContext", root.GetProperty("contextName").GetString());
        Assert.Equal("20261113093414_AddCustomerEmail", root.GetProperty("migrationId").GetString());
        Assert.Equal("up", root.GetProperty("direction").GetString());
        Assert.Equal(1, root.GetProperty("operationCount").GetInt32());
        var operation = Assert.Single(root.GetProperty("operations").EnumerateArray());
        Assert.Equal("AddColumn", operation.GetProperty("kind").GetString());
        Assert.Equal("Customers", operation.GetProperty("table").GetString());
        Assert.Equal("Email", operation.GetProperty("name").GetString());
        Assert.Equal("TEXT", operation.GetProperty("columnType").GetString());
        Assert.True(operation.GetProperty("isNullable").GetBoolean());
        Assert.False(File.Exists(_db.ConnectionString["Data Source=".Length..]));
    }

    [Fact]
    public async Task DescribeMigration_Down_ReturnsDropColumn()
    {
        var tools = CreateTools();

        var json = await tools.DescribeMigration("SampleAppDbContext", "AddCustomerEmail", "MigrationsTests", direction: "down", cancellationToken: TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(json);
        Assert.Equal("down", document.RootElement.GetProperty("direction").GetString());
        var operation = Assert.Single(document.RootElement.GetProperty("operations").EnumerateArray());
        Assert.Equal("DropColumn", operation.GetProperty("kind").GetString());
        Assert.Equal("Customers", operation.GetProperty("table").GetString());
        Assert.Equal("Email", operation.GetProperty("name").GetString());
    }

    [Fact]
    public async Task DescribeMigration_InitialCreate_ReturnsCreateTableOperationsWithColumns()
    {
        var tools = CreateTools();

        var json = await tools.DescribeMigration("SampleAppDbContext", "InitialCreate", "MigrationsTests", cancellationToken: TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(json);
        var createTables = document.RootElement.GetProperty("operations").EnumerateArray()
            .Where(o => o.GetProperty("kind").GetString() == "CreateTable")
            .ToList();
        var customers = Assert.Single(createTables, o => o.GetProperty("table").GetString() == "Customers");
        var name = Assert.Single(customers.GetProperty("columns").EnumerateArray(), c => c.GetProperty("name").GetString() == "Name");
        Assert.False(name.GetProperty("isNullable").GetBoolean());
        Assert.Contains(document.RootElement.GetProperty("operations").EnumerateArray(), o => o.GetProperty("kind").GetString() == "CreateIndex");
    }

    [Fact]
    public async Task DescribeMigration_WithUnknownId_ThrowsActionableError()
    {
        var tools = CreateTools();

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.DescribeMigration("SampleAppDbContext", "DoesNotExist", "MigrationsTests", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("DoesNotExist", exception.Message, StringComparison.Ordinal);
        Assert.Contains("list_migrations", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescribeMigration_WithInvalidDirection_Throws()
    {
        var tools = CreateTools();

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.DescribeMigration("SampleAppDbContext", "AddCustomerEmail", "MigrationsTests", direction: "sideways", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("'up' or 'down'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescribeMigration_OnProductionReadOnlyConnectionToUnreachableDatabase_Succeeds()
    {
        var tools = CreateTools(accessMode: ConnectionAccessMode.ReadOnly, environment: EnvironmentType.Production);
        var dbFilePath = _db.ConnectionString["Data Source=".Length..];
        Assert.False(File.Exists(dbFilePath));

        var json = await tools.DescribeMigration("SampleAppDbContext",         "AddCustomerEmail", "MigrationsTests", cancellationToken: TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("operationCount").GetInt32());
        Assert.False(File.Exists(dbFilePath));
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
        Assert.Equal(2, root.GetProperty("migrationCount").GetInt32());

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
        // Issue #85: the provider's own limitation text is now appended as the cause, since that is
        // what tells the caller which provider refused and why. Credentials are still redacted.
        Assert.Contains("Cause: NotSupportedException", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Data Source", exception.Message, StringComparison.Ordinal);
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
