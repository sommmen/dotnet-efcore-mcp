using DotnetEfCoreMcp.Server.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;

namespace DotnetEfCoreMcp.Server.Migrations;

/// <summary>Inspects EF Core migration state and previews migration scripts for a
/// <see cref="DbContext"/>. Every operation here is read-only: it never calls
/// <c>Migrate()</c>/<c>MigrateAsync()</c>, opens a transaction, executes raw SQL, or calls
/// <c>SaveChanges</c> - <see cref="GenerateScriptAsync"/> only ever produces a SQL string via
/// <see cref="IMigrator.GenerateScript"/>, which is itself pure string generation.</summary>
public sealed class MigrationInspector(MigrationsOptions options, ILogger<MigrationInspector> logger)
{
    private const string ProductVersionAnnotationName = "ProductVersion";

    public async Task<MigrationsInspectionResult> InspectAsync(DbContext context, ConnectionRegistryEntry entry, DatabaseProvider provider, CancellationToken cancellationToken)
    {
        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var historyRepository = context.GetService<IHistoryRepository>();

        // Migrations is a SortedList keyed by migration ID, so this is already in EF Core's
        // migration ordering (ascending, chronological by convention).
        var knownMigrations = migrationsAssembly.Migrations;

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(entry.CommandTimeoutSeconds) + options.CancellationMargin);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var databaseExists = await context.Database.CanConnectAsync(linkedCts.Token);

        var appliedStateAvailable = false;
        var appliedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appliedMigrations = new List<AppliedMigrationInfo>();

        if (databaseExists)
        {
            try
            {
                if (await historyRepository.ExistsAsync(linkedCts.Token))
                {
                    var rows = await historyRepository.GetAppliedMigrationsAsync(linkedCts.Token);
                    foreach (var row in rows)
                    {
                        appliedIds.Add(row.MigrationId);
                        appliedMigrations.Add(new AppliedMigrationInfo { MigrationId = row.MigrationId, ProductVersion = row.ProductVersion });
                    }

                    appliedStateAvailable = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The database is reachable but reading __EFMigrationsHistory failed (permissions,
                // provider quirk, etc.). Surface that applied state is unavailable rather than
                // silently reporting an empty/misleading applied list.
                logger.LogWarning(ex, "Failed to read applied migration history for connection '{ConnectionName}'.", entry.Name);
            }
        }

        var pendingMigrations = new List<PendingMigrationInfo>();
        foreach (var (migrationId, migrationType) in knownMigrations)
        {
            if (appliedIds.Contains(migrationId))
            {
                continue;
            }

            var migration = migrationsAssembly.CreateMigration(migrationType, ToProviderAssemblyName(provider));
            var productVersion = migration.TargetModel.FindAnnotation(ProductVersionAnnotationName)?.Value as string ?? "unknown";
            pendingMigrations.Add(new PendingMigrationInfo { MigrationId = migrationId, Target = $"{provider}:{productVersion}" });
        }

        return new MigrationsInspectionResult
        {
            AppliedMigrations = appliedMigrations,
            PendingMigrations = pendingMigrations,
            DatabaseExists = databaseExists,
            AppliedStateAvailable = appliedStateAvailable,
        };
    }

    public async Task<MigrationScriptResult> GenerateScriptAsync(DbContext context, ConnectionRegistryEntry entry, MigrationScriptRequest request, CancellationToken cancellationToken)
    {
        var migrator = context.GetService<IMigrator>();
        var migrationsAssembly = context.GetService<IMigrationsAssembly>();

        var generationOptions = request.Idempotent
            ? MigrationsSqlGenerationOptions.Idempotent
            : MigrationsSqlGenerationOptions.Default;

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(entry.CommandTimeoutSeconds) + options.CancellationMargin);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        linkedCts.Token.ThrowIfCancellationRequested();

        string sql;
        try
        {
            // IMigrator.GenerateScript is synchronous, string-generation-only work (no I/O against
            // the target database). WaitAsync makes cancellation observable to the caller even
            // though EF Core cannot cooperatively cancel a generation already in progress.
            sql = await Task.Run(
                    () => migrator.GenerateScript(request.FromMigration, request.ToMigration, generationOptions))
                .WaitAsync(linkedCts.Token);
        }
        catch (NotSupportedException ex)
        {
            var message = request.Idempotent
                ? "This connection's database provider does not support idempotent migration scripts. Retry with idempotent: false."
                : "This connection's database provider does not support migration script generation.";
            throw new MigrationInspectionException(message, ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new MigrationInspectionException(
                $"{ex.Message} Next step: call list_migrations to confirm valid migration IDs for fromMigration/toMigration.",
                ex);
        }

        var migrationCount = CountMigrationsInRange(migrationsAssembly, request.FromMigration, request.ToMigration);

        var maxLength = Math.Max(1, options.MaxScriptLength);
        var truncated = sql.Length > maxLength;
        if (truncated)
        {
            sql = TruncateAtStatementBoundary(sql, maxLength);
        }

        return new MigrationScriptResult
        {
            Sql = sql,
            Truncated = truncated,
            MigrationCount = migrationCount,
        };
    }

    /// <summary>Best-effort count of migrations covered by [fromMigration (exclusive), toMigration
    /// (inclusive)], mirroring <see cref="IMigrator.GenerateScript"/>'s own resolution semantics
    /// closely enough for reporting purposes without duplicating its exception behavior.</summary>
    private static int CountMigrationsInRange(IMigrationsAssembly migrationsAssembly, string? fromMigration, string? toMigration)
    {
        var orderedIds = migrationsAssembly.Migrations.Keys.ToList();

        var fromIndex = string.IsNullOrEmpty(fromMigration) || fromMigration == "0"
            ? -1
            : orderedIds.FindIndex(id => string.Equals(id, migrationsAssembly.FindMigrationId(fromMigration) ?? fromMigration, StringComparison.OrdinalIgnoreCase));

        var toIndex = string.IsNullOrEmpty(toMigration)
            ? orderedIds.Count - 1
            : orderedIds.FindIndex(id => string.Equals(id, migrationsAssembly.FindMigrationId(toMigration) ?? toMigration, StringComparison.OrdinalIgnoreCase));

        if (toIndex < 0 || toIndex <= fromIndex)
        {
            return 0;
        }

        return toIndex - fromIndex;
    }

    /// <summary>Truncates generated SQL to at most <paramref name="maxLength"/> characters,
    /// preferring to cut at the last statement-separator boundary at or before the cap rather than
    /// bisecting a statement.</summary>
    private static string TruncateAtStatementBoundary(string sql, int maxLength)
    {
        var window = sql[..maxLength];
        var lastBoundary = window.LastIndexOf('\n');
        return lastBoundary > 0 ? window[..lastBoundary] : window;
    }

    /// <summary>Maps a <see cref="DatabaseProvider"/> to the EF Core provider assembly name
    /// <see cref="IMigrationsAssembly.CreateMigration"/> expects as its "active provider" -
    /// mirroring the reverse mapping in <see cref="ProviderInference"/>.</summary>
    private static string ToProviderAssemblyName(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.Sqlite => "Microsoft.EntityFrameworkCore.Sqlite",
        DatabaseProvider.SqlServer => "Microsoft.EntityFrameworkCore.SqlServer",
        DatabaseProvider.PostgreSql => "Npgsql.EntityFrameworkCore.PostgreSQL",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported database provider."),
    };
}
