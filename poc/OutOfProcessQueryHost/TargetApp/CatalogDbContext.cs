using Microsoft.EntityFrameworkCore;

namespace TargetApp;

/// <summary>The "target application"'s own <see cref="DbContext"/>. Uses the common ASP.NET Core
/// construction convention (a public constructor accepting <c>DbContextOptions&lt;TContext&gt;</c>)
/// so QueryHost can build it the same way the real MCP server's <c>DbContextActivator</c> does.</summary>
public class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options)
        : base(options)
    {
    }

    public DbSet<Product> Products => Set<Product>();
}
