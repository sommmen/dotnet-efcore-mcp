namespace DotnetEfCoreMcp.Server.Connections;

/// <summary>A single server-side connection registry entry: a logical name -> provider +
/// connection string + policy mapping. Never constructed from data supplied by an MCP client -
/// only ever loaded from server-side configuration (user-secrets / environment variables).</summary>
public sealed class ConnectionRegistryEntry
{
    public required string Name { get; init; }

    /// <summary>Explicit provider override. When omitted, the provider is inferred from the loaded target assembly
    /// (or, for <see cref="ConnectionSource.ApplicationFactory"/> connections, validated against the provider the
    /// factory-built <see cref="Microsoft.EntityFrameworkCore.DbContext"/> reports).</summary>
    public DatabaseProvider? Provider { get; init; }

    /// <summary>How this connection's provider/connection string are obtained (see
    /// <see cref="ConnectionSource"/>). Defaults to <see cref="ConnectionSource.Explicit"/> for backwards
    /// compatibility with existing configurations.</summary>
    public ConnectionSource Source { get; init; } = ConnectionSource.Explicit;

    /// <summary>Required when <see cref="Source"/> is <see cref="ConnectionSource.Explicit"/>; must be
    /// absent when <see cref="Source"/> is <see cref="ConnectionSource.ApplicationFactory"/>, whose connection
    /// string is instead supplied by the target's own design-time factory at resolution time.</summary>
    public string? ConnectionString { get; init; }

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

    /// <summary>The required, server-side context/entity allowlist for this connection (see
    /// docs/development/connections.md, "P0 #9"). Never null - <see cref="ConnectionRegistry"/>
    /// rejects any connection missing this section rather than inferring a default policy.</summary>
    public required ConnectionAccessPolicy AccessPolicy { get; init; }

    /// <summary>Redacted representation safe to log or return to an MCP client - never includes
    /// <see cref="ConnectionString"/>.</summary>
    public override string ToString() =>
        $"ConnectionRegistryEntry {{ Name = {Name}, Source = {Source}, Provider = {Provider?.ToString() ?? "(inferred)"}, AccessMode = {AccessMode}, Environment = {Environment}, CommandTimeoutSeconds = {CommandTimeoutSeconds}, ConnectionString = {(Source == ConnectionSource.ApplicationFactory ? "(from application factory)" : "[REDACTED]")} }}";
}
