using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace DotnetEfCoreMcp.Server.Migrations;

/// <summary>Maps tables and columns to the latest migration whose <c>Up</c> operations produce
/// that name (<c>CreateTable</c> and its columns, <c>AddColumn</c>, <c>RenameColumn</c>,
/// <c>RenameTable</c>). Table and column names match case-insensitively; a <c>null</c> schema on
/// either the lookup or the recorded operation matches any schema.</summary>
public sealed class MigrationOperationIndex
{
    private readonly Dictionary<string, List<Entry>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Table, string Column), List<Entry>> _columns = new(TableColumnComparer.Instance);
    private int _ordinal;

    private MigrationOperationIndex()
    {
    }

    /// <summary>Builds the index from migrations given in EF Core migration order.</summary>
    public static MigrationOperationIndex Build(IEnumerable<(string MigrationId, IEnumerable<MigrationOperation> UpOperations)> migrations)
    {
        var index = new MigrationOperationIndex();
        foreach (var (migrationId, operations) in migrations)
        {
            foreach (var operation in operations)
            {
                index.Apply(migrationId, operation);
            }
        }

        return index;
    }

    public string? FindTable(string? schema, string table) =>
        _tables.TryGetValue(table, out var entries) ? Latest(entries, schema) : null;

    public string? FindColumn(string? schema, string table, string column) =>
        _columns.TryGetValue((table, column), out var entries) ? Latest(entries, schema) : null;

    private void Apply(string migrationId, MigrationOperation operation)
    {
        switch (operation)
        {
            case CreateTableOperation create:
                AddTable(create.Schema, create.Name, migrationId);
                foreach (var column in create.Columns)
                {
                    AddColumn(create.Schema, create.Name, column.Name, migrationId);
                }

                break;
            case AddColumnOperation add:
                AddColumn(add.Schema, add.Table, add.Name, migrationId);
                break;
            case RenameColumnOperation rename:
                AddColumn(rename.Schema, rename.Table, rename.NewName, migrationId);
                break;
            case RenameTableOperation { NewName: not null } rename:
                var newSchema = rename.NewSchema ?? rename.Schema;
                AddTable(newSchema, rename.NewName, migrationId);
                var carriedColumns = _columns
                    .Where(pair => string.Equals(pair.Key.Table, rename.Name, StringComparison.OrdinalIgnoreCase)
                        && pair.Value.Any(e => SchemaMatches(e.Schema, rename.Schema)))
                    .Select(pair => pair.Key.Column)
                    .ToList();
                foreach (var column in carriedColumns)
                {
                    AddColumn(newSchema, rename.NewName, column, migrationId);
                }

                break;
        }
    }

    private void AddTable(string? schema, string table, string migrationId)
    {
        if (!_tables.TryGetValue(table, out var entries))
        {
            _tables[table] = entries = [];
        }

        entries.Add(new Entry(schema, migrationId, _ordinal++));
    }

    private void AddColumn(string? schema, string table, string column, string migrationId)
    {
        if (!_columns.TryGetValue((table, column), out var entries))
        {
            _columns[(table, column)] = entries = [];
        }

        entries.Add(new Entry(schema, migrationId, _ordinal++));
    }

    private static string? Latest(List<Entry> entries, string? schema) =>
        entries.Where(e => SchemaMatches(e.Schema, schema)).MaxBy(e => e.Ordinal)?.MigrationId;

    private static bool SchemaMatches(string? recorded, string? requested) =>
        recorded is null || requested is null || string.Equals(recorded, requested, StringComparison.OrdinalIgnoreCase);

    private sealed record Entry(string? Schema, string MigrationId, int Ordinal);

    private sealed class TableColumnComparer : IEqualityComparer<(string Table, string Column)>
    {
        public static readonly TableColumnComparer Instance = new();

        public bool Equals((string Table, string Column) x, (string Table, string Column) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Table, y.Table) && StringComparer.OrdinalIgnoreCase.Equals(x.Column, y.Column);

        public int GetHashCode((string Table, string Column) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Table), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Column));
    }
}
