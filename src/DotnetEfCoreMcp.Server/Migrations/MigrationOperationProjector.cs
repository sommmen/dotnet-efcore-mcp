using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace DotnetEfCoreMcp.Server.Migrations;

/// <summary>Projects EF Core <see cref="MigrationOperation"/>s into <see cref="MigrationOperationInfo"/>.</summary>
public static class MigrationOperationProjector
{
    private const string OperationSuffix = "Operation";

    public static MigrationOperationInfo Project(MigrationOperation operation, int maxSqlLength) => operation switch
    {
        CreateTableOperation create => new MigrationOperationInfo
        {
            Kind = KindOf(create),
            Schema = create.Schema,
            Table = create.Name,
            Columns = create.Columns.Select(c => new MigrationColumnInfo
            {
                Name = c.Name,
                ColumnType = c.ColumnType,
                IsNullable = c.IsNullable,
                DefaultValueSql = c.DefaultValueSql,
            }).ToList(),
        },
        DropTableOperation drop => new MigrationOperationInfo { Kind = KindOf(drop), Schema = drop.Schema, Table = drop.Name },
        RenameTableOperation rename => new MigrationOperationInfo
        {
            Kind = KindOf(rename),
            Schema = rename.Schema,
            Table = rename.Name,
            NewName = rename.NewName,
        },
        ColumnOperation column => new MigrationOperationInfo
        {
            Kind = KindOf(column),
            Schema = column.Schema,
            Table = column.Table,
            Name = column.Name,
            ColumnType = column.ColumnType,
            IsNullable = column.IsNullable,
            DefaultValueSql = column.DefaultValueSql,
        },
        DropColumnOperation drop => new MigrationOperationInfo { Kind = KindOf(drop), Schema = drop.Schema, Table = drop.Table, Name = drop.Name },
        RenameColumnOperation rename => new MigrationOperationInfo
        {
            Kind = KindOf(rename),
            Schema = rename.Schema,
            Table = rename.Table,
            Name = rename.Name,
            NewName = rename.NewName,
        },
        CreateIndexOperation index => new MigrationOperationInfo
        {
            Kind = KindOf(index),
            Schema = index.Schema,
            Table = index.Table,
            Name = index.Name,
            Columns = index.Columns.Select(c => new MigrationColumnInfo { Name = c }).ToList(),
        },
        DropIndexOperation drop => new MigrationOperationInfo { Kind = KindOf(drop), Schema = drop.Schema, Table = drop.Table, Name = drop.Name },
        AddForeignKeyOperation fk => new MigrationOperationInfo
        {
            Kind = KindOf(fk),
            Schema = fk.Schema,
            Table = fk.Table,
            Name = fk.Name,
            Columns = fk.Columns.Select(c => new MigrationColumnInfo { Name = c }).ToList(),
        },
        DropForeignKeyOperation drop => new MigrationOperationInfo { Kind = KindOf(drop), Schema = drop.Schema, Table = drop.Table, Name = drop.Name },
        SqlOperation sql => ProjectSql(sql, maxSqlLength),
        _ => new MigrationOperationInfo { Kind = KindOf(operation) },
    };

    private static MigrationOperationInfo ProjectSql(SqlOperation operation, int maxSqlLength)
    {
        var sql = operation.Sql ?? string.Empty;
        var maxLength = Math.Max(1, maxSqlLength);
        var truncated = sql.Length > maxLength;
        return new MigrationOperationInfo
        {
            Kind = KindOf(operation),
            Sql = truncated ? MigrationInspector.TruncateAtStatementBoundary(sql, maxLength) : sql,
            Truncated = truncated,
        };
    }

    private static string KindOf(MigrationOperation operation)
    {
        var name = operation.GetType().Name;
        return name.Length > OperationSuffix.Length && name.EndsWith(OperationSuffix, StringComparison.Ordinal)
            ? name[..^OperationSuffix.Length]
            : name;
    }
}
