using DotnetEfCoreMcp.Server.Telemetry;

namespace DotnetEfCoreMcp.Server.Tests.Telemetry;

/// <summary>Smoke-tests the disabled-by-default path: when telemetry is disabled,
/// <see cref="NullMcpMetrics"/> is used and every call is a safe, side-effect-free no-op.</summary>
public sealed class NullMcpMetricsTests
{
    [Fact]
    public void BeginToolInvocation_ReturnsUsableNoOpScope()
    {
        var scope = NullMcpMetrics.Instance.BeginToolInvocation("run_query", requestId: "req-1");

        // Must not throw regardless of call order/repetition - the no-op scope has no state to corrupt.
        scope.Complete(succeeded: true, errorCategory: null);
        scope.Complete(succeeded: false, errorCategory: "SomeCategory");
        scope.Dispose();
        scope.Dispose();
    }

    [Fact]
    public void RecordQueryExecution_DoesNotThrow()
    {
        NullMcpMetrics.Instance.RecordQueryExecution("run_query", "Sqlite", "CustomerContext", rowCount: 10, succeeded: true);
        NullMcpMetrics.Instance.RecordQueryExecution("run_sql_query", "Sqlite", modelName: null, rowCount: null, succeeded: false);
    }

    [Fact]
    public void Instance_IsSingleton() => Assert.Same(NullMcpMetrics.Instance, NullMcpMetrics.Instance);
}
