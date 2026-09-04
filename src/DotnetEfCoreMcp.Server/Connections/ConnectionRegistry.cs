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
    private readonly object _gate = new();
    private string? _activeName;

    public ConnectionRegistry(IConfiguration configuration)
    {
        _entries = Load(configuration);
        _activeName = _entries.Values.FirstOrDefault(entry => !entry.IsProduction)?.Name;
    }

    public IReadOnlyCollection<string> ConnectionNames => _entries.Keys;

    /// <summary>Name of the currently active connection, i.e. the one used as the default when an
    /// MCP tool call does not specify an explicit connection name. Null when no connection is
    /// configured.</summary>
    public string? ActiveConnectionName => _activeName;

    /// <summary>The currently active <see cref="ConnectionRegistryEntry"/>, or null when no
    /// connection is configured. Never a production connection unless one was explicitly activated
    /// with acknowledgment.</summary>
    public ConnectionRegistryEntry? ActiveConnection =>
        _activeName is not null && _entries.TryGetValue(_activeName, out var entry) ? entry : null;

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

    /// <summary>Makes <paramref name="name"/> the active connection used as the default by MCP tools
    /// that don't specify an explicit connection. Refuses (throws <see cref="ProductionProtectedException"/>)
    /// when the target is designated as production and <paramref name="allowProduction"/> is false,
    /// so a production database can't become the default unless the caller explicitly opts in.</summary>
    public void SetActive(string name, bool allowProduction = false)
    {
        var entry = Get(name);

        if (entry.IsProduction && !allowProduction)
        {
            throw new ProductionProtectedException(name);
        }

        lock (_gate)
        {
            _activeName = name;
        }
    }

    /// <summary>Returns a redacted, environment-aware view of every registered connection - safe to
    /// return to an MCP client (never includes any connection string). The active connection is
    /// flagged.</summary>
    public IReadOnlyList<ConnectionInfo> ListConnections()
    {
        lock (_gate)
        {
            return _entries.Values
                .OrderBy(e => e.IsProduction)
                .ThenBy(e => e.Name, StringComparer.Ordinal)
                .Select(e => new ConnectionInfo(
                    e.Name,
                    e.Provider,
                    e.AccessMode,
                    e.Environment,
                    e.IsProduction,
                    IsActive: string.Equals(e.Name, _activeName, StringComparison.Ordinal)))
                .ToList();
        }
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
            var environmentRaw = child["Environment"];

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ConnectionRegistryConfigurationException(
                    $"Connection '{name}' is missing a required 'ConnectionString' value.");
            }

            DatabaseProvider? provider = null;
            if (!string.IsNullOrWhiteSpace(providerRaw))
            {
                if (!Enum.TryParse<DatabaseProvider>(providerRaw, ignoreCase: true, out var parsedProvider))
                {
                    var allowed = string.Join(", ", Enum.GetNames<DatabaseProvider>());
                    throw new ConnectionRegistryConfigurationException(
                        $"Connection '{name}' has unsupported provider '{providerRaw}'. Allowed providers: {allowed}.");
                }

                provider = parsedProvider;
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

            var environment = EnvironmentType.Unspecified;
            if (!string.IsNullOrWhiteSpace(environmentRaw))
            {
                if (!Enum.TryParse(environmentRaw, ignoreCase: true, out environment))
                {
                    var allowedEnvironments = string.Join(", ", Enum.GetNames<EnvironmentType>());
                    throw new ConnectionRegistryConfigurationException(
                        $"Connection '{name}' has invalid Environment '{environmentRaw}'. Allowed environments: {allowedEnvironments}.");
                }
            }

            // Fail-safe RSFU protection: a connection designated as production is update-forbidden
            // regardless of what AccessMode was configured. Writes to production can then never be
            // authorized by a misconfiguration.
            if (environment == EnvironmentType.Production)
            {
                accessMode = ConnectionAccessMode.ReadOnly;
            }

            var accessPolicy = LoadAccessPolicy(name, child.GetSection("AccessPolicy"));

            result[name] = new ConnectionRegistryEntry
            {
                Name = name,
                Provider = provider,
                ConnectionString = connectionString,
                AccessMode = accessMode,
                CommandTimeoutSeconds = commandTimeoutSeconds,
                Environment = environment,
                AccessPolicy = accessPolicy,
            };
        }

        return result;
    }

    private static readonly HashSet<string> KnownAccessPolicyMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "AllowContexts", "DenyContexts", "AllowEntities", "DenyEntities",
    };

    /// <summary>Parses and shape-validates the required <c>AccessPolicy</c> section for a single
    /// connection: presence, no unknown members, no malformed selectors, no duplicate selectors
    /// within any one list. Deliberately does not check whether any selector resolves against a
    /// loaded model - no target assembly is loaded yet when the registry is constructed, so that
    /// check is deferred to <see cref="ConnectionAccessPolicy.EnsureResolvable"/>, invoked the first
    /// time a connection is used against a loaded assembly.</summary>
    private static ConnectionAccessPolicy LoadAccessPolicy(string name, IConfigurationSection section)
    {
        if (!section.Exists())
        {
            throw new ConnectionRegistryConfigurationException(
                $"Connection '{name}' is missing a required 'AccessPolicy' section. Every connection must " +
                "declare an explicit AccessPolicy (AllowContexts, DenyContexts, AllowEntities, DenyEntities); " +
                "there is no default policy.");
        }

        var unknownMembers = section.GetChildren()
            .Select(c => c.Key)
            .Where(key => !KnownAccessPolicyMembers.Contains(key))
            .ToArray();
        if (unknownMembers.Length > 0)
        {
            throw new ConnectionRegistryConfigurationException(
                $"Connection '{name}' has an AccessPolicy with unknown member(s): {string.Join(", ", unknownMembers)}. " +
                $"Allowed members: {string.Join(", ", KnownAccessPolicyMembers)}.");
        }

        var allowContexts = LoadContextSelectorList(name, "AllowContexts", section);
        var denyContexts = LoadContextSelectorList(name, "DenyContexts", section);
        var allowEntities = LoadEntitySelectorList(name, "AllowEntities", section);
        var denyEntities = LoadEntitySelectorList(name, "DenyEntities", section);

        return new ConnectionAccessPolicy
        {
            AllowContexts = allowContexts,
            DenyContexts = denyContexts,
            AllowEntities = allowEntities,
            DenyEntities = denyEntities,
        };
    }

    private static IReadOnlyList<string> LoadContextSelectorList(string name, string member, IConfigurationSection policySection)
    {
        var values = policySection.GetSection(member).GetChildren()
            .Select(c => c.Value)
            .ToArray();

        var result = new List<string>(values.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in values)
        {
            if (string.IsNullOrEmpty(raw))
            {
                throw new ConnectionRegistryConfigurationException(
                    $"Connection '{name}' has a malformed AccessPolicy.{member} entry: selectors must be non-empty " +
                    "CLR DbContext full names.");
            }

            if (!seen.Add(raw))
            {
                throw new ConnectionRegistryConfigurationException(
                    $"Connection '{name}' has a duplicate AccessPolicy.{member} selector '{raw}'.");
            }

            result.Add(raw);
        }

        return result;
    }

    private static IReadOnlyList<EntitySelector> LoadEntitySelectorList(string name, string member, IConfigurationSection policySection)
    {
        var values = policySection.GetSection(member).GetChildren()
            .Select(c => c.Value)
            .ToArray();

        var result = new List<EntitySelector>(values.Length);
        var seen = new HashSet<EntitySelector>();
        foreach (var raw in values)
        {
            if (!EntitySelector.TryParse(raw, out var selector))
            {
                throw new ConnectionRegistryConfigurationException(
                    $"Connection '{name}' has a malformed AccessPolicy.{member} entry '{raw}'. Expected the exact " +
                    "form '<context full name>:<entity name>'.");
            }

            if (!seen.Add(selector))
            {
                throw new ConnectionRegistryConfigurationException(
                    $"Connection '{name}' has a duplicate AccessPolicy.{member} selector '{selector}'.");
            }

            result.Add(selector);
        }

        return result;
    }
}

/// <summary>Redacted view of a registered connection intended for return to an MCP client - never
/// contains the connection string.</summary>
public sealed record ConnectionInfo(
    string Name,
    DatabaseProvider? Provider,
    ConnectionAccessMode AccessMode,
    EnvironmentType Environment,
    bool IsProduction,
    bool IsActive);
