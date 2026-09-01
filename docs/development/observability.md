# Auditing & observability

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: logging is emitted from
[`Querying/QueryExecutor.cs`](../../src/DotnetEfCoreMcp.Server/Querying/QueryExecutor.cs) and
[`Tools/EfCoreMcpTools.cs`](../../src/DotnetEfCoreMcp.Server/Tools/EfCoreMcpTools.cs) via
`Microsoft.Extensions.Logging`.

- [x] Log every executed query (context, entity, query shape, row count, duration) without logging secrets
  - `QueryExecutor` logs (via injected `ILogger<QueryExecutor>`) the context type, entity
    name, effective skip/take, included navigation names, row count, and elapsed
    milliseconds for every `ExecuteAsync` call, plus a warning-level log on failure (entity/
    context only, never the connection string or full exception detail that could include
    it). `EfCoreMcpTools` similarly logs `load_assembly`/`get_schema` invocations.
- [x] Add structured logging with configurable verbosity
  - Standard `Microsoft.Extensions.Logging` structured logging (named parameters, not
    string interpolation), verbosity configurable via the normal `Logging:LogLevel`
    configuration section in `appsettings.json`/environment variables.
- [ ] Add basic metrics/telemetry hooks (optional, later-stage)
  - Open item — see [`WORK-TRACKER.md`](./WORK-TRACKER.md). No metrics pipeline exists in
    this repo yet to hook into (would need an OpenTelemetry or similar dependency decision).
