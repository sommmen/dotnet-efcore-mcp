using Microsoft.Extensions.Configuration;

namespace DotnetEfCoreMcp.Server.Connections;

/// <summary>Server-side registry mapping a logical connection name to a provider + connection
/// string + access policy. Populated exclusively from server-side configuration (.NET
/// user-secrets for local/dev, overridable via environment variables) under the "Connections"
/// section - never from anything supplied by an MCP client, and never from the target project's
/// own configuration files. Production deployments should replace the user-secrets source with a
/// mounted secrets file or a real secret store (e.g. Azure Key Vault, AWS Secrets Manager); that
/// integration is out of scope for this MVP.</summary>
public sealed class ConnectionRegistry
{
    private readonly Dictionary<string, ConnectionRegistryEntry> _entries;

    public ConnectionRegistry(IConfiguration configuration)
    {
        _entries = Load(configuration);
    }

    public IReadOnlyCollection<string> ConnectionNames => _entries.Keys;

    public bool TryGet(string name, out ConnectionRegistryEntry entry)
    {
        return _entries.TryGetValue(name, out entry!);
    }

    /// <summary>Fails closed: throws <see cref="UnknownConnectionException"/> rather than falling
    /// back to any default connection when <paramref name="name"/> isn't registered.</summary>
    public ConnectionRegistryEntry Get(string name)
    {
        if (!TryGet(name, out var entry))
        {
            throw new UnknownConnectionException(name, ConnectionNames);
        }

        return entry;
    }

    private static Dictionary<string, ConnectionRegistryEntry> Load(IConfiguration configuration)
    {
        var section = configuration.GetSection("Connections");
        var result = new Dictionary<string, ConnectionRegistryEntry>(StringComparer.Ordinal);

        foreach (var child in section.GetChildren())
        {
            var name = child.Key;
            var providerRaw = child["Provider"];
            var connectionString = child["ConnectionString"];
            var accessModeRaw = child["AccessMode"];
            var commandTimeoutRaw = child["CommandTimeoutSeconds"];

            if (string.IsNullOrWhiteSpace(providerRaw))
            {
                throw new ConnectionRegistryConfigurationException(
                    $"Connection '{name}' is missing a required 'Provider' value.");
            }

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ConnectionRegistryConfigurationException(
                    $"Connection '{name}' is missing a required 'ConnectionString' value.");
            }

            if (!Enum.TryParse<DatabaseProvider>(providerRaw, ignoreCase: true, out var provider))
            {
                var allowed = string.Join(", ", Enum.GetNames<DatabaseProvider>());
                throw new ConnectionRegistryConfigurationException(
                    $"Connection '{name}' has unsupported provider '{providerRaw}'. Allowed providers: {allowed}.");
            }

            var accessMode = ConnectionAccessMode.ReadOnly;
            if (!string.IsNullOrWhiteSpace(accessModeRaw) && !Enum.TryParse(accessModeRaw, ignoreCase: true, out accessMode))
            {
                throw new ConnectionRegistryConfigurationException(
                    $"Connection '{name}' has invalid AccessMode '{accessModeRaw}'. Expected 'ReadOnly' or 'ReadWrite'.");
            }

            var commandTimeoutSeconds = 30;
            if (!string.IsNullOrWhiteSpace(commandTimeoutRaw))
            {
                if (!int.TryParse(commandTimeoutRaw, out commandTimeoutSeconds) || commandTimeoutSeconds <= 0)
                {
                    throw new ConnectionRegistryConfigurationException(
                        $"Connection '{name}' has invalid CommandTimeoutSeconds '{commandTimeoutRaw}'. Expected a positive integer.");
                }
            }

            result[name] = new ConnectionRegistryEntry
            {
                Name = name,
                Provider = provider,
                ConnectionString = connectionString,
                AccessMode = accessMode,
                CommandTimeoutSeconds = commandTimeoutSeconds,
            };
        }

        return result;
    }
}
