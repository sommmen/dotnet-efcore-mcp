using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SampleApp;

/// <summary>Regression fixture for issue #97: a target context that escalates EF Core's
/// <see cref="CoreEventId.RowLimitingOperationWithoutOrderByWarning"/> to a thrown exception, which
/// real applications commonly do behind <c>#if DEBUG</c> to catch non-deterministic paging early.
///
/// Against such a context, the server's own implicit row cap is itself a row-limiting operator, so
/// every unordered query the server caps fails before reaching the database - even queries that
/// succeed under plain EF Core without the cap. Shares <see cref="Customer"/>/<see cref="Order"/>
/// with <see cref="SampleAppDbContext"/> so a database created by that context can be queried
/// through this one.</summary>
public class ThrowOnUnorderedRowLimitDbContext : DbContext
{
    public ThrowOnUnorderedRowLimitDbContext(DbContextOptions<ThrowOnUnorderedRowLimitDbContext> options)
        : base(options)
    {
    }

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ConfigureWarnings(builder => builder.Throw(
            CoreEventId.RowLimitingOperationWithoutOrderByWarning,
            CoreEventId.FirstWithoutOrderByAndFilterWarning,
            CoreEventId.DistinctAfterOrderByWithoutRowLimitingOperatorWarning));
    }
}
