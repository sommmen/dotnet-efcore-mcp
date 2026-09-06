namespace DotnetEfCoreMcp.Server.Telemetry;

/// <summary>Records bounded, low-cardinality metrics for MCP tool requests and query execution.
/// Every implementation must never throw and must never block the calling request - metric
/// recording is a best-effort side channel, not part of request correctness.</summary>
public interface IMcpMetrics
{
    /// <summary>Records the start of a tool invocation, returning an opaque handle used to record
    /// its completion. Increments the in-flight/active-request gauge for the lifetime of the
    /// returned handle.</summary>
    /// <param name="operation">Stable tool name, e.g. <c>"run_query"</c>.</param>
    /// <param name="requestId">Per-invocation correlation identifier (opaque, not derived from
    /// caller input) used to correlate this request's metrics/log entries.</param>
    IToolInvocationScope BeginToolInvocation(string operation, string requestId);

    /// <summary>Records a single query execution outcome (row count, provider, and optionally a
    /// bounded "model" attribute) after a <c>run_query</c>/<c>run_sql_query</c> call completes.</summary>
    /// <param name="operation">Stable tool name that triggered the query, e.g. <c>"run_query"</c>.</param>
    /// <param name="provider">Effective database provider.</param>
    /// <param name="modelName">Bounded, short (non-namespaced) DbContext type name, or <see langword="null"/>
    /// when unavailable. Never a fully-qualified/assembly-qualified name in production.</param>
    /// <param name="rowCount">Rows returned/affected, or <see langword="null"/> when not applicable.</param>
    /// <param name="succeeded">Whether the query completed without error.</param>
    void RecordQueryExecution(string operation, string provider, string? modelName, long? rowCount, bool succeeded);
}

/// <summary>Opaque handle for an in-flight tool invocation, returned by
/// <see cref="IMcpMetrics.BeginToolInvocation"/>. Disposing (or calling <see cref="Complete"/>)
/// records the outcome/latency and decrements the in-flight gauge exactly once.</summary>
public interface IToolInvocationScope : IDisposable
{
    /// <summary>Records the terminal outcome of the invocation. Safe to call at most once; a
    /// missing call (e.g. an unexpected process exit) simply means no completion metric is
    /// recorded, but the in-flight gauge is still corrected on <see cref="IDisposable.Dispose"/>.</summary>
    /// <param name="succeeded">Whether the tool call completed without error.</param>
    /// <param name="errorCategory">A bounded, safe error category (never exception text/message),
    /// or <see langword="null"/> when <paramref name="succeeded"/> is <see langword="true"/>.</param>
    void Complete(bool succeeded, string? errorCategory);
}
