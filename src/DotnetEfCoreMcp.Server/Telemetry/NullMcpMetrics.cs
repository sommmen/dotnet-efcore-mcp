namespace DotnetEfCoreMcp.Server.Telemetry;

/// <summary>No-op <see cref="IMcpMetrics"/> used whenever telemetry is disabled (the default) and as
/// the synthesized default for callers that do not supply an explicit metrics implementation, such as
/// the test-only <see cref="Tools.EfCoreMcpTools"/> secondary constructor.</summary>
public sealed class NullMcpMetrics : IMcpMetrics
{
    public static readonly NullMcpMetrics Instance = new();

    private NullMcpMetrics()
    {
    }

    public IToolInvocationScope BeginToolInvocation(string operation, string requestId) => NullToolInvocationScope.Instance;

    public void RecordQueryExecution(string operation, string provider, string? modelName, long? rowCount, bool succeeded)
    {
        // Intentionally a no-op.
    }

    private sealed class NullToolInvocationScope : IToolInvocationScope
    {
        public static readonly NullToolInvocationScope Instance = new();

        private NullToolInvocationScope()
        {
        }

        public void Complete(bool succeeded, string? errorCategory)
        {
            // Intentionally a no-op.
        }

        public void Dispose()
        {
            // Intentionally a no-op.
        }
    }
}
