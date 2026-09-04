namespace DotnetEfCoreMcp.Server.Schema;

/// <summary>A compact search match: the entity's own name plus the names of any of its properties
/// and relationships (navigations) that matched the query. Full entity definitions are never
/// returned by search - callers follow up with <see cref="SchemaSlicer.FindEntity"/> (exposed as the
/// <c>get_entity_schema</c> tool) for the complete slice.</summary>
public sealed record SchemaSearchMatch(
    string EntityName,
    bool EntityNameMatched,
    IReadOnlyList<string> MatchingProperties,
    IReadOnlyList<string> MatchingRelationships);

/// <summary>The outcome of a bounded <see cref="SchemaSlicer.Search"/> call: the capped, ordered
/// matches plus the total number of entities that matched before the cap was applied, so callers can
/// tell whether results were truncated.</summary>
public sealed record SchemaSearchResult(IReadOnlyList<SchemaSearchMatch> Matches, int TotalMatchCount);

/// <summary>Read-only slicing/search over an already-built, cached <see cref="SchemaDto"/>. Never
/// constructs a <c>DbContext</c>, opens a database connection, or rediscovers the EF Core model -
/// every operation here is pure in-memory metadata access against the schema the caller already
/// obtained from <see cref="SchemaCache"/>. Both entry points route the schema through an
/// <see cref="ISchemaAccessPolicy"/> first, so a future access-policy evaluator can restrict the
/// visible entities/properties/relationships without changing either tool's request or response
/// shape.</summary>
public static class SchemaSlicer
{
    /// <summary>Absolute upper bound on <c>search_schema</c>'s <c>maxResults</c>, per the P0 #6
    /// contract.</summary>
    public const int MaxSearchResults = 25;

    /// <summary>Default <c>maxResults</c> for <c>search_schema</c> when the caller does not supply
    /// one.</summary>
    public const int DefaultSearchResults = 10;

    /// <summary>Returns the complete cached definition for the entity whose name matches
    /// <paramref name="entityName"/> exactly (ordinal, case-sensitive - matching the entity names
    /// <see cref="SchemaBuilder"/> already exposes), or <c>null</c> when no visible entity has that
    /// name.</summary>
    public static EntityTypeSchema? FindEntity(SchemaDto schema, string entityName, ISchemaAccessPolicy policy)
    {
        var visible = policy.Apply(schema);
        return visible.Entities.FirstOrDefault(e => string.Equals(e.Name, entityName, StringComparison.Ordinal));
    }

    /// <summary>Searches entity names, property names, and relationship (navigation) names for a
    /// case-insensitive substring match on <paramref name="query"/>. Results are ordered
    /// deterministically by entity name (ordinal, case-insensitive; ties broken by CLR full name),
    /// then capped at <paramref name="maxResults"/>. <paramref name="maxResults"/> must already have
    /// been validated by the caller (see <see cref="MaxSearchResults"/>/<see cref="DefaultSearchResults"/>).</summary>
    public static SchemaSearchResult Search(SchemaDto schema, string query, int maxResults, ISchemaAccessPolicy policy)
    {
        var visible = policy.Apply(schema);

        var matches = visible.Entities
            .Select(entity => BuildMatch(entity, query))
            .Where(match => match is not null)
            .Select(match => match!)
            .OrderBy(match => match.EntityName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.EntityName, StringComparer.Ordinal)
            .ToList();

        var page = matches.Take(maxResults).ToList();
        return new SchemaSearchResult(page, matches.Count);
    }

    private static SchemaSearchMatch? BuildMatch(EntityTypeSchema entity, string query)
    {
        var entityNameMatched = ContainsIgnoreCase(entity.Name, query);
        var matchingProperties = entity.Properties
            .Where(p => ContainsIgnoreCase(p.Name, query))
            .Select(p => p.Name)
            .ToList();
        var matchingRelationships = entity.Navigations
            .Where(n => ContainsIgnoreCase(n.Name, query))
            .Select(n => n.Name)
            .ToList();

        if (!entityNameMatched && matchingProperties.Count == 0 && matchingRelationships.Count == 0)
            return null;

        return new SchemaSearchMatch(entity.Name, entityNameMatched, matchingProperties, matchingRelationships);
    }

    private static bool ContainsIgnoreCase(string candidate, string query)
        => candidate.Contains(query, StringComparison.OrdinalIgnoreCase);
}
