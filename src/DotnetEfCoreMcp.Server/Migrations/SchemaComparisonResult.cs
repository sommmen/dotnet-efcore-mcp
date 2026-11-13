using System.Text.Json.Serialization;

namespace DotnetEfCoreMcp.Server.Migrations;

/// <summary>One model-vs-database difference reported by <c>compare_schema</c>. Only the fields
/// relevant to <see cref="Kind"/> are populated; the rest stay <c>null</c>.</summary>
public sealed record SchemaDriftEntry
{
    /// <summary><c>Table</c>, <c>Column</c>, <c>PrimaryKey</c>, <c>ForeignKey</c>, or <c>Index</c>.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>The table, schema-qualified (<c>schema.table</c>) when it isn't in the default schema.</summary>
    [JsonPropertyName("table")]
    public required string Table { get; init; }

    [JsonPropertyName("column")]
    public string? Column { get; init; }

    /// <summary>The constraint or index name for <c>PrimaryKey</c>, <c>ForeignKey</c>, and <c>Index</c> entries.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("entity")]
    public string? Entity { get; init; }

    [JsonPropertyName("property")]
    public string? Property { get; init; }

    /// <summary>What was compared, e.g. <c>ColumnType</c>, <c>Nullability</c>, <c>DefaultValueSql</c>.</summary>
    [JsonPropertyName("attribute")]
    public string? Attribute { get; init; }

    [JsonPropertyName("expected")]
    public string? Expected { get; init; }

    [JsonPropertyName("found")]
    public string? Found { get; init; }

    /// <summary>The latest migration whose operations create this table or column; <c>null</c> when
    /// no migration does (a model change without a migration).</summary>
    [JsonPropertyName("introducedBy")]
    public string? IntroducedBy { get; init; }

    /// <summary>Whether <see cref="IntroducedBy"/> is still pending; <c>null</c> when applied state
    /// couldn't be read.</summary>
    [JsonPropertyName("introducedByIsPending")]
    public bool? IntroducedByIsPending { get; init; }
}

/// <summary>The result of comparing a <c>DbContext</c> model with the live database catalog.</summary>
public sealed class SchemaComparisonResult
{
    public required bool HasDifferences { get; init; }

    public required IReadOnlyList<SchemaDriftEntry> NotInDatabase { get; init; }

    public required IReadOnlyList<SchemaDriftEntry> Different { get; init; }

    public required IReadOnlyList<SchemaDriftEntry> ExtraInDatabase { get; init; }

    public required int ComparedTableCount { get; init; }

    public required bool Truncated { get; init; }
}

/// <summary>Options for one <c>compare_schema</c> run.</summary>
public sealed class SchemaComparisonRequest
{
    /// <summary>Table names (optionally schema-qualified) to report on, matched
    /// case-insensitively; <c>null</c> or empty reports every table.</summary>
    public IReadOnlyList<string>? Tables { get; init; }

    public bool IncludeExtras { get; init; }

    /// <summary>Whether an entity (by CLR type name) may be disclosed under the connection's access policy.</summary>
    public Func<string, bool> IsEntityVisible { get; init; } = _ => true;
}
