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
        modelBuilder.Entity<Customer>(builder =>
        {
            builder.ToTable("Customers", tableBuilder => tableBuilder.HasComment("Registered customers."));

            builder.Property(c => c.Version)
                .IsConcurrencyToken();

            builder.Property(c => c.Name)
                .HasMaxLength(200)
                .IsUnicode(false)
                .IsFixedLength(false)
                .HasComment("The customer's display name.");

            builder.HasIndex(c => c.Name)
                .IsUnique()
                .HasDatabaseName("IX_Customers_Name");

            builder.HasMany(c => c.Orders)
                .WithOne(o => o.Customer)
                .HasForeignKey(o => o.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Order>(builder =>
        {
            builder.Property(o => o.Amount)
                .HasPrecision(18, 2)
                .HasDefaultValueSql("0.0");

            builder.Property(o => o.CreatedAtUtc)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .ValueGeneratedOnAdd();
        });
    }
}
