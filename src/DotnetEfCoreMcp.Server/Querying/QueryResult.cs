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

/// <summary>Renders an exception chain as the actionable diagnostic a query caller needs.
/// <para>EF Core wraps translation failures several layers deep - a
/// <see cref="System.Reflection.TargetInvocationException"/> or <see cref="AggregateException"/>
/// around the real <see cref="InvalidOperationException"/> - and only the innermost message names
/// the offending sub-expression. Reporting just the outer wrapper makes a genuinely broken query
/// indistinguishable from a healthy tool refusing to work (issue #85).</para>
/// <para>Lives here, beside <see cref="QueryExecutionException"/>, so the isolated query host and the
/// MCP server flatten identically: the host must preserve the detail when it serializes a failure to
/// its single <c>Error</c> string, or no amount of server-side formatting can recover it.</para>
/// <para>Translation and SQL diagnostics describe the <em>query</em>, not the data, so they carry no
/// row-level information.</para></summary>
public static class QueryExceptionDetail
{
    /// <summary>Returns <c>TypeName: message</c> for the innermost meaningful exception in
    /// <paramref name="exception"/>'s chain, or <c>null</c> when there is no usable detail.</summary>
    public static string? Describe(Exception? exception)
    {
        var cause = Unwrap(exception);
        if (cause is null)
            return null;

        var detail = cause.Message.Trim();
        return string.IsNullOrEmpty(detail) ? null : $"{cause.GetType().Name}: {detail}";
    }

    /// <summary>Walks past exception types that exist purely to wrap another exception and carry no
    /// diagnostic text of their own, so callers see the provider's message rather than
    /// "Exception has been thrown by the target of an invocation."</summary>
    public static Exception? Unwrap(Exception? exception)
    {
        while (exception is System.Reflection.TargetInvocationException or AggregateException
            && exception.InnerException is not null)
        {
            exception = exception.InnerException;
        }

        return exception;
    }
}