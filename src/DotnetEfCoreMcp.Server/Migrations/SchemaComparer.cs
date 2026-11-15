using System.Globalization;
using System.Text.RegularExpressions;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.Schema;
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
        var entities = WithoutComparisonNoise(Flatten(logs, context.Model), FindComputedColumns(context.Model))
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
        var entityNames = MapEntityNames(model);
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
                case CompareType.Entity:
                    foreach (var child in FlattenEntity(log, model, entityNames))
                    {
                        yield return child;
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

    // EfCore.SchemaCompare nests every entity that follows a NotChecked one (keyless or view-mapped) beneath
    // that entity's log, so entity logs are walked recursively; stopping at a NotChecked entity hides all of them.
    private static IEnumerable<FlatEntry> FlattenEntity(CompareLog log, IModel model, IReadOnlyDictionary<(string ClrName, string Table), string> entityNames)
    {
        if (log.State == CompareState.NotInDatabase && log.Expected is not null)
        {
            yield return new FlatEntry(log.State, new SchemaDriftEntry { Kind = "Table", Table = log.Expected, Entity = ResolveEntityName(entityNames, log.Name, log.Expected) });
        }
        // Entity-level Different logs duplicate the PrimaryKey child's constraint-name check.
        else if (log.State == CompareState.Ok && log.Expected is not null)
        {
            var entityName = ResolveEntityName(entityNames, log.Name, log.Expected);
            foreach (var child in log.SubLogs.Where(sub => sub.Type != CompareType.Entity && IsReportable(sub)))
            {
                yield return new FlatEntry(child.State, ProjectEntityChild(child, entityName, log.Expected, model));
            }
        }

        foreach (var nested in log.SubLogs.Where(sub => sub.Type == CompareType.Entity))
        {
            foreach (var entry in FlattenEntity(nested, model, entityNames))
            {
                yield return entry;
            }
        }
    }

    private static string ResolveEntityName(IReadOnlyDictionary<(string ClrName, string Table), string> entityNames, string clrName, string table) =>
        entityNames.TryGetValue((clrName, table), out var name) ? name : clrName;

    /// <summary>(CLR type name, table) to the entity name callers see, so shared-type and generic
    /// entities are reported under their EF entity name instead of the CLR type name they share.</summary>
    private static Dictionary<(string ClrName, string Table), string> MapEntityNames(IModel model)
    {
        var map = new Dictionary<(string, string), string>();
        foreach (var entityType in model.GetEntityTypes())
        {
            if (TableOf(entityType) is { } table)
            {
                map.TryAdd((entityType.ClrType.Name, table), SchemaBuilder.EntityName(entityType));
            }
        }

        return map;
    }

    private static string? TableOf(IEntityType entityType)
    {
        var name = entityType.GetTableName() ?? entityType.GetViewName();
        if (name is null)
        {
            return null;
        }

        var schema = entityType.GetTableName() is not null ? entityType.GetSchema() : entityType.GetViewSchema();
        return schema is null ? name : $"{schema}.{name}";
    }

    private static HashSet<(string Table, string Column)> FindComputedColumns(IModel model)
    {
        var computed = new HashSet<(string, string)>();
        foreach (var entityType in model.GetEntityTypes().Where(t => !IsQueryMapped(t)))
        {
            if (TableOf(entityType) is not { } table || entityType.GetTableName() is not { } tableName)
            {
                continue;
            }

            var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
            foreach (var property in entityType.GetProperties().Where(p => p.GetComputedColumnSql(storeObject) is not null))
            {
                computed.Add((table, property.GetColumnName(storeObject) ?? property.Name));
            }
        }

        return computed;
    }
    /// <summary>Drops differences that aren't drift: column types differing only in case, the pair
    /// EF Core's own <c>AddColumn</c> leaves behind when it adds a non-nullable column with a database
    /// default to fill existing rows (a <c>DefaultValueSql</c> the model doesn't declare, and the
    /// resulting <c>ValueGenerated</c> Never/OnAdd mismatch on the same column), SQL expressions the
    /// database merely reformatted, client-only delete behaviors the database cannot record, and the
    /// nullability and type the database infers for computed columns.</summary>
    internal static IEnumerable<FlatEntry> WithoutComparisonNoise(IEnumerable<FlatEntry> entries, IReadOnlySet<(string Table, string Column)>? computedColumns = null)
    {
        var list = entries.ToList();
        var defaultedColumns = list
            .Where(e => e.State == CompareState.Different
                && e.Entry.Attribute == nameof(CompareAttributes.DefaultValueSql)
                && string.IsNullOrEmpty(e.Entry.Expected))
            .Select(e => (e.Entry.Table, e.Entry.Column))
            .ToHashSet();

        return list.Where(e => !IsEquivalentDifference(e) && !(e.State == CompareState.Different
            && ((e.Entry.Attribute is nameof(CompareAttributes.Nullability) or nameof(CompareAttributes.ColumnType) && computedColumns is not null && e.Entry.Column is not null && computedColumns.Contains((e.Entry.Table, e.Entry.Column)))
                || (defaultedColumns.Contains((e.Entry.Table, e.Entry.Column))
                    && (e.Entry.Attribute == nameof(CompareAttributes.DefaultValueSql)
                        || (e.Entry.Attribute == nameof(CompareAttributes.ValueGenerated) && e.Entry.Expected == "Never" && e.Entry.Found == "OnAdd"))))));
    }

    private static bool IsEquivalentDifference(FlatEntry entry)
    {
        if (entry.State != CompareState.Different)
        {
            return false;
        }

        var expected = entry.Entry.Expected;
        var found = entry.Entry.Found;
        return entry.Entry.Attribute switch
        {
            nameof(CompareAttributes.ColumnType) => string.Equals(expected, found, StringComparison.OrdinalIgnoreCase),
            nameof(CompareAttributes.DefaultValueSql) or nameof(CompareAttributes.ComputedColumnSql) =>
                expected is not null && found is not null && SqlExpressionsMatch(expected, found),
            // The catalog cannot represent these model-only behaviors; the database stores NO ACTION for them.
            nameof(CompareAttributes.DeleteBehavior) =>
                expected is "ClientCascade" or "ClientSetNull" or "ClientNoAction" && found == "NoAction",
            _ => false,
        };
    }

    /// <summary>Whether two SQL expressions are the same once the formatting a database applies when it
    /// stores one (quoting, case, redundant parentheses, <c>CAST</c> rewritten as <c>CONVERT</c>,
    /// <c>YEAR</c>/<c>MONTH</c>/<c>DAY</c> rewritten as <c>DATEPART</c>, float literal notation) is
    /// removed. The expressions are compared as normalized text, not parsed.</summary>
    internal static bool SqlExpressionsMatch(string expected, string found)
    {
        var left = NormalizeSql(expected);
        var right = NormalizeSql(found);
        if (left == right)
        {
            return true;
        }

        // EfCore.SchemaCompare trims one leading and one trailing parenthesis off the stored expression, which unbalances
        // it when the stored form was ([a]+x)+y. Only then is precedence ignored.
        if (!AreParenthesesBalanced(right) && StripParentheses(left) == StripParentheses(right))
        {
            return true;
        }

        return double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var leftNumber)
            && double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var rightNumber)
            && leftNumber == rightNumber;
    }

    private static string NormalizeSql(string sql)
    {
        var text = Regex.Replace(sql.ToLowerInvariant(), @"[\[\]""`]", string.Empty);
        text = Regex.Replace(text, @"cast\s*\(([^()]+?)\s+as\s+([a-z0-9_]+(?:\(\s*\d+(?:\s*,\s*\d+)?\s*\))?)\s*\)", "convert($2,$1)");
        text = Regex.Replace(text, @"datepart\s*\(\s*(year|month|day)\s*,", "$1(");
        string previous;
        do
        {
            previous = text;
            // A parenthesized lone literal ((0), ('x'), (1.5)) carries no meaning. The lookbehind keeps a function call's own parentheses.
            text = Regex.Replace(text, @"(?<![a-z0-9_])\(\s*((?:-?\d+(?:\.\d+)?(?:e[+-]?\d+)?)|(?:n?'[^']*'))\s*\)", "$1");
        }
        while (text != previous);

        text = Regex.Replace(text, @"\s+", string.Empty);
        text = RemoveLeftOperandGroups(text);
        while (text.Length > 1 && text[0] == '(' && text[^1] == ')' && AreOuterParenthesesPaired(text))
        {
            text = text[1..^1];
        }

        return text;
    }
    /// <summary>Removes parentheses around the left operand of <c>+</c>/<c>-</c> when that operand starts an
    /// expression, the grouping SQL Server adds when it stores <c>a+b+c</c> as <c>((a)+b)+c</c>. Additive operators
    /// are left-associative, so the grouping never changes meaning; a group after another operator is kept.</summary>
    private static string RemoveLeftOperandGroups(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == '(' && (index == 0 || text[index - 1] is '(' or ','))
            {
                var close = FindClosingParenthesis(text, index);
                if (close > 0 && close + 1 < text.Length && text[close + 1] is '+' or '-')
                {
                    text = string.Concat(text.AsSpan(0, index), text.AsSpan(index + 1, close - index - 1), text.AsSpan(close + 1));
                    continue;
                }
            }

            index++;
        }

        return text;
    }

    private static int FindClosingParenthesis(string text, int open)
    {
        var depth = 0;
        for (var index = open; index < text.Length; index++)
        {
            depth += text[index] == '(' ? 1 : text[index] == ')' ? -1 : 0;
            if (depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static string StripParentheses(string text) => text.Replace("(", string.Empty).Replace(")", string.Empty);

    private static bool AreParenthesesBalanced(string text)
    {
        var depth = 0;
        foreach (var character in text)
        {
            depth += character == '(' ? 1 : character == ')' ? -1 : 0;
            if (depth < 0)
            {
                return false;
            }
        }

        return depth == 0;
    }

    private static bool AreOuterParenthesesPaired(string text)
    {
        var depth = 0;
        for (var index = 0; index < text.Length - 1; index++)
        {
            depth += text[index] == '(' ? 1 : text[index] == ')' ? -1 : 0;
            if (depth == 0)
            {
                return false;
            }
        }

        return true;
    }
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
            .Where(t => SchemaBuilder.EntityName(t) == entity || t.ClrType.Name == entity)
            .Select(t => t.FindProperty(property))
            .Select(p => p?.GetColumnName(storeObject) ?? p?.GetColumnName())
            .FirstOrDefault(c => c is not null);
    }

    /// <summary>Entities mapped to <c>ToSqlQuery</c> or a table-valued function have no table of their
    /// own, but EfCore.SchemaCompare still expects one named after the entity.</summary>
    private static HashSet<string> FindQueryMappedEntities(IModel model) =>
        model.GetEntityTypes()
            .Where(IsQueryMapped)
            .SelectMany(t => new[] { t.ClrType.Name, SchemaBuilder.EntityName(t) })
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
            map.TryAdd(SchemaBuilder.EntityName(entityType), schema is null ? name : $"{schema}.{name}");
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
