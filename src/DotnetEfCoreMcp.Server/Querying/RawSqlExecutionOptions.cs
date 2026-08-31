namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Controls raw SQL execution. It is disabled by default and must be enabled explicitly
/// in server-side configuration; it can never be used with a production connection.</summary>
public sealed class RawSqlExecutionOptions
{
    /// <summary>Whether the <c>run_sql_query</c> MCP tool is available. Defaults to false.</summary>
    public bool Enabled { get; init; }

    /// <summary>Maximum number of rows returned by a raw SQL result set.</summary>
    public int MaxRows { get; init; } = 200;

    /// <summary>Extra wall-clock margin added to the connection command timeout before cancellation.</summary>
    public TimeSpan CancellationMargin { get; init; } = TimeSpan.FromSeconds(5);
}
