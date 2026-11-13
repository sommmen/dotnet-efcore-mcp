using DotnetEfCoreMcp.Server.Connections;
using EfSchemaCompare;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace DotnetEfCoreMcp.Server.Migrations;

/// <summary>Compares a <see cref="DbContext"/>'s model with the live database catalog through
/// <see href="https://github.com/JonPSmith/EfCore.SchemaCompare">EfCore.SchemaCompare</see>, which
/// reads the catalog with EF Core's own reverse-engineering <c>IDatabaseModelFactory</c>. Catalog
/// and <c>__EFMigrationsHistory</c> reads only: nothing here issues DDL or DML.</summary>
public sealed class SchemaComparer(MigrationsOptions options, ILogger<SchemaComparer> logger)
{
    /// <summary>Takes ownership of <paramref name="context"/> and disposes it once the comparison finishes.</summary>
    public async Task<SchemaComparisonResult> CompareAsync(
        DbContext context,
        ConnectionRegistryEntry entry,
        DatabaseProvider provider,
        SchemaComparisonRequest request,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(entry.CommandTimeoutSeconds) + options.CancellationMargin;
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var token = linkedCts.Token;

        // The synchronous scaffolder cannot be interrupted. Its worker owns the context until it
        // finishes, even when the caller stops waiting, avoiding concurrent use/disposal on timeout.
        var worker = Task.Run(async () =>
        {
            using (context)
            {
                return await CompareCoreAsync(context, provider, request, token);
            }
        }, CancellationToken.None);
        try
        {
            return await worker.WaitAsync(token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ObserveAbandoned(worker);
            throw new MigrationInspectionException(
                $"Schema comparison timed out after {timeout.TotalSeconds:0} seconds. The library reads the whole catalog; " +
                "raise the connection's CommandTimeoutSeconds, or use list_migrations and describe_migration instead.");
        }
        catch (OperationCanceledException)
        {
            ObserveAbandoned(worker);
            throw;
        }
        catch (MigrationInspectionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "compare_schema failed for connection '{ConnectionName}'.", entry.Name);
            throw new MigrationInspectionException(
                "Comparing the model with the database catalog failed. Next step: call test_connection for this connection, " +
                "and check that the account can read catalog metadata. See the server logs for details.");
        }
    }

    private async Task<SchemaComparisonResult> CompareCoreAsync(
        DbContext context, DatabaseProvider provider, SchemaComparisonRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The SQLite scaffolder opens with ReadWriteCreate, so a missing file would be created empty.
        if (provider == DatabaseProvider.Sqlite && !context.GetService<IRelationalDatabaseCreator>().Exists())
        {
            throw new MigrationInspectionException(
                "The database does not exist, so there is no catalog to compare. Next step: call list_migrations to see which migrations are pending.");
        }

        var config = CreateConfig(provider, request.IncludeExtras);
        var comparer = new CompareEfSql(config);
        // Reads the connection string from the unopened DbConnection; SqlClient drops the password once opened.
        comparer.CompareEfWithDb(context);
        cancellationToken.ThrowIfCancellationRequested();
        var logs = comparer.Logs;

        var tablesByEntity = MapTablesByEntity(context.Model);
        var queryMappedEntities = FindQueryMappedEntities(context.Model);
        var entities = WithoutComparisonNoise(Flatten(logs, context.Model))
            .Where(e => e.Entry.Entity is null || !queryMappedEntities.Contains(e.Entry.Entity))
            .Where(e => IsVisible(e, request, tablesByEntity))
            .Where(e => MatchesTableFilter(e.Table, request.Tables))
            .ToList();

        var notInDatabase = entities.Where(e => e.State == CompareState.NotInDatabase).Select(e => e.Entry).ToList();
        var different = entities.Where(e => e.State == CompareState.Different).Select(e => e.Entry).ToList();
        var extraInDatabase = request.IncludeExtras
            ? entities.Where(e => e.State == CompareState.ExtraInDatabase).Select(e => e.Entry).ToList()
            : [];

        notInDatabase = await AttributeAsync(context, provider, notInDatabase, cancellationToken);

        var total = notInDatabase.Count + different.Count + extraInDatabase.Count;
        var max = Math.Max(1, options.MaxDriftEntries);
        var remaining = max;
        var cappedNotInDatabase = Take(notInDatabase, ref remaining);
        var cappedDifferent = Take(different, ref remaining);
        var cappedExtra = Take(extraInDatabase, ref remaining);

        var comparedTables = tablesByEntity
            .Where(pair => request.IsEntityVisible(pair.Key))
            .Select(pair => pair.Value)
            .Where(table => MatchesTableFilter(table, request.Tables))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return new SchemaComparisonResult
        {
            HasDifferences = total > 0,
            NotInDatabase = cappedNotInDatabase,
            Different = cappedDifferent,
            ExtraInDatabase = cappedExtra,
            ComparedTableCount = comparedTables,
            Truncated = total > max,
        };
    }

