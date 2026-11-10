using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace DotnetEfCoreMcp.Server.Connections;

/// <summary>Server-side registry mapping a logical connection name to a provider + connection
/// string + access policy. Populated exclusively from server-side configuration (.NET
/// user-secrets for local/dev, overridable via environment variables) under the "Connections"
/// section - never from anything supplied by an MCP client, and never from the target project's
/// own configuration files. Production deployments should replace the user-secrets source with a
/// mounted secrets file or a real secret store (e.g. Azure Key Vault, AWS Secrets Manager); that
/// integration is out of scope for this MVP.
///
/// The parsed registry is a cache over that configuration, not a one-shot startup snapshot: every
/// accessor first checks whether any file-backed configuration source changed on disk (and whether
/// a provider raised its own reload token) and re-reads before answering, so a connection added to
/// the secrets store while the server is running becomes visible without restarting the MCP client
/// (issue #121). <see cref="Reload"/> forces the same refresh unconditionally, for the case where
/// the configuration source cannot be observed by file metadata at all.</summary>
public sealed class ConnectionRegistry : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly object _gate = new();
    private readonly IDisposable? _reloadTokenSubscription;

    private Dictionary<string, ConnectionRegistryEntry> _entries = new(StringComparer.Ordinal);
    private string? _activeName;
    private string _sourceSignature = string.Empty;
    private bool _stale;
    private DateTimeOffset _lastLoadedAtUtc;

    /// <summary>Captures a malformed-configuration failure from <see cref="Load"/> instead of
    /// letting it escape the constructor. This type is registered as a DI singleton and resolved
    /// lazily - the first thing that resolves it is <c>EfCoreMcpTools</c>'s constructor, which runs
    /// during the MCP SDK's own tool-activation step, entirely outside of
    /// <c>EfCoreMcpTools.Execute</c>/<c>ExecuteAsync</c>'s exception handling. A throw from here
    /// would therefore never reach those handlers (which already know how to surface this
    /// exception's actionable message intact) and would instead be caught by the MCP framework's
    /// generic invocation wrapper, producing an undiagnosable "An error occurred invoking 'x'."
    /// message for every single tool call. Deferring the throw to first actual use (see
    /// <see cref="EnsureLoaded"/>) means it is always raised from inside a tool method body, where
    /// the existing handling applies normally.</summary>
    private ConnectionRegistryConfigurationException? _configurationError;

    /// <summary>Non-null when the most recent read of the "Connections" configuration failed
    /// validation. Every tool call already surfaces this (via <see cref="EnsureLoaded"/>) with its
    /// actual message intact; this property additionally lets the server log the same failure
    /// prominently at startup, before any MCP client has made a call. Reading it never triggers a
    /// refresh, so startup logging reports the startup state.</summary>
    public ConnectionRegistryConfigurationException? ConfigurationError
    {
        get
        {
            lock (_gate)
            {
                return _configurationError;
            }
        }
    }

    /// <summary>When the registry last (re-)read its configuration source. Surfaced to MCP callers
    /// so a stale-looking connection list can be reasoned about rather than guessed at.</summary>
    public DateTimeOffset LastLoadedAtUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastLoadedAtUtc;
            }
        }
    }

    public ConnectionRegistry(IConfiguration configuration)
    {
        _configuration = configuration;

        lock (_gate)
        {
            // Forced rather than a plain ApplyLoad: the configuration may have been built before a
            // source file existed, in which case the provider still holds no data while the file is
            // already on disk. Recording that file's signature against the provider's stale data
            // would make EnsureCurrent conclude everything is current and serve an empty registry
            // indefinitely.
            _stale = true;
            EnsureCurrent();
        }

        // A provider that was registered with reloadOnChange:true refreshes itself and raises this
        // token; the file-metadata check in EnsureCurrent covers the providers that do not.
        _reloadTokenSubscription = ChangeToken.OnChange(
            _configuration.GetReloadToken,
            () =>
            {
                lock (_gate)
                {
                    _stale = true;
                }
            });
    }

    public IReadOnlyCollection<string> ConnectionNames
    {
        get
        {
            lock (_gate)
            {
                EnsureCurrent();
                EnsureLoaded();
                return _entries.Keys.ToArray();
            }
        }
    }

    /// <summary>Name of the currently active connection, i.e. the one used as the default when an
    /// MCP tool call does not specify an explicit connection name. Null when no connection is
    /// configured.</summary>
    public string? ActiveConnectionName
    {
        get
        {
            lock (_gate)
            {
                EnsureCurrent();
                EnsureLoaded();
                return _activeName;
            }
        }
    }

    /// <summary>The currently active <see cref="ConnectionRegistryEntry"/>, or null when no
    /// connection is configured. Never a production connection unless one was explicitly activated
    /// with acknowledgment.</summary>
    public ConnectionRegistryEntry? ActiveConnection
    {
        get
        {
            lock (_gate)
            {
                EnsureCurrent();
                EnsureLoaded();
                return _activeName is not null && _entries.TryGetValue(_activeName, out var entry) ? entry : null;
            }
        }
    }

    public bool TryGet(string name, out ConnectionRegistryEntry entry)
    {
        lock (_gate)
        {
            EnsureCurrent();
            EnsureLoaded();
            return _entries.TryGetValue(name, out entry!);
        }
    }

    /// <summary>Fails closed: throws <see cref="UnknownConnectionException"/> rather than falling
    /// back to any default connection when <paramref name="name"/> isn't registered.</summary>
    public ConnectionRegistryEntry Get(string name)
    {
        lock (_gate)
        {
            EnsureCurrent();
            EnsureLoaded();

            if (!_entries.TryGetValue(name, out var entry))
            {
                throw new UnknownConnectionException(name, _entries.Keys.ToArray());
            }

            return entry;
        }
    }

    /// <summary>Makes <paramref name="name"/> the active connection used as the default by MCP tools
    /// that don't specify an explicit connection. Refuses (throws <see cref="ProductionProtectedException"/>)
    /// when the target is designated as production and <paramref name="allowProduction"/> is false,
    /// so a production database can't become the default unless the caller explicitly opts in.</summary>
    public void SetActive(string name, bool allowProduction = false)
    {
        lock (_gate)
        {
            EnsureCurrent();
            EnsureLoaded();

            if (!_entries.TryGetValue(name, out var entry))
            {
                throw new UnknownConnectionException(name, _entries.Keys.ToArray());
            }

            if (entry.IsProduction && !allowProduction)
            {
                throw new ProductionProtectedException(name);
            }

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
            EnsureCurrent();
            EnsureLoaded();
            return BuildConnectionInfos_NoLock();
        }
    }

    /// <summary>Unconditionally re-reads the "Connections" configuration and returns the resulting
    /// redacted view, along with whether the set of registered connection names changed. This is the
    /// explicit escape hatch behind the <c>reload_connections</c> tool: the automatic refresh in
    /// <see cref="EnsureCurrent"/> only observes file-backed sources, so a configuration source that
    /// cannot be fingerprinted (an in-memory or custom provider, or an environment variable changed
    /// in-process) still needs a way to be picked up without restarting the MCP client.</summary>
    public ConnectionReloadResult Reload()
    {
        lock (_gate)
        {
            var previousNames = _entries.Keys.ToArray();

            // Ask every reload-capable provider to re-read its own source first; otherwise a
            // user-secrets/JSON provider keeps serving the values it parsed at startup and the
            // reload would be a no-op.
            if (_configuration is IConfigurationRoot root)
            {
                root.Reload();
            }

            ApplyLoad();
            _stale = false;

            var currentNames = _configurationError is null ? _entries.Keys.ToArray() : [];
            var added = currentNames.Except(previousNames, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var removed = previousNames.Except(currentNames, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();

            return new ConnectionReloadResult(
                added,
                removed,
                _lastLoadedAtUtc,
                _configurationError,
                _configurationError is null ? BuildConnectionInfos_NoLock() : []);
        }
    }

    private IReadOnlyList<ConnectionInfo> BuildConnectionInfos_NoLock() =>
        _entries.Values
            .OrderBy(e => e.IsProduction)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => new ConnectionInfo(
                e.Name,
                e.Provider,
                e.AccessMode,
                e.Environment,
                e.IsProduction,
                IsActive: string.Equals(e.Name, _activeName, StringComparison.Ordinal),
                e.Source))
            .ToList();

    /// <summary>Re-throws the configuration failure captured by the most recent load, if any. See
    /// <see cref="_configurationError"/> for why this is deferred rather than thrown eagerly.</summary>
    private void EnsureLoaded()
    {
        if (_configurationError is not null)
        {
            throw _configurationError;
        }
    }

    /// <summary>Re-reads the configuration when a reload token fired or any file-backed source's
    /// path/length/timestamp changed since the last load. The fingerprint check exists because
    /// <c>AddUserSecrets</c>/<c>AddJsonFile</c> are registered without <c>reloadOnChange</c> here,
    /// so they neither refresh themselves nor raise a token when an external edit (such as
    /// <c>dotnet user-secrets set</c>) rewrites the file.</summary>
    private void EnsureCurrent()
    {
        if (!_stale && string.Equals(_sourceSignature, ComputeSourceSignature(), StringComparison.Ordinal))
        {
            return;
        }

        if (_configuration is IConfigurationRoot root)
        {
            root.Reload();
        }

        ApplyLoad();
        _stale = false;
    }

    /// <summary>Parses the current configuration into <see cref="_entries"/>, preserving the active
    /// connection selection when that connection still exists. Must be called under
    /// <see cref="_gate"/>.</summary>
    private void ApplyLoad()
    {
        var previousActive = _activeName;
        var previousEntries = _entries;

        try
        {
            _entries = Load(_configuration, previousEntries);
            _configurationError = null;
        }
        catch (ConnectionRegistryConfigurationException ex)
        {
            _entries = new Dictionary<string, ConnectionRegistryEntry>(StringComparer.Ordinal);
            _configurationError = ex;
            _activeName = null;
            _sourceSignature = ComputeSourceSignature();
            _lastLoadedAtUtc = DateTimeOffset.UtcNow;
            return;
        }

        // A swap_connection selection must survive a refresh triggered by an unrelated edit, so the
        // previous choice is kept whenever it is still registered; only a selection that vanished
        // falls back to the startup rule (first non-production connection). A selection that was
        // reclassified as production is dropped too - staying active would silently carry over an
        // acknowledgment that was given for a non-production target.
        _activeName = previousActive is not null
            && _entries.TryGetValue(previousActive, out var retained)
            && !(retained.IsProduction && previousEntries.TryGetValue(previousActive, out var before) && !before.IsProduction)
                ? previousActive
                : _entries.Values.FirstOrDefault(entry => !entry.IsProduction)?.Name;

        _sourceSignature = ComputeSourceSignature();
        _lastLoadedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Returns the <see cref="ConnectionRegistryEntry.ConfigurationRevision"/> a freshly
    /// parsed connection should carry: the previous one, bumped when this reload repointed the name
    /// at a different database, so <see cref="ConnectionRegistryEntry.SchemaCacheKey"/> changes only
    /// when the cached schema would actually be wrong.</summary>
    private static int ResolveConfigurationRevision(
        IReadOnlyDictionary<string, ConnectionRegistryEntry> previous,
        string name,
        ConnectionSource source,
        DatabaseProvider? provider,
        string? connectionString)
    {
        if (!previous.TryGetValue(name, out var before))
        {
            return 0;
        }

        var repointed = before.Source != source
            || before.Provider != provider
            || !string.Equals(before.ConnectionString, connectionString, StringComparison.Ordinal);

        return repointed ? before.ConfigurationRevision + 1 : before.ConfigurationRevision;
    }

    /// <summary>A cheap fingerprint of every file-backed configuration source: path plus length plus
    /// last-write time. Only file metadata is read - never the file contents - so this stays
    /// inexpensive enough to run on every registry access.</summary>
    private string ComputeSourceSignature()
    {
        if (_configuration is not IConfigurationRoot root)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var provider in root.Providers)
        {
            if (provider is not FileConfigurationProvider fileProvider || fileProvider.Source.Path is not { } path)
            {
                continue;
            }

            var fileInfo = fileProvider.Source.FileProvider?.GetFileInfo(path);
            parts.Add(fileInfo is { Exists: true }
                ? $"{path}|{fileInfo.Length}|{fileInfo.LastModified.UtcTicks}"
                : $"{path}|missing");
        }

        return string.Join(";", parts);
    }

    public void Dispose() => _reloadTokenSubscription?.Dispose();

    private static Dictionary<string, ConnectionRegistryEntry> Load(
        IConfiguration configuration,
        IReadOnlyDictionary<string, ConnectionRegistryEntry> previousEntries)
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
            var sourceRaw = child["Source"];

            var source = ConnectionSource.Explicit;
            if (!string.IsNullOrWhiteSpace(sourceRaw))
            {
                if (!Enum.TryParse(sourceRaw, ignoreCase: true, out source))
                {
                    var allowedSources = string.Join(", ", Enum.GetNames<ConnectionSource>());
                    throw new ConnectionRegistryConfigurationException(
                        $"Connection '{name}' has invalid Source '{sourceRaw}'. Allowed sources: {allowedSources}.");
                }
            }

            if (source == ConnectionSource.Explicit)
            {
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new ConnectionRegistryConfigurationException(
                        $"Connection '{name}' is missing a required 'ConnectionString' value.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(connectionString))
            {
                // ApplicationFactory connections get their connection string from the target's own
                // IDesignTimeDbContextFactory<TContext> at resolution time - configuring one here would
                // be ambiguous about which one is authoritative, so it's rejected outright rather than
                // silently ignored or silently preferred.
                throw new ConnectionRegistryConfigurationException(
                    $"Connection '{name}' has Source '{source}' and must not configure a 'ConnectionString' " +
                    "- it is supplied by the target's own IDesignTimeDbContextFactory<TContext> at resolution time.");
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
                Source = source,
                ConnectionString = connectionString,
                AccessMode = accessMode,
                CommandTimeoutSeconds = commandTimeoutSeconds,
                Environment = environment,
                AccessPolicy = accessPolicy,
                ConfigurationRevision = ResolveConfigurationRevision(previousEntries, name, source, provider, connectionString),
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
    bool IsActive,
    ConnectionSource Source = ConnectionSource.Explicit);

/// <summary>Outcome of <see cref="ConnectionRegistry.Reload"/>: which connection names appeared or
/// disappeared relative to the previously loaded set, when the reload happened, the post-reload
/// redacted view, and the configuration failure if the reloaded configuration is malformed (in
/// which case <see cref="Connections"/> is empty and every later tool call surfaces the same
/// error).</summary>
public sealed record ConnectionReloadResult(
    IReadOnlyList<string> AddedConnectionNames,
    IReadOnlyList<string> RemovedConnectionNames,
    DateTimeOffset LoadedAtUtc,
    ConnectionRegistryConfigurationException? ConfigurationError,
    IReadOnlyList<ConnectionInfo> Connections);
