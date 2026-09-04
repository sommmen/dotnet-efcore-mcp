namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>The materialized result of a LINQPad-style query expression.</summary>
/// <param name="HasMoreRows">For a sequence result with a positive <paramref name="EffectiveTake"/>,
/// <c>true</c> only if at least one row remains after applying the final sequence ordering and
/// effective <c>skip</c>/<c>take</c> values; it is not a total-count indicator - <see cref="Rows"/>
/// and <see cref="RowCount"/> always contain at most <paramref name="EffectiveTake"/> rows regardless
/// of this flag. Always <c>false</c> for <c>take: 0</c> (no sentinel probe is issued) and for terminal
/// scalar aggregates/element operators (<see cref="IsScalar"/> results), which have no page window.</param>
public sealed record QueryResult(
    string Entity,
    int RowCount,
    int? EffectiveTake,
    bool HasMoreRows,
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