using Microsoft.EntityFrameworkCore;

namespace SampleApp;

/// <summary>Primary sample DbContext. Uses the ASP.NET Core convention of a public constructor
/// accepting <see cref="DbContextOptions{TContext}"/> - this is the preferred/most common
/// construction path the server's activator looks for first.</summary>
public class SampleAppDbContext : DbContext
{
    public SampleAppDbContext(DbContextOptions<SampleAppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>()
            .HasMany(c => c.Orders)
            .WithOne(o => o.Customer)
            .HasForeignKey(o => o.CustomerId);
    }
}
