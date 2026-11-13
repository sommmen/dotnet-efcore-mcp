using System.Text.Json.Serialization;

namespace DotnetEfCoreMcp.Server.Migrations;

public enum MigrationDirection
{
    Up,
    Down,
}

/// <summary>The operations one migration performs in a given direction.</summary>
public sealed class MigrationDescription
{
    public required string MigrationId { get; init; }

    public required MigrationDirection Direction { get; init; }

    public required IReadOnlyList<MigrationOperationInfo> Operations { get; init; }
}

/// <summary>A stable, provider-neutral projection of an EF Core <c>MigrationOperation</c>. Only the
/// fields relevant to <see cref="Kind"/> are populated; the rest stay <c>null</c>.</summary>
public sealed class MigrationOperationInfo
{
    /// <summary>The operation type name without the <c>Operation</c> suffix, e.g. <c>AddColumn</c>.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("schema")]
    public string? Schema { get; init; }

    [JsonPropertyName("table")]
    public string? Table { get; init; }

    /// <summary>The column, index, or foreign-key name the operation targets.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("columnType")]
    public string? ColumnType { get; init; }

    [JsonPropertyName("isNullable")]
    public bool? IsNullable { get; init; }

    [JsonPropertyName("defaultValueSql")]
    public string? DefaultValueSql { get; init; }

    [JsonPropertyName("newName")]
    public string? NewName { get; init; }

    [JsonPropertyName("columns")]
    public IReadOnlyList<MigrationColumnInfo>? Columns { get; init; }

    [JsonPropertyName("sql")]
    public string? Sql { get; init; }

    [JsonPropertyName("truncated")]
    public bool? Truncated { get; init; }
}

/// <summary>A column in a <c>CreateTable</c> (full definition) or <c>CreateIndex</c> (name only).</summary>
public sealed class MigrationColumnInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("columnType")]
    public string? ColumnType { get; init; }

    [JsonPropertyName("isNullable")]
    public bool? IsNullable { get; init; }

    [JsonPropertyName("defaultValueSql")]
    public string? DefaultValueSql { get; init; }
}
