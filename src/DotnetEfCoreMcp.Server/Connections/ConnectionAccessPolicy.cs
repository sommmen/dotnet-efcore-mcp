using DotnetEfCoreMcp.Server.DbContextDiscovery;

namespace DotnetEfCoreMcp.Server.Connections;

/// <summary>An exact <c>&lt;context full name&gt;:&lt;entity name&gt;</c> selector used by
/// <see cref="ConnectionAccessPolicy.AllowEntities"/>/<see cref="ConnectionAccessPolicy.DenyEntities"/>.
/// Both parts are matched case-sensitively and ordinally.</summary>
public readonly record struct EntitySelector(string ContextFullName, string EntityName)
{
    public override string ToString() => $"{ContextFullName}:{EntityName}";

    /// <summary>Parses a raw <c>context:entity</c> selector string. A selector is malformed unless it
    /// contains exactly one <c>:</c> separator with non-empty context and entity parts.</summary>
    public static bool TryParse(string? raw, out EntitySelector selector)
    {
        selector = default;
        if (string.IsNullOrEmpty(raw))
            return false;

        var parts = raw.Split(':');
        if (parts.Length != 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
            return false;

        selector = new EntitySelector(parts[0], parts[1]);
        return true;
    }
}

/// <summary>The required, per-connection, server-side access policy that allowlists exactly which
/// <c>DbContext</c> types and EF entity names an MCP client may reach through a given named
/// connection (see docs/development/connections.md, "P0 #9"). Never supplied or overridable by an
/// MCP client - only ever loaded from server-side configuration alongside the rest of a
/// <see cref="ConnectionRegistryEntry"/>.
///
/// The evaluator is fail-closed: a candidate (context or entity) is permitted only when it matches
/// an allow selector; a matching allow always wins over a matching deny (allowlist-over-deny
/// precedence); anything that matches neither an allow nor a deny selector is denied by default.
/// Entity access additionally requires a reachable context - an entity-level allow can expose one
/// entity within its context without exposing any other entity in that same context.</summary>
public sealed record ConnectionAccessPolicy
{
    public required IReadOnlyList<string> AllowContexts { get; init; }

    public required IReadOnlyList<string> DenyContexts { get; init; }

    public required IReadOnlyList<EntitySelector> AllowEntities { get; init; }

    public required IReadOnlyList<EntitySelector> DenyEntities { get; init; }

    /// <summary>True when <paramref name="contextFullName"/> is reachable at all through this
    /// policy - either because it is explicitly allowlisted at the context level, or because at
    /// least one entity within it has been individually allowlisted. Used to gate every
    /// context/entity tool path before a <c>DbContext</c> is constructed, and to decide whether a
    /// context is included in a filtered discovery view (e.g. <c>list_contexts</c>).</summary>
    public bool IsContextReachable(string? contextFullName)
    {
        if (contextFullName is null)
            return false;

        return AllowContexts.Contains(contextFullName, StringComparer.Ordinal) ||
               AllowEntities.Any(e => string.Equals(e.ContextFullName, contextFullName, StringComparison.Ordinal));
    }

    /// <summary>True when <paramref name="contextFullName"/> is allowed as a whole - i.e. every
    /// entity in it is permitted unless a more specific entity-level rule says otherwise. This is
    /// the "blanket" context allow, distinct from <see cref="IsContextReachable"/> (which also
    /// counts a context as reachable purely because of a narrower per-entity allow).</summary>
    public bool IsContextAllowed(string? contextFullName) =>
        contextFullName is not null && AllowContexts.Contains(contextFullName, StringComparer.Ordinal);

    /// <summary>True when <paramref name="contextFullName"/> is explicitly denylisted at the
    /// context level.</summary>
    public bool IsContextDenied(string? contextFullName) =>
        contextFullName is not null && DenyContexts.Contains(contextFullName, StringComparer.Ordinal);

    /// <summary>Evaluates whether <paramref name="entityName"/> within <paramref name="contextFullName"/>
    /// is permitted, applying allowlist-over-deny precedence and failing closed when nothing
    /// matches.</summary>
    public bool IsEntityAllowed(string? contextFullName, string entityName)
    {
        if (contextFullName is null)
            return false;

        var allowMatch = IsContextAllowed(contextFullName) ||
            AllowEntities.Any(e => string.Equals(e.ContextFullName, contextFullName, StringComparison.Ordinal) &&
                                    string.Equals(e.EntityName, entityName, StringComparison.Ordinal));
        if (allowMatch)
            return true;

        // No matching allow: denied whether or not a deny selector also matches (fail-closed
        // default), so no further check is needed here - but evaluating the deny lists is kept
        // implicit rather than explicit since the outcome is identical either way.
        return false;
    }

    /// <summary>Validates that every configured selector actually resolves against
    /// <paramref name="discoveredContexts"/> - the set of <c>DbContext</c> types discovered in the
    /// currently loaded target assembly. A context selector must match a discovered context's
    /// <see cref="DbContextDescriptor.FullName"/>; an entity selector's context part must match a
    /// discovered context, and its entity part must match the entity type of one of that context's
    /// public <c>DbSet&lt;T&gt;</c> properties. Throws <see cref="ConnectionRegistryConfigurationException"/>
    /// (not a runtime denial) on the first unresolved selector, since an unresolved selector is a
    /// server misconfiguration rather than a request-time authorization outcome.</summary>
    public void EnsureResolvable(string connectionName, IReadOnlyCollection<DbContextDescriptor> discoveredContexts)
    {
        var contextsByFullName = discoveredContexts
            .Where(c => c.FullName is not null)
            .ToLookup(c => c.FullName!, StringComparer.Ordinal);

        foreach (var contextSelector in AllowContexts.Concat(DenyContexts))
        {
            if (!contextsByFullName.Contains(contextSelector))
            {
                throw new ConnectionRegistryConfigurationException(
                    $"Connection '{connectionName}' has an AccessPolicy context selector '{contextSelector}' " +
                    "that does not resolve to any DbContext in the currently loaded assembly.");
            }
        }

        foreach (var entitySelector in AllowEntities.Concat(DenyEntities))
        {
            var matchingContexts = contextsByFullName[entitySelector.ContextFullName].ToArray();
            if (matchingContexts.Length == 0 ||
                !matchingContexts.Any(c => HasEntity(c.ClrType, entitySelector.EntityName)))
            {
                throw new ConnectionRegistryConfigurationException(
                    $"Connection '{connectionName}' has an AccessPolicy entity selector '{entitySelector}' " +
                    "that does not resolve to any entity reachable from a DbSet on that DbContext in the " +
                    "currently loaded assembly.");
            }
        }
    }

    private static bool HasEntity(Type contextType, string entityName) =>
        contextType.GetProperties()
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(Microsoft.EntityFrameworkCore.DbSet<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0])
            .Any(entityType => string.Equals(entityType.Name, entityName, StringComparison.Ordinal));
}
