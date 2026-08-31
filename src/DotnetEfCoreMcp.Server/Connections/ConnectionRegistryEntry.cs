namespace DotnetEfCoreMcp.Server.Connections;

/// <summary>A single server-side connection registry entry: a logical name -> provider +
/// connection string + policy mapping. Never constructed from data supplied by an MCP client -
/// only ever loaded from server-side configuration (user-secrets / environment variables).</summary>
public sealed class ConnectionRegistryEntry
{
    public required string Name { get; init; }

    public required DatabaseProvider Provider { get; init; }

    public required string ConnectionString { get; init; }

    public ConnectionAccessMode AccessMode { get; init; } = ConnectionAccessMode.ReadOnly;

    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>The runtime environment this connection points at (see <see cref="EnvironmentType"/>).
    /// Drives <see cref="IsProduction"/> so an operator can mark a connection as the production
    /// database, which then gets read-only + protection semantics.</summary>
    public EnvironmentType Environment { get; init; } = EnvironmentType.Unspecified;

    /// <summary>True when this connection is designated as production via
    /// <see cref="Environment"/> == <see cref="EnvironmentType.Production"/>. Production connections
    /// are update-forbidden and protected from being made the active connection without explicit
    /// acknowledgment (see <c>ConnectionRegistry.SetActive</c>).</summary>
    public bool IsProduction => Environment == EnvironmentType.Production;

    /// <summary>Redacted representation safe to log or return to an MCP client - never includes
    /// <see cref="ConnectionString"/>.</summary>
    public override string ToString() =>
        $"ConnectionRegistryEntry {{ Name = {Name}, Provider = {Provider}, AccessMode = {AccessMode}, Environment = {Environment}, CommandTimeoutSeconds = {CommandTimeoutSeconds}, ConnectionString = [REDACTED] }}";
}
