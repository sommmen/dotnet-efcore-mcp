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

    /// <summary>Redacted representation safe to log or return to an MCP client - never includes
    /// <see cref="ConnectionString"/>.</summary>
    public override string ToString() =>
        $"ConnectionRegistryEntry {{ Name = {Name}, Provider = {Provider}, AccessMode = {AccessMode}, CommandTimeoutSeconds = {CommandTimeoutSeconds}, ConnectionString = [REDACTED] }}";
}
