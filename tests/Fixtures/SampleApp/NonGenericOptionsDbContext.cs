using Microsoft.EntityFrameworkCore;

namespace SampleApp;

/// <summary>A DbContext whose public constructor accepts the non-generic
/// <see cref="DbContextOptions"/> rather than the generic <see cref="DbContextOptions{TContext}"/>
/// form. Exercises the server's <c>DbContextConstructorShape.NonGenericOptions</c> construction
/// path, which is otherwise untested (<see cref="SampleAppDbContext"/> only covers the generic
/// options shape).</summary>
public class NonGenericOptionsDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
}
