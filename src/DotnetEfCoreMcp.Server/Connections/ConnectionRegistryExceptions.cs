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
