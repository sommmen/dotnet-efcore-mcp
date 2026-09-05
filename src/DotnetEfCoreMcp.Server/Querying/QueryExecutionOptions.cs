namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Server-wide limits enforced on every query, regardless of what the caller requests.</summary>
public sealed class QueryExecutionOptions
{
    /// <summary>Which engine <c>run_query</c> uses to execute the caller's query. See
    /// <c>docs/development/roslyn-user-query.md</c>; defaults to <see cref="QueryEngine.Roslyn"/>
    /// while <see cref="QueryEngine.DynamicLinq"/> remains an explicit, temporary compatibility
    /// escape hatch.</summary>
    public QueryEngine Engine { get; init; } = QueryEngine.Roslyn;

    /// <summary>Where Roslyn queries execute. Auto safely selects an isolated process.</summary>
    public QueryExecutionMode Mode { get; init; } = QueryExecutionMode.Auto;

    /// <summary>Path to the query-host DLL. Required for out-of-process Roslyn execution.</summary>
    public string? OutOfProcessHostPath { get; init; }

    /// <summary>Maximum number of pooled persistent query-host workers kept warm for one target assembly build.</summary>
    public int PoolMaxWorkersPerTarget { get; init; } = 2;

    /// <summary>Maximum total number of pooled persistent query-host workers across all target assembly builds in one MCP server instance.</summary>
    public int PoolMaxTotalWorkers { get; init; } = 8;

    /// <summary>Maximum number of successful queries one pooled worker serves before it is retired and replaced on demand.</summary>
    public int PoolMaxQueriesPerWorker { get; init; } = 50;

    /// <summary>Maximum time a pooled worker may remain idle in the server-side pool before it is retired.</summary>
    public int PoolIdleTimeoutSeconds { get; init; } = 300;

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
