namespace DotnetEfCoreMcp.Server.Connections;

/// <summary>Thrown when the connection registry's configuration is malformed (missing required
/// fields, unknown provider name, etc.). Never includes the connection string value itself.</summary>
public sealed class ConnectionRegistryConfigurationException : Exception
{
    public ConnectionRegistryConfigurationException(string message)
        : base(message)
    {
    }
}

/// <summary>Thrown when an MCP client requests a connection name that has no matching entry in the
/// server-side registry. The server always fails closed - there is no default/fallback
/// connection.</summary>
public sealed class UnknownConnectionException : Exception
{
    public UnknownConnectionException(string requestedName, IReadOnlyCollection<string> knownNames)
        : base(BuildMessage(requestedName, knownNames))
    {
        RequestedName = requestedName;
        KnownNames = knownNames;
    }

    public string RequestedName { get; }

    public IReadOnlyCollection<string> KnownNames { get; }

    private static string BuildMessage(string requestedName, IReadOnlyCollection<string> knownNames)
    {
        var known = knownNames.Count > 0 ? string.Join(", ", knownNames) : "(none configured)";
        return $"No connection named '{requestedName}' is configured on the server. Known connections: {known}.";
    }
}

/// <summary>Thrown when a request is rejected by a connection's <see cref="ConnectionAccessPolicy"/>
/// (see docs/development/connections.md, "P0 #9"): the requested context or entity did not match an
/// allow selector, either because it matched a deny selector or because it matched neither list
/// (fail-closed default denial). The message intentionally identifies only the requested selector
/// and connection - it never enumerates permitted or prohibited alternatives, so a denial cannot be
/// used to discover whether other names exist in the model.</summary>
public sealed class AccessPolicyDeniedException : Exception
{
    public AccessPolicyDeniedException(string connectionName, string message)
        : base(message)
    {
        ConnectionName = connectionName;
    }

    public string ConnectionName { get; }

    /// <summary>The single, unified denial for an unreachable/unlisted <c>DbContext</c>. Deliberately
    /// worded so it does not confirm or deny whether <paramref name="requestedContextName"/> exists
    /// in the loaded model at all.</summary>
    public static AccessPolicyDeniedException ForContext(string connectionName, string requestedContextName) =>
        new(
            connectionName,
            $"Access to DbContext '{requestedContextName}' is not permitted for connection '{connectionName}'.");

    /// <summary>The single, unified denial for an unreachable/unlisted entity within an otherwise
    /// reachable context. Deliberately worded so it does not confirm or deny whether
    /// <paramref name="requestedEntityName"/> exists in the loaded model at all.</summary>
    public static AccessPolicyDeniedException ForEntity(string connectionName, string requestedContextName, string requestedEntityName) =>
        new(
            connectionName,
            $"Access to entity '{requestedEntityName}' on DbContext '{requestedContextName}' is not permitted for connection '{connectionName}'.");
}

/// <summary>Thrown when an operation attempts to mutate or make active a connection that is
/// protected because it is designated as production (see <see cref="ConnectionRegistryEntry.IsProduction"/>).
/// Production connections are update-forbidden and cannot be set as the active connection without
/// the caller explicitly acknowledging the production target.</summary>
public sealed class ProductionProtectedException : Exception
{
    public ProductionProtectedException(string connectionName)
        : base(
            $"Connection '{connectionName}' is designated as production and is protected. " +
            "Production connections cannot be made the active connection without explicitly " +
            "confirming the production target (allowProduction: true).")
    {
        ConnectionName = connectionName;
    }

    public string ConnectionName { get; }
}
