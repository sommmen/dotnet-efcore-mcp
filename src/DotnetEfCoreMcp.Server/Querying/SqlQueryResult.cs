namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>The serialized result of a raw SQL command.</summary>
public sealed class SqlQueryResult
{
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }

    public int ReturnedRowCount => Rows.Count;

    public int? AffectedRows { get; init; }

    public bool HasMoreRows { get; init; }

    public int MaxRows { get; init; }
}
