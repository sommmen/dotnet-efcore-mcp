namespace DotnetEfCoreMcp.Server.Querying;

public sealed record QueryResult(
    string Entity,
    int RowCount,
    int EffectiveTake,
    int EffectiveSkip,
    IReadOnlyList<string> IncludedNavigations,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

/// <summary>Thrown for any invalid or unsafe query request (unknown entity, unknown/invalid
/// `Include` navigation, malformed Dynamic LINQ expression, provider/query failure). Messages are
/// sanitized - never includes connection strings, and EF Core/provider exceptions are summarized
/// rather than passed through with full stack traces.</summary>
public sealed class QueryExecutionException : Exception
{
    public QueryExecutionException(string message)
        : base(message)
    {
    }

    public QueryExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
