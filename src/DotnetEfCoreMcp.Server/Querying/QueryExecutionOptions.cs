namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Server-wide limits enforced on every query, regardless of what the caller requests.</summary>
public sealed class QueryExecutionOptions
{
    /// <summary>Which engine <c>run_query</c> uses to execute the caller's query. See
    /// <c>docs/development/roslyn-user-query.md</c>; defaults to <see cref="QueryEngine.DynamicLinq"/>
    /// during the migration so the two engines can be validated side by side before the default
    /// flips.</summary>
    public QueryEngine Engine { get; init; } = QueryEngine.DynamicLinq;

    /// <summary>Whether a compiled <see cref="QueryEngine.Roslyn"/> query is allowed to call
    /// <c>SaveChanges()</c>/<c>SaveChangesAsync()</c> (directly or via tracked entities). Defaults
    /// to <c>false</c> so <c>run_query</c> stays read-only by default even under the Roslyn
    /// engine; even when <c>true</c>, the generated query context still refuses to save unless the
    /// active connection is also non-production <see cref="Connections.ConnectionAccessMode.ReadWrite"/>
    /// - mirrors the gating shape used for <c>EntityMutations:Enabled</c>. Has no effect under
    /// <see cref="QueryEngine.DynamicLinq"/>, which cannot express mutations at all.</summary>
    public bool AllowMutationsInRunQuery { get; init; }

    /// <summary>Absolute maximum number of rows any sequence query can return.</summary>
    public int MaxTake { get; init; } = 200;

    /// <summary>Row count used for sequence queries that do not specify <c>Take</c>.</summary>
    public int DefaultTake { get; init; } = 50;

    /// <summary>Extra wall-clock margin added on top of a connection's configured EF Core command
    /// timeout before the query is cancelled from the server side as a defense-in-depth measure
    /// (the primary enforcement mechanism is the provider's own command timeout).</summary>
    public TimeSpan CancellationMargin { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum accepted query-expression length.</summary>
    public int MaxQueryLength { get; init; } = 4_000;

    /// <summary>Maximum expression-tree nodes accepted from the query parser.</summary>
    public int MaxExpressionNodes { get; init; } = 300;

    /// <summary>Maximum expression-tree nesting depth accepted from the query parser.</summary>
    public int MaxExpressionDepth { get; init; } = 40;

    /// <summary>Maximum LINQ operator calls accepted in one query expression.</summary>
    public int MaxQueryOperators { get; init; } = 20;
}
