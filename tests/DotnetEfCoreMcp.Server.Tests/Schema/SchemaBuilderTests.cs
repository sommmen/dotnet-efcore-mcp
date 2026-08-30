using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Schema;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.Schema;

public sealed class SchemaBuilderTests
{
    private static (Microsoft.EntityFrameworkCore.DbContext Context, IDisposable Db) CreateSampleAppContext()
    {
        var db = new SqliteTestDatabase();
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);
        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly).Single(d => d.Name == "SampleAppDbContext");
        var context = DbContextActivator.CreateInstance(descriptor.ClrType, db.ToRegistryEntry());
        return (context, db);
    }

    [Fact]
    public void Build_IncludesBothFixtureEntities()
    {
        var (context, db) = CreateSampleAppContext();
        using (context)
        using (db)
        {
            var schema = SchemaBuilder.Build(context);

            var entityNames = schema.Entities.Select(e => e.Name).ToHashSet();
            Assert.Contains("Customer", entityNames);
            Assert.Contains("Order", entityNames);
        }
    }

    [Fact]
    public void Build_CustomerHasOrdersCollectionNavigation()
    {
        var (context, db) = CreateSampleAppContext();
        using (context)
        using (db)
        {
            var schema = SchemaBuilder.Build(context);

            var customer = schema.Entities.Single(e => e.Name == "Customer");
            var navigation = customer.Navigations.Single(n => n.Name == "Orders");

            Assert.True(navigation.IsCollection);
            Assert.Equal("Order", navigation.TargetEntity);
        }
    }

    [Fact]
    public void Build_OrderHasCustomerReferenceNavigationAndForeignKey()
    {
        var (context, db) = CreateSampleAppContext();
        using (context)
        using (db)
        {
            var schema = SchemaBuilder.Build(context);

            var order = schema.Entities.Single(e => e.Name == "Order");
            var navigation = order.Navigations.Single(n => n.Name == "Customer");
            Assert.False(navigation.IsCollection);

            var foreignKey = Assert.Single(order.ForeignKeys);
            Assert.Contains("CustomerId", foreignKey.Properties);
            Assert.Equal("Customer", foreignKey.PrincipalEntity);
        }
    }

    [Fact]
    public void Build_CustomerPrimaryKeyIsId()
    {
        var (context, db) = CreateSampleAppContext();
        using (context)
        using (db)
        {
            var schema = SchemaBuilder.Build(context);

            var customer = schema.Entities.Single(e => e.Name == "Customer");
            Assert.Equal(["Id"], customer.PrimaryKeyProperties);
        }
    }

    [Fact]
    public void Build_DoesNotRequireAnOpenDatabaseConnection()
    {
        // Uses a connection string pointing at a database file that is never created - schema
        // discovery must still succeed since it only reads compiled model metadata.
        var directory = Path.Combine(AppContext.BaseDirectory, "TestData");
        Directory.CreateDirectory(directory);
        var neverCreatedPath = Path.Combine(directory, $"never_created_{Guid.NewGuid():N}.db");

        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);
        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly).Single(d => d.Name == "SampleAppDbContext");
        var entry = new Server.Connections.ConnectionRegistryEntry
        {
            Name = "Unreachable",
            Provider = Server.Connections.DatabaseProvider.Sqlite,
            ConnectionString = $"Data Source={neverCreatedPath}",
        };

        using var context = DbContextActivator.CreateInstance(descriptor.ClrType, entry);

        var schema = SchemaBuilder.Build(context);

        Assert.NotEmpty(schema.Entities);
    }
}
