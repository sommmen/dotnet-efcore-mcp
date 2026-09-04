using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Migrations;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetEfCoreMcp.Server.Tests.Migrations;

/// <summary>Exercises <see cref="MigrationInspector"/> directly against the real
/// <c>InitialCreate</c> migration scaffolded into the SampleApp fixture, so applied/pending state
/// and script generation are verified against genuine EF Core behavior rather than hand-authored
/// stand-ins.</summary>
public sealed class MigrationInspectorTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();
    private readonly Type _contextType;

    public MigrationInspectorTests()
    {
        var handle = new AssemblyLoaderService().Load(FixturePaths.SampleAppDllPath);
        _contextType = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors.Single(d => d.Name == "SampleAppDbContext").ClrType;
    }

    public void Dispose() => _db.Dispose();

    private DbContext NewContext(ConnectionAccessMode accessMode = ConnectionAccessMode.ReadWrite) =>
        DbContextActivator.CreateInstance(_contextType, _db.ToRegistryEntry(accessMode: accessMode), DatabaseProvider.Sqlite);

    private static MigrationInspector NewInspector(MigrationsOptions? options = null) =>
        new(options ?? new MigrationsOptions(), NullLogger<MigrationInspector>.Instance);

    [Fact]
    public async Task InspectAsync_WhenDatabaseDoesNotExist_ReportsEveryMigrationPendingAndAppliedStateUnavailable()
    {
        using var context = NewContext();
        var inspector = NewInspector();

        var result = await inspector.InspectAsync(context, _db.ToRegistryEntry(), DatabaseProvider.Sqlite, CancellationToken.None);

        Assert.False(result.DatabaseExists);
        Assert.False(result.AppliedStateAvailable);
        Assert.Empty(result.AppliedMigrations);
        var pending = Assert.Single(result.PendingMigrations);
        Assert.Contains("InitialCreate", pending.MigrationId, StringComparison.Ordinal);
        Assert.Equal("Sqlite:10.0.11", pending.Target);
    }

    [Fact]
    public async Task InspectAsync_AfterMigrate_ReportsTheMigrationApplied()
    {
        using (var setupContext = NewContext())
        {
            await setupContext.Database.MigrateAsync();
        }

        using var context = NewContext();
        var inspector = NewInspector();

        var result = await inspector.InspectAsync(context, _db.ToRegistryEntry(), DatabaseProvider.Sqlite, CancellationToken.None);

        Assert.True(result.DatabaseExists);
        Assert.True(result.AppliedStateAvailable);
        var applied = Assert.Single(result.AppliedMigrations);
        Assert.Contains("InitialCreate", applied.MigrationId, StringComparison.Ordinal);
        Assert.Equal("10.0.11", applied.ProductVersion);
        Assert.Empty(result.PendingMigrations);
    }

    [Fact]
    public async Task InspectAsync_WhenDatabaseExistsButHistoryTableIsAbsent_ReportsAppliedStateUnavailable()
    {
        // EnsureCreated() creates the database file and entity tables from the model snapshot, but
        // never creates __EFMigrationsHistory - a real EF Core edge case distinct from "no database
        // at all" that the tool must not conflate with "zero migrations applied".
        using (var setupContext = NewContext())
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        using var context = NewContext();
        var inspector = NewInspector();

        var result = await inspector.InspectAsync(context, _db.ToRegistryEntry(), DatabaseProvider.Sqlite, CancellationToken.None);

        Assert.True(result.DatabaseExists);
        Assert.False(result.AppliedStateAvailable);
        Assert.Empty(result.AppliedMigrations);
        Assert.Single(result.PendingMigrations);
    }

    [Fact]
    public async Task GenerateScriptAsync_NonIdempotent_ProducesNonEmptyScriptAndDoesNotMutateDatabase()
    {
        using var context = NewContext();
        var inspector = NewInspector();

        var result = await inspector.GenerateScriptAsync(
            context, _db.ToRegistryEntry(), new MigrationScriptRequest { Idempotent = false }, CancellationToken.None);

        Assert.Contains("CREATE TABLE", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.Truncated);
        Assert.Equal(1, result.MigrationCount);

        // Non-mutation proof: the script is generated purely from the migration assembly's known
        // metadata - it must never touch the target database itself.
        Assert.False(await context.Database.CanConnectAsync());
    }

    [Fact]
    public async Task GenerateScriptAsync_Idempotent_ThrowsRedactedExceptionForSqliteLimitation()
    {
        // SQLite does not support idempotent migration scripts (EF Core limitation:
        // SqliteHistoryRepository.GetEndIfScript() throws NotSupportedException). The tool must
        // catch that and surface an actionable, redacted message rather than the raw provider
        // exception.
        using var context = NewContext();
        var inspector = NewInspector();

        var ex = await Assert.ThrowsAsync<MigrationInspectionException>(
            () => inspector.GenerateScriptAsync(context, _db.ToRegistryEntry(), new MigrationScriptRequest { Idempotent = true }, CancellationToken.None));

        Assert.Contains("idempotent", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idempotent: false", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("NotSupportedException", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateScriptAsync_WithUnknownToMigration_ThrowsRedactedException()
    {
        using var context = NewContext();
        var inspector = NewInspector();

        var ex = await Assert.ThrowsAsync<MigrationInspectionException>(
            () => inspector.GenerateScriptAsync(
                context, _db.ToRegistryEntry(), new MigrationScriptRequest { Idempotent = false, ToMigration = "bogus-migration-id" }, CancellationToken.None));

        Assert.Contains("bogus-migration-id", ex.Message, StringComparison.Ordinal);
        Assert.Contains("list_migrations", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateScriptAsync_ExceedingMaxScriptLength_TruncatesAtLineBoundaryAndSetsFlag()
    {
        using var fullContext = NewContext();
        var fullScript = (await NewInspector().GenerateScriptAsync(
            fullContext, _db.ToRegistryEntry(), new MigrationScriptRequest { Idempotent = false }, CancellationToken.None)).Sql;

        using var context = NewContext();
        var inspector = NewInspector(new MigrationsOptions { MaxScriptLength = 100 });

        var result = await inspector.GenerateScriptAsync(
            context, _db.ToRegistryEntry(), new MigrationScriptRequest { Idempotent = false }, CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.True(result.Sql.Length <= 100);
        // Cut on a line boundary rather than bisecting mid-statement: the truncated text should be
        // a clean prefix of the full script up to a newline, not an arbitrary character cutoff.
        Assert.StartsWith(result.Sql, fullScript, StringComparison.Ordinal);
        Assert.True(result.Sql.Length == 0 || fullScript[result.Sql.Length] == '\n' || fullScript[result.Sql.Length] == '\r');
    }
}
