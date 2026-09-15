using System.Diagnostics;

namespace DotnetEfCoreMcp.Server.Telemetry;

/// <summary>Central <see cref="ActivitySource"/> used to correlate MCP tool requests for tracing and
/// metric exemplars. Starting an activity is always cheap: with telemetry disabled (the default) no
/// listener is registered, so <c>ActivitySource.StartActivity(string)</c> short-circuits to
/// <see langword="null"/> without allocating.</summary>
public static class McpActivitySource
{
    public const string Name = "DotnetEfCoreMcp.Server";

    public static readonly ActivitySource Instance = new(Name);
}
