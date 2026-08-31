namespace DotnetEfCoreMcp.Server.Connections;

/// <summary>Database providers supported by the connection registry allowlist. Any provider name
/// found in configuration that doesn't map to one of these is rejected explicitly rather than
/// silently ignored.</summary>
public enum DatabaseProvider
{
    Sqlite,
    SqlServer,
    PostgreSql,
}

/// <summary>Whether a registered connection may be used only for reads, or also for writes. This
/// is a registry-level policy setting independent of the underlying database user's own grants -
/// it lets an operator mark a connection read-only even if the database credentials themselves
/// happen to allow writes.</summary>
public enum ConnectionAccessMode
{
    ReadOnly,
    ReadWrite,
}

/// <summary>Which runtime environment a registered connection points at. It is a registry-level
/// designation used to drive safety policy (see <c>IsProduction</c>): a connection marked
/// <see cref="Production"/> is treated as update-forbidden (read-only) and is protected from being
/// made the server's active connection without explicit acknowledgment. Connections without an
/// explicit environment default to <see cref="Unspecified"/> and get no special production
/// safeguards (backwards compatible with existing configurations).</summary>
public enum EnvironmentType
{
    Unspecified,
    Development,
    Staging,
    Production,
}
