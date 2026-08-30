namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Server-wide limits enforced on every query, regardless of what the caller requests.</summary>
public sealed class QueryExecutionOptions
{
    /// <summary>Absolute maximum number of rows any single query can return. Enforced even if the
    /// caller omits <see cref="QueryRequest.Take"/> or requests more than this.</summary>
    public int MaxTake { get; init; } = 200;

    /// <summary>Row count used when the caller doesn't specify <see cref="QueryRequest.Take"/>.</summary>
    public int DefaultTake { get; init; } = 50;

    /// <summary>Extra wall-clock margin added on top of a connection's configured EF Core command
    /// timeout before the query is cancelled from the server side as a defense-in-depth measure
    /// (the primary enforcement mechanism is the provider's own command timeout).</summary>
    public TimeSpan CancellationMargin { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum number of items projected from a single included collection navigation
    /// (e.g. <c>include=["Orders"]</c>). Bounds the *width* of an included one-to-many navigation
    /// the same way <see cref="MaxTake"/> bounds the number of top-level rows - without this, a
    /// single row with a very large collection navigation could bypass the row cap entirely.</summary>
    public int MaxIncludedCollectionItems { get; init; } = 200;
}
