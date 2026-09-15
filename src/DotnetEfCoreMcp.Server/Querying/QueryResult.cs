namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>The materialized result of a LINQPad-style query expression.</summary>
/// <param name="Entity">The queried entity name.</param>
/// <param name="RowCount">The number of materialized rows.</param>
/// <param name="EffectiveTake">The effective page size applied to sequence results.</param>
/// <param name="HasMoreRows">For a sequence result with a positive <paramref name="EffectiveTake"/>,
/// <c>true</c> only if at least one row remains after applying the final sequence ordering and
/// effective <c>skip</c>/<c>take</c> values; it is not a total-count indicator - <paramref name="Rows"/>
/// and <paramref name="RowCount"/> always contain at most <paramref name="EffectiveTake"/> rows regardless
/// of this flag. Always <c>false</c> for <c>take: 0</c> (no sentinel probe is issued) and for terminal
/// scalar aggregates/element operators (<paramref name="IsScalar"/> results), which have no page window.</param>
/// <param name="IsScalar">Whether the query returned a scalar value rather than rows.</param>
/// <param name="Scalar">The scalar result, when <paramref name="IsScalar"/> is <c>true</c>.</param>
/// <param name="Rows">The materialized row values for a sequence result.</param>
/// <param name="NextCursor">The cursor for the next page, when one is available.</param>
public sealed record QueryResult(
    string Entity,
    int RowCount,
    int? EffectiveTake,
    bool HasMoreRows,
    bool IsScalar,
    object? Scalar,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    string? NextCursor = null);

/// <summary>The result of previewing the SQL a query would issue, obtained from an unexecuted
/// <see cref="IQueryable"/>'s <c>ToQueryString()</c> without ever opening a database connection,
/// executing a command, or materializing any rows.</summary>
public sealed record QuerySqlPreviewResult(string Entity, string Sql);

/// <summary>Thrown for invalid, unsafe, or failed query expressions. Messages are sanitized and
/// never include connection strings or provider-generated SQL.</summary>
public sealed class QueryExecutionException : Exception
{
    public QueryExecutionException(string message) : base(message) { }
    public QueryExecutionException(string message, Exception innerException) : base(message, innerException) { }
}