namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>The query request DSL described in the project design: a JSON-serializable shape
/// executed via System.Linq.Dynamic.Core against a single entity's <c>DbSet</c>. Individual
/// fields map 1:1 onto the <c>run_query</c> MCP tool's parameters; this type exists as the shared,
/// independently testable representation of "a query" used by <see cref="QueryExecutor"/>.</summary>
public sealed class QueryRequest
{
    /// <summary>CLR type name of the entity to query (as it appears in <c>get_schema</c>'s
    /// output), resolved via reflection against the DbContext's model.</summary>
    public required string Entity { get; init; }

    /// <summary>Optional Dynamic LINQ predicate string, e.g. <c>"Age > 18 and Name.Contains(@0)"</c>.
    /// Positional parameters (<c>@0</c>, <c>@1</c>, ...) are always resolved from
    /// <see cref="Parameters"/> - never string-concatenated into this expression.</summary>
    public string? Where { get; init; }

    /// <summary>Positional parameters referenced from <see cref="Where"/> and/or
    /// <see cref="OrderBy"/> as <c>@0</c>, <c>@1</c>, ...</summary>
    public IReadOnlyList<object?>? Parameters { get; init; }

    /// <summary>Optional Dynamic LINQ order-by string, e.g. <c>"Age desc"</c>.</summary>
    public string? OrderBy { get; init; }

    public int? Skip { get; init; }

    /// <summary>Requested row limit. Always clamped to the server's configured maximum
    /// (<see cref="QueryExecutionOptions.MaxTake"/>), even if omitted or larger than the max.</summary>
    public int? Take { get; init; }

    /// <summary>Navigation property names to eager-load. Each name MUST be an actual navigation
    /// property on the requested entity's EF Core model - anything else is rejected rather than
    /// silently ignored, to avoid unbounded/unexpected `.Include()` graphs.</summary>
    public IReadOnlyList<string>? Include { get; init; }
}
