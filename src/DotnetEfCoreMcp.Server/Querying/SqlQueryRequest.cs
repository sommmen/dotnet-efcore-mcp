namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>A parameterized raw SQL command. Values are supplied separately and are never
/// interpolated into the SQL text.</summary>
public sealed class SqlQueryRequest
{
    public required string Sql { get; init; }

    public object?[]? Parameters { get; init; }
}
