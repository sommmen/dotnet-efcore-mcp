namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>A LINQPad-style read-only query expression rooted at a public <c>DbSet&lt;T&gt;</c>
/// property on the selected <see cref="Microsoft.EntityFrameworkCore.DbContext"/>.</summary>
public sealed class QueryRequest
{
    /// <summary>For example, <c>Customers.Where(c =&gt; c.Age &gt; 18).Select(c =&gt; c.Name)</c>.</summary>
    public required string Query { get; init; }
}