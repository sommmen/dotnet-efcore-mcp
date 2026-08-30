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
