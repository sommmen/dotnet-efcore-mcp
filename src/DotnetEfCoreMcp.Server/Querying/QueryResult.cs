namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>The materialized result of a LINQPad-style query expression.</summary>
public sealed record QueryResult(
    string Entity,
    int RowCount,
    int? EffectiveTake,
    bool IsScalar,
    object? Scalar,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

/// <summary>Thrown for invalid, unsafe, or failed query expressions. Messages are sanitized and
/// never include connection strings or provider-generated SQL.</summary>
public sealed class QueryExecutionException : Exception
{
    public QueryExecutionException(string message) : base(message) { }
    public QueryExecutionException(string message, Exception innerException) : base(message, innerException) { }
}