using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DotnetEfCoreMcp.Server.Telemetry;

/// <summary>Meter-backed <see cref="IMcpMetrics"/> implementation, active only when
/// <see cref="TelemetryOptions.Enabled"/> is <see langword="true"/>. All recording methods swallow
/// any exception - telemetry must never fail or block an MCP tool request.</summary>
public sealed class McpMetrics : IMcpMetrics, IDisposable
{
    /// <summary>Name used both for the <see cref="Meter"/> and as the OpenTelemetry meter name
    /// registered via <c>AddMeter</c> in <c>Program.cs</c>.</summary>
    public const string MeterName = "DotnetEfCoreMcp.Server";

    private readonly Meter _meter;
    private readonly Counter<long> _requestCount;
    private readonly Counter<long> _successCount;
    private readonly Counter<long> _errorCount;
    private readonly Histogram<double> _requestLatencyMs;
    private readonly UpDownCounter<long> _activeRequests;
    private readonly Counter<long> _queryCount;
    private readonly Histogram<long> _queryRowCount;
    private readonly TelemetryOptions _options;

    public McpMetrics(TelemetryOptions options)
    {
        _options = options;
        _meter = new Meter(MeterName);

        _requestCount = _meter.CreateCounter<long>(
            "mcp.tool.requests",
            unit: "{request}",
            description: "Number of MCP tool invocations started.");
        _successCount = _meter.CreateCounter<long>(
            "mcp.tool.requests.success",
            unit: "{request}",
            description: "Number of MCP tool invocations that completed successfully.");
        _errorCount = _meter.CreateCounter<long>(
            "mcp.tool.requests.error",
            unit: "{request}",
            description: "Number of MCP tool invocations that completed with an error.");
        _requestLatencyMs = _meter.CreateHistogram<double>(
            "mcp.tool.request.duration",
            unit: "ms",
            description: "Latency of MCP tool invocations, in milliseconds.");
        _activeRequests = _meter.CreateUpDownCounter<long>(
            "mcp.tool.requests.active",
            unit: "{request}",
            description: "Number of MCP tool invocations currently in flight.");
        _queryCount = _meter.CreateCounter<long>(
            "mcp.query.executions",
            unit: "{query}",
            description: "Number of query executions (run_query/run_sql_query), tagged by provider/model/outcome.");
        _queryRowCount = _meter.CreateHistogram<long>(
            "mcp.query.row_count",
            unit: "{row}",
            description: "Rows returned or affected by a query execution.");
    }

    public IToolInvocationScope BeginToolInvocation(string operation, string requestId)
    {
        try
        {
            _requestCount.Add(1, new KeyValuePair<string, object?>("mcp.tool.name", operation));
            _activeRequests.Add(1, new KeyValuePair<string, object?>("mcp.tool.name", operation));
        }
        catch
        {
            // Recording must never fail or block the request.
        }

        return new ToolInvocationScope(this, operation, requestId);
    }

    public void RecordQueryExecution(string operation, string provider, string? modelName, long? rowCount, bool succeeded)
    {
        try
        {
            var tags = new TagList
            {
                { "mcp.tool.name", operation },
                { "db.system", provider },
                { "mcp.outcome", succeeded ? "success" : "error" },
            };

            if (modelName is not null && (_options.EnableDevelopmentFullData || IsBoundedModelName(modelName)))
            {
                tags.Add("db.model", modelName);
            }

            _queryCount.Add(1, tags);

            if (rowCount is { } rows)
            {
                _queryRowCount.Record(rows, new TagList
                {
                    { "mcp.tool.name", operation },
                    { "db.system", provider },
                });
            }
        }
        catch
        {
            // Recording must never fail or block the request.
        }
    }

    /// <summary>Production-safe model names are short CLR type names (no dots/generics), which keeps
    /// cardinality bounded to the set of DbContext types the loaded assembly defines. Anything richer
    /// (namespaces, assembly-qualified names) requires <see cref="TelemetryOptions.EnableDevelopmentFullData"/>.</summary>
    private static bool IsBoundedModelName(string modelName) => modelName.Length <= 128 && !modelName.Contains('.');

    public void Dispose() => _meter.Dispose();

    private sealed class ToolInvocationScope : IToolInvocationScope
    {
        private readonly McpMetrics _owner;
        private readonly string _operation;
        private readonly long _startTimestamp;
        private int _completed;

        public ToolInvocationScope(McpMetrics owner, string operation, string requestId)
        {
            _owner = owner;
            _operation = operation;
            _startTimestamp = Stopwatch.GetTimestamp();
            _ = requestId; // Correlation is carried via logging; metrics dimensions stay low-cardinality.
        }

        public void Complete(bool succeeded, string? errorCategory)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            try
            {
                var elapsedMs = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
                var tags = new TagList { { "mcp.tool.name", _operation } };
                _owner._requestLatencyMs.Record(elapsedMs, tags);

                if (succeeded)
                {
                    _owner._successCount.Add(1, tags);
                }
                else
                {
                    var errorTags = new TagList
                    {
                        { "mcp.tool.name", _operation },
                        { "mcp.error.category", errorCategory ?? "unknown" },
                    };
                    _owner._errorCount.Add(1, errorTags);
                }
            }
            catch
            {
                // Recording must never fail or block the request.
            }
        }

        public void Dispose()
        {
            try
            {
                _owner._activeRequests.Add(-1, new KeyValuePair<string, object?>("mcp.tool.name", _operation));
            }
            catch
            {
                // Recording must never fail or block the request.
            }
        }
    }
}