    internal static CompareEfSqlConfig CreateConfig(DatabaseProvider provider, bool includeExtras)
    {
        var config = new CompareEfSqlConfig { AlwaysRunStage2 = includeExtras };
        if (provider == DatabaseProvider.Sqlite)
        {
            // SQLite's scaffolder cannot recover constraint names; empty names aren't drift.
            config.AddIgnoreCompareLog(new CompareLog(CompareType.MatchAnything, CompareState.Different, null!, CompareAttributes.ConstraintName, null!, string.Empty));
            config.AddIgnoreCompareLog(new CompareLog(CompareType.Entity, CompareState.NotChecked, null!, CompareAttributes.ConstraintName));
        }

        return config;
    }

    private static void ObserveAbandoned(Task task) =>
        task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private async Task<List<SchemaDriftEntry>> AttributeAsync(DbContext context, DatabaseProvider provider, List<SchemaDriftEntry> notInDatabase, CancellationToken cancellationToken)
    {
        if (notInDatabase.Count == 0)
        {
            return notInDatabase;
        }

        var index = MigrationInspector.BuildOperationIndex(context, provider);
        var attributed = notInDatabase.Select(e =>
        {
            var (schema, table) = SplitTable(e.Table);
            var migrationId = e.Kind switch
            {
                "Table" => index.FindTable(schema, table),
                "Column" when e.Column is not null => index.FindColumn(schema, table, e.Column),
                _ => null,
            };
            return e with { IntroducedBy = migrationId };
        }).ToList();

        if (attributed.All(e => e.IntroducedBy is null))
        {
            return attributed;
        }

        var applied = await TryReadAppliedMigrationsAsync(context, cancellationToken);
        return applied is null
            ? attributed
            : attributed.Select(e => e.IntroducedBy is null ? e : e with { IntroducedByIsPending = !applied.Contains(e.IntroducedBy) }).ToList();
    }

    private async Task<HashSet<string>?> TryReadAppliedMigrationsAsync(DbContext context, CancellationToken cancellationToken)
    {
        try
        {
            var history = context.GetService<IHistoryRepository>();
            if (!await history.ExistsAsync(cancellationToken))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var rows = await history.GetAppliedMigrationsAsync(cancellationToken);
            return rows.Select(r => r.MigrationId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "compare_schema could not read __EFMigrationsHistory; introducedByIsPending is omitted.");
            return null;
        }
    }

    private static List<SchemaDriftEntry> Take(List<SchemaDriftEntry> entries, ref int remaining)
    {
        var taken = entries.Take(remaining).ToList();
        remaining -= taken.Count;
        return taken;
    }

    internal sealed record FlatEntry(CompareState State, SchemaDriftEntry Entry)
    {
        public string Table => Entry.Table;
    }

    private static IEnumerable<FlatEntry> Flatten(IEnumerable<CompareLog> logs, IModel model)
    {
        foreach (var log in logs)
        {
            switch (log.Type)
            {
                case CompareType.DbContext:
                case CompareType.Database:
                    foreach (var child in Flatten(log.SubLogs, model))
                    {
                        yield return child;
                    }

                    break;
                case CompareType.Entity when log.State == CompareState.NotInDatabase && log.Expected is not null:
                    yield return new FlatEntry(log.State, new SchemaDriftEntry { Kind = "Table", Table = log.Expected, Entity = log.Name });
                    break;
                // Entity-level Different logs duplicate the PrimaryKey child's constraint-name check.
                case CompareType.Entity when log.State == CompareState.Ok && log.Expected is not null:
                    foreach (var child in log.SubLogs.Where(IsReportable))
                    {
                        yield return new FlatEntry(child.State, ProjectEntityChild(child, log.Name, log.Expected, model));
                    }

                    break;
                case CompareType.Table when log.State == CompareState.ExtraInDatabase:
                    yield return new FlatEntry(log.State, new SchemaDriftEntry { Kind = "Table", Table = log.Name });
                    break;
                case CompareType.Column when log.State == CompareState.ExtraInDatabase:
                    yield return new FlatEntry(log.State, new SchemaDriftEntry { Kind = "Column", Table = log.Name, Column = log.Found });
                    break;
                case CompareType.Index when log.State == CompareState.ExtraInDatabase:
                    yield return new FlatEntry(log.State, new SchemaDriftEntry { Kind = "Index", Table = log.Name, Name = log.Found });
                    break;
            }
        }
    }

    /// <summary>Drops differences that aren't drift: column types differing only in case, and the pair
    /// EF Core's own <c>AddColumn</c> leaves behind when it adds a non-nullable column with a database
    /// default to fill existing rows (a <c>DefaultValueSql</c> the model doesn't declare, and the
    /// resulting <c>ValueGenerated</c> Never/OnAdd mismatch on the same column).</summary>
    internal static IEnumerable<FlatEntry> WithoutComparisonNoise(IEnumerable<FlatEntry> entries)
    {
        var list = entries.ToList();
        var defaultedColumns = list
            .Where(e => e.State == CompareState.Different
                && e.Entry.Attribute == nameof(CompareAttributes.DefaultValueSql)
                && string.IsNullOrEmpty(e.Entry.Expected))
            .Select(e => (e.Entry.Table, e.Entry.Column))
            .ToHashSet();

        return list.Where(e => !IsCaseOnlyTypeDifference(e) && !(e.State == CompareState.Different
            && defaultedColumns.Contains((e.Entry.Table, e.Entry.Column))
            && (e.Entry.Attribute == nameof(CompareAttributes.DefaultValueSql)
                || (e.Entry.Attribute == nameof(CompareAttributes.ValueGenerated) && e.Entry.Expected == "Never" && e.Entry.Found == "OnAdd"))));
    }

