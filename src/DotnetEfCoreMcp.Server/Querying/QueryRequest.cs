namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>A LINQPad-style read-only query expression rooted at a public <c>DbSet&lt;T&gt;</c>
/// property on the selected <see cref="Microsoft.EntityFrameworkCore.DbContext"/>.</summary>
public sealed class QueryRequest
{
    /// <summary>For example, <c>Customers.Where(c =&gt; c.Age &gt; 18).Select(c =&gt; c.Name)</c>.</summary>
    public required string Query { get; init; }

    /// <summary>Optional dot-separated EF navigation paths to load, such as <c>Orders.OrderLines</c>.</summary>
    public IReadOnlyList<string>? Include { get; init; }

    /// <summary>Optional forward-only cursor paging request.</summary>
    public QueryPagination? Pagination { get; init; }
}

/// <summary>Opt-in forward-only keyset pagination settings for <c>run_query</c>.</summary>
public sealed class QueryPagination
{
    /// <summary>The paging mode. Only <c>cursor</c> is supported.</summary>
    public required string Mode { get; init; }

    /// <summary>An opaque continuation token returned by a preceding cursor-paged result.</summary>
    public string? Cursor { get; init; }
}
