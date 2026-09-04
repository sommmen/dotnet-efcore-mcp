using DotnetEfCoreMcp.Server.Connections;

namespace DotnetEfCoreMcp.Server.Schema;

/// <summary>Policy-ready seam for restricting the schema visible to the slicing/search tools
/// (<c>get_entity_schema</c>, <c>search_schema</c>). P0 #6 ships only <see cref="NoOpSchemaAccessPolicy"/>:
/// it supplies no access policy of its own. A later access-policy evaluator (P0 #9) can implement this
/// interface to filter entities, properties, and relationships out of the cached <see cref="SchemaDto"/>
/// before entity lookup or search matching runs, without changing either tool's public request or
/// response shape.</summary>
public interface ISchemaAccessPolicy
{
    /// <summary>Returns the subset of <paramref name="schema"/> that is visible for the current
    /// request. The no-op implementation returns <paramref name="schema"/> unchanged.</summary>
    SchemaDto Apply(SchemaDto schema);
}

/// <summary>Default, permissive <see cref="ISchemaAccessPolicy"/> that performs no filtering. P0 #6
/// does not implement access control; this stands in for a future policy evaluator.</summary>
public sealed class NoOpSchemaAccessPolicy : ISchemaAccessPolicy
{
    public static readonly NoOpSchemaAccessPolicy Instance = new();

    public SchemaDto Apply(SchemaDto schema) => schema;
}

/// <summary>P0 #9 <see cref="ISchemaAccessPolicy"/> implementation backed by a connection's
/// <see cref="ConnectionAccessPolicy"/>. Filters a cached <see cref="SchemaDto"/> down to the
/// entities permitted for <paramref name="contextFullName"/>, without ever mutating the shared,
/// cached instance - every filtered value is a fresh record built with <c>with</c>-expressions or
/// new lists. Foreign keys and navigations that would otherwise point at an excluded entity are
/// dropped from the entities that remain visible, so a permitted entity's schema never discloses
/// the existence of an excluded related entity.</summary>
public sealed class ConnectionSchemaAccessPolicy(ConnectionAccessPolicy policy, string? contextFullName) : ISchemaAccessPolicy
{
    public SchemaDto Apply(SchemaDto schema)
    {
        var permittedNames = schema.Entities
            .Where(e => policy.IsEntityAllowed(contextFullName, e.Name))
            .Select(e => e.Name)
            .ToHashSet(StringComparer.Ordinal);

        var filteredEntities = schema.Entities
            .Where(e => permittedNames.Contains(e.Name))
            .Select(e => e with
            {
                ForeignKeys = e.ForeignKeys.Where(fk => permittedNames.Contains(fk.PrincipalEntity)).ToList(),
                Navigations = e.Navigations.Where(nav => permittedNames.Contains(nav.TargetEntity)).ToList(),
                BaseEntityName = e.BaseEntityName is not null && permittedNames.Contains(e.BaseEntityName) ? e.BaseEntityName : null,
            })
            .ToList();

        return schema with { Entities = filteredEntities };
    }
}
