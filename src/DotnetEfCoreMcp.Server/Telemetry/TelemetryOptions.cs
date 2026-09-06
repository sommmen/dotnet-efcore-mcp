using Microsoft.Extensions.Configuration;

namespace DotnetEfCoreMcp.Server.Telemetry;

/// <summary>Controls OpenTelemetry metrics export for MCP tool requests and query execution.
/// Disabled by default; every field can be overridden via the standard configuration layering
/// (<c>appsettings.json</c>, user secrets, <c>DOTNETEFCOREMCP_Telemetry__*</c> environment
/// variables). Metrics are exported over OTLP/HTTP only.</summary>
public sealed class TelemetryOptions
{
    /// <summary>Master switch. When <see langword="false"/> (the default) no OpenTelemetry SDK
    /// components are initialized at all - not just "exporting nothing" - so there is zero startup
    /// or runtime overhead unless a deployment explicitly opts in.</summary>
    public bool Enabled { get; init; }

    /// <summary>OTLP/HTTP endpoint the metrics exporter sends to, e.g.
    /// <c>http://localhost:4318/v1/metrics</c>. Required when <see cref="Enabled"/> is
    /// <see langword="true"/>; otherwise unused.</summary>
    public string? OtlpEndpoint { get; init; }

    /// <summary>Parent-based sampling ratio in the inclusive range [0, 1], applied to the root
    /// tracing/telemetry decision that gates whether a given request's activity is sampled. Defaults
    /// to a low, production-safe value so enabling telemetry does not itself become a
    /// performance/volume concern.</summary>
    public double SamplingRatio { get; init; } = 0.05;

    /// <summary>Explicit, Development-only opt-in that allows richer, higher-cardinality attributes
    /// (e.g. raw query/SQL text, full context type names) to be attached to metrics. Even when
    /// configured <see langword="true"/>, <see cref="CreateEffective"/> refuses to honor it outside
    /// the Development host environment - mirroring <see cref="Tools.ToolDiagnosticsOptions"/>.</summary>
    public bool EnableDevelopmentFullData { get; init; }

    /// <summary>Interval, in milliseconds, between periodic asynchronous metric export cycles. Metric
    /// instruments aggregate in-memory between cycles - recording a measurement never waits on
    /// export, so this only controls export cadence.</summary>
    public int ExportIntervalMilliseconds { get; init; } = 60_000;

    /// <summary>Per-cycle export timeout, in milliseconds. If the OTLP endpoint is slow or
    /// unreachable, an export attempt is abandoned after this timeout rather than accumulating
    /// unbounded in-flight exports; the next cycle's aggregated data supersedes it (graceful drop,
    /// never a blocked or failed MCP request).</summary>
    public int ExportTimeoutMilliseconds { get; init; } = 10_000;

    /// <summary>Creates the effective options, ensuring a configuration value cannot enable
    /// Development-only full-data attributes outside the Development host environment.</summary>
    public static TelemetryOptions CreateEffective(IConfiguration configuration, bool isDevelopment)
    {
        var configured = configuration.GetSection("Telemetry").Get<TelemetryOptions>() ?? new TelemetryOptions();
        return new TelemetryOptions
        {
            Enabled = configured.Enabled,
            OtlpEndpoint = configured.OtlpEndpoint,
            SamplingRatio = configured.SamplingRatio,
            EnableDevelopmentFullData = isDevelopment && configured.EnableDevelopmentFullData,
            ExportIntervalMilliseconds = configured.ExportIntervalMilliseconds,
            ExportTimeoutMilliseconds = configured.ExportTimeoutMilliseconds,
        };
    }
}
