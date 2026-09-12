using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SampleApp;

public class ApplicationFactoryDbContext : DbContext
{
    internal ApplicationFactoryDbContext(DbContextOptions<ApplicationFactoryDbContext> options, object? sentinel = null)
        : base(options)
    {
    }

    public DbSet<Customer> Customers => Set<Customer>();
}

public class ApplicationFactoryDbContextFactory : IDesignTimeDbContextFactory<ApplicationFactoryDbContext>
{
    public const string ConnectionStringEnvironmentVariable = "DOTNET_EFCORE_MCP_APPLICATION_FACTORY_CONNECTION";

    public ApplicationFactoryDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)
            ?? throw new InvalidOperationException($"Environment variable '{ConnectionStringEnvironmentVariable}' is required.");
        var builder = new DbContextOptionsBuilder<ApplicationFactoryDbContext>();
        builder.UseSqlite(connectionString);
        return new ApplicationFactoryDbContext(builder.Options);
    }
}
