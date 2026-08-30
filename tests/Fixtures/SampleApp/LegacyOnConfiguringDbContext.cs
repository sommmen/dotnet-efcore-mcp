using Microsoft.EntityFrameworkCore;

namespace SampleApp;

/// <summary>A DbContext that relies entirely on <see cref="OnConfiguring"/> with a hardcoded
/// (and intentionally wrong for real use) connection string - simulating an app that does not
/// take externally-supplied <see cref="DbContextOptions"/>. Exercises the server's
/// parameterless-constructor construction path; the server MUST override the connection string
/// via <c>Database.SetConnectionString</c> after construction rather than trusting this
/// hardcoded value, since connection strings only ever come from the server-side registry.</summary>
public class LegacyOnConfiguringDbContext : DbContext
{
    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            // Deliberately bogus: this should never actually be used to connect. If the server's
            // connection-string override logic is broken, tests exercising this context will
            // fail loudly (pointing at a nonexistent file) rather than silently succeeding
            // against the wrong database.
            optionsBuilder.UseSqlite("Data Source=__should_never_be_used__.db");
        }
    }
}
