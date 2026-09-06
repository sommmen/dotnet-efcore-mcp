# Auditing & observability

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: logging is emitted from
[`Querying/QueryExecutor.cs`](../../src/DotnetEfCoreMcp.Server/Querying/QueryExecutor.cs) and
[`Tools/EfCoreMcpTools.cs`](../../src/DotnetEfCoreMcp.Server/Tools/EfCoreMcpTools.cs) via
`Microsoft.Extensions.Logging`.

- [x] Log every executed query (context, root set, query shape, row count, duration) without logging secrets
  - `QueryExecutor` logs (via injected `ILogger<QueryExecutor>`) the context type, root
    `DbSet` name, effective page size, result row count, and elapsed milliseconds for every
    `ExecuteAsync` call, plus a warning-level log on failure. It never logs the connection
    string, caller expression text, or full exception detail that could expose either.
    `EfCoreMcpTools` similarly logs `load_assembly`/`get_schema` invocations.
- [x] Add structured logging with configurable verbosity
  - Standard `Microsoft.Extensions.Logging` structured logging (named parameters, not
    string interpolation), verbosity configurable via the normal `Logging:LogLevel`
    configuration section in `appsettings.json`/environment variables.
- [x] Add basic metrics/telemetry hooks (optional, later-stage)
  - OpenTelemetry metrics and tracing instrument MCP tool requests and query execution.
    Export is disabled by default and uses OTLP/HTTP only when `Telemetry:Enabled` is set;
    all settings support the existing `DOTNETEFCOREMCP_` environment-variable overrides.
    Metrics cover request outcomes, latency, active requests, query outcomes, and row counts
    with bounded production-safe dimensions. Sampling is parent-based and configurable (5%
    by default). `Telemetry:EnableDevelopmentFullData` permits richer query/model data only
    when the server runs in Development. The exporter uses bounded batch processing so it
    never blocks or fails MCP requests; no HTTP metrics endpoint or metrics MCP tool is exposed.
