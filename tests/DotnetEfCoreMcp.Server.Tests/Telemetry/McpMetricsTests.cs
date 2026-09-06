using System.Diagnostics.Metrics;
using DotnetEfCoreMcp.Server.Telemetry;

namespace DotnetEfCoreMcp.Server.Tests.Telemetry;

/// <summary>Verifies <see cref="McpMetrics"/> instrument names/attributes using an in-memory
/// <see cref="MeterListener"/> - no OTLP exporter or network access is involved.</summary>
public sealed class McpMetricsTests
{
    private sealed record Measurement(string InstrumentName, object Value, IReadOnlyDictionary<string, object?> Tags);

    private static (McpMetrics Metrics, List<Measurement> Measurements, MeterListener Listener) CreateListenedMetrics(TelemetryOptions options)
    {
        var metrics = new McpMetrics(options);
        var measurements = new List<Measurement>();

        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == McpMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add(new Measurement(instrument.Name, value, ToDictionary(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add(new Measurement(instrument.Name, value, ToDictionary(tags))));
        listener.Start();

        return (metrics, measurements, listener);
    }

    private static IReadOnlyDictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var tag in tags)
        {
            dict[tag.Key] = tag.Value;
        }
        return dict;
    }

    [Fact]
    public void BeginToolInvocation_RecordsRequestAndActiveCounters()
    {
        var (metrics, measurements, listener) = CreateListenedMetrics(new TelemetryOptions());
        using (listener)
        {
            using var scope = metrics.BeginToolInvocation("run_query", requestId: "req-1");
            scope.Complete(succeeded: true, errorCategory: null);
        }

        Assert.Contains(measurements, m => m.InstrumentName == "mcp.tool.requests" && (long)m.Value == 1);
        Assert.Contains(measurements, m => m.InstrumentName == "mcp.tool.requests.active" && (long)m.Value == 1);
        Assert.Contains(measurements, m => m.InstrumentName == "mcp.tool.requests.active" && (long)m.Value == -1);
        Assert.Contains(measurements, m => m.InstrumentName == "mcp.tool.requests.success" && (long)m.Value == 1);
        Assert.Contains(measurements, m => m.InstrumentName == "mcp.tool.request.duration");
    }

    [Fact]
    public void Complete_WithFailure_RecordsErrorCountWithSafeCategoryOnly()
    {
        var (metrics, measurements, listener) = CreateListenedMetrics(new TelemetryOptions());
        using (listener)
        {
            using var scope = metrics.BeginToolInvocation("run_sql_query", requestId: "req-2");
            scope.Complete(succeeded: false, errorCategory: "McpException");
        }

        var errorMeasurement = Assert.Single(measurements, m => m.InstrumentName == "mcp.tool.requests.error");
        Assert.Equal("McpException", errorMeasurement.Tags["mcp.error.category"]);
        Assert.DoesNotContain(measurements, m => m.InstrumentName == "mcp.tool.requests.success");
    }

    [Fact]
    public void ToolInvocationScope_Complete_IsIdempotent()
    {
        var (metrics, measurements, listener) = CreateListenedMetrics(new TelemetryOptions());
        using (listener)
        {
            using var scope = metrics.BeginToolInvocation("run_query", requestId: "req-3");
            scope.Complete(succeeded: true, errorCategory: null);
            scope.Complete(succeeded: true, errorCategory: null);
            scope.Complete(succeeded: false, errorCategory: "ShouldBeIgnored");
        }

        Assert.Single(measurements, m => m.InstrumentName == "mcp.tool.requests.success");
        Assert.DoesNotContain(measurements, m => m.InstrumentName == "mcp.tool.requests.error");
    }

    [Fact]
    public void RecordQueryExecution_InProduction_OmitsModelNameForNamespacedType()
    {
        var (metrics, measurements, listener) = CreateListenedMetrics(new TelemetryOptions { EnableDevelopmentFullData = false });
        using (listener)
        {
            metrics.RecordQueryExecution("run_query", "Sqlite", "MyApp.Data.CustomerContext", rowCount: 5, succeeded: true);
        }

        var queryMeasurement = Assert.Single(measurements, m => m.InstrumentName == "mcp.query.executions");
        Assert.False(queryMeasurement.Tags.ContainsKey("db.model"));
        Assert.Equal("Sqlite", queryMeasurement.Tags["db.system"]);
        Assert.Equal("success", queryMeasurement.Tags["mcp.outcome"]);
    }

    [Fact]
    public void RecordQueryExecution_InProduction_IncludesModelNameForBoundedShortType()
    {
        var (metrics, measurements, listener) = CreateListenedMetrics(new TelemetryOptions { EnableDevelopmentFullData = false });
        using (listener)
        {
            metrics.RecordQueryExecution("run_query", "Sqlite", "CustomerContext", rowCount: 5, succeeded: true);
        }

        var queryMeasurement = Assert.Single(measurements, m => m.InstrumentName == "mcp.query.executions");
        Assert.Equal("CustomerContext", queryMeasurement.Tags["db.model"]);
    }

    [Fact]
    public void RecordQueryExecution_WithDevelopmentFullData_IncludesNamespacedModelName()
    {
        var (metrics, measurements, listener) = CreateListenedMetrics(new TelemetryOptions { EnableDevelopmentFullData = true });
        using (listener)
        {
            metrics.RecordQueryExecution("run_query", "Sqlite", "MyApp.Data.CustomerContext", rowCount: 5, succeeded: true);
        }

        var queryMeasurement = Assert.Single(measurements, m => m.InstrumentName == "mcp.query.executions");
        Assert.Equal("MyApp.Data.CustomerContext", queryMeasurement.Tags["db.model"]);
    }

    [Fact]
    public void RecordQueryExecution_RecordsRowCountHistogram()
    {
        var (metrics, measurements, listener) = CreateListenedMetrics(new TelemetryOptions());
        using (listener)
        {
            metrics.RecordQueryExecution("run_query", "Sqlite", "CustomerContext", rowCount: 42, succeeded: true);
        }

        Assert.Contains(measurements, m => m.InstrumentName == "mcp.query.row_count" && (long)m.Value == 42);
    }

    [Fact]
    public void RecordQueryExecution_WithNullRowCount_DoesNotRecordHistogram()
    {
        var (metrics, measurements, listener) = CreateListenedMetrics(new TelemetryOptions());
        using (listener)
        {
            metrics.RecordQueryExecution("run_query", "Sqlite", "CustomerContext", rowCount: null, succeeded: false);
        }

        Assert.DoesNotContain(measurements, m => m.InstrumentName == "mcp.query.row_count");
    }
}
