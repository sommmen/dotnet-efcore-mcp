using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SampleApp;

/// <summary>A DbContext with no constructor matching the options-based patterns the server's
/// activator looks for first, forcing it to fall back to its
/// <see cref="IDesignTimeDbContextFactory{TContext}"/> construction path. The constructor
/// deliberately takes an extra sentinel parameter beyond <c>DbContextOptions&lt;T&gt;</c> so it
/// does not match the exact-signature lookups for the options-constructor paths (even though it
/// is otherwise internal, which alone would not be enough to exclude it - reflection can see
/// non-public constructors too).</summary>
public class FactoryOnlyDbContext : DbContext
{
    internal FactoryOnlyDbContext(DbContextOptions<FactoryOnlyDbContext> options, object? sentinel = null)
        : base(options)
    {
    }

    public DbSet<Customer> Customers => Set<Customer>();
}

public class FactoryOnlyDbContextFactory : IDesignTimeDbContextFactory<FactoryOnlyDbContext>
{
    public FactoryOnlyDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<FactoryOnlyDbContext>();
        // Deliberately bogus, same rationale as LegacyOnConfiguringDbContext: the server must
        // override this via Database.SetConnectionString using the registry entry, never trust
        // whatever a design-time factory happens to configure.
        builder.UseSqlite("Data Source=__factory_should_never_be_used__.db");
        return new FactoryOnlyDbContext(builder.Options);
    }
}