    private static bool IsCaseOnlyTypeDifference(FlatEntry entry) =>
        entry.State == CompareState.Different
        && entry.Entry.Attribute == nameof(CompareAttributes.ColumnType)
        && string.Equals(entry.Entry.Expected, entry.Entry.Found, StringComparison.OrdinalIgnoreCase);

    private static bool IsReportable(CompareLog log) =>
        log.State is CompareState.Different or CompareState.NotInDatabase or CompareState.ExtraInDatabase;

    private static SchemaDriftEntry ProjectEntityChild(CompareLog log, string entity, string table, IModel model)
    {
        var isDifferent = log.State == CompareState.Different;
        var entry = new SchemaDriftEntry
        {
            Kind = log.Type.ToString(),
            Table = table,
            Entity = entity,
            Attribute = isDifferent ? log.Attribute.ToString() : null,
            Expected = isDifferent ? log.Expected : null,
            Found = isDifferent ? log.Found : null,
        };

        return log.Type switch
        {
            CompareType.Property => entry with
            {
                Kind = "Column",
                Property = log.Name,
                Column = log.State == CompareState.NotInDatabase ? log.Expected : FindColumnName(model, entity, log.Name, table),
            },
            CompareType.Index => entry with
            {
                Name = log.Attribute == CompareAttributes.IndexConstraintName ? log.Expected : null,
                Column = log.Name,
            },
            _ => entry with
            {
                Name = log.Name,
                Column = log.Attribute is CompareAttributes.ColumnName or CompareAttributes.NotSet ? log.Expected ?? log.Found : null,
            },
        };
    }

    private static string? FindColumnName(IModel model, string entity, string property, string table)
    {
        var (schema, name) = SplitTable(table);
        var storeObject = StoreObjectIdentifier.Table(name, schema);
        return model.GetEntityTypes()
            .Where(t => t.ClrType.Name == entity)
            .Select(t => t.FindProperty(property))
            .Select(p => p?.GetColumnName(storeObject) ?? p?.GetColumnName())
            .FirstOrDefault(c => c is not null);
    }

    /// <summary>Entities mapped to <c>ToSqlQuery</c> or a table-valued function have no table of their
    /// own, but EfCore.SchemaCompare still expects one named after the entity.</summary>
    private static HashSet<string> FindQueryMappedEntities(IModel model) =>
        model.GetEntityTypes()
            .Where(IsQueryMapped)
            .Select(t => t.ClrType.Name)
            .ToHashSet(StringComparer.Ordinal);

    private static bool IsQueryMapped(IEntityType entityType) =>
        entityType.GetSqlQuery() is not null || entityType.GetFunctionName() is not null;

    private static Dictionary<string, string> MapTablesByEntity(IModel model)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entityType in model.GetEntityTypes())
        {
            if (IsQueryMapped(entityType))
            {
                continue;
            }

            var name = entityType.GetTableName() ?? entityType.GetViewName();
            if (name is null)
            {
                continue;
            }

            var schema = entityType.GetTableName() is not null ? entityType.GetSchema() : entityType.GetViewSchema();
            map.TryAdd(entityType.ClrType.Name, schema is null ? name : $"{schema}.{name}");
        }

        return map;
    }

    private static bool IsVisible(FlatEntry entry, SchemaComparisonRequest request, IReadOnlyDictionary<string, string> tablesByEntity)
    {
        if (entry.Entry.Entity is { } entity && !request.IsEntityVisible(entity))
        {
            return false;
        }

        return !tablesByEntity.Any(pair =>
            string.Equals(pair.Value, entry.Table, StringComparison.OrdinalIgnoreCase) && !request.IsEntityVisible(pair.Key));
    }

    internal static bool MatchesTableFilter(string table, IReadOnlyList<string>? filter)
    {
        if (filter is null || filter.Count == 0)
        {
            return true;
        }

        var (schema, name) = SplitTable(table);
        return filter.Any(f =>
        {
            var (filterSchema, filterName) = SplitTable(f.Trim());
            return string.Equals(name, filterName, StringComparison.OrdinalIgnoreCase)
                && (filterSchema is null || schema is null || string.Equals(schema, filterSchema, StringComparison.OrdinalIgnoreCase));
        });
    }

    private static (string? Schema, string Table) SplitTable(string table)
    {
        var dot = table.LastIndexOf('.');
        return dot > 0 ? (table[..dot], table[(dot + 1)..]) : (null, table);
    }
}
