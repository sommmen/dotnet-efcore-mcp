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
