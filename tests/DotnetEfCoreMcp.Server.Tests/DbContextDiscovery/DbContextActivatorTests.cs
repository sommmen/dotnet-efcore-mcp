using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace DotnetEfCoreMcp.Server.Tests.DbContextDiscovery;

public sealed class DbContextActivatorTests
{
    [Fact]
    public void CreateInstance_OptionsConstructorContext_UsesRegistryConnectionString()
    {
        using var db = new SqliteTestDatabase();
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);
        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors.Single(d => d.Name == "SampleAppDbContext");

        using var context = DbContextActivator.CreateInstance(descriptor.ClrType, db.ToRegistryEntry(), DatabaseProvider.Sqlite);

        Assert.Equal(db.ConnectionString, context.Database.GetConnectionString());
    }

    [Fact]
    public void CreateInstance_ParameterlessOnConfiguringContext_OverridesHardcodedConnectionString()
    {
        using var db = new SqliteTestDatabase();
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);
        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors.Single(d => d.Name == "LegacyOnConfiguringDbContext");

        using var context = DbContextActivator.CreateInstance(descriptor.ClrType, db.ToRegistryEntry(), DatabaseProvider.Sqlite);

        // The fixture's OnConfiguring hardcodes a bogus filename; if the server's override didn't
        // work this would still be "Data Source=__should_never_be_used__.db".
        Assert.Equal(db.ConnectionString, context.Database.GetConnectionString());
        Assert.DoesNotContain("should_never_be_used", context.Database.GetConnectionString());
    }

    [Fact]
    public void CreateInstance_DesignTimeFactoryContext_OverridesHardcodedConnectionString()
    {
        using var db = new SqliteTestDatabase();
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);
        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors.Single(d => d.Name == "FactoryOnlyDbContext");

        using var context = DbContextActivator.CreateInstance(descriptor.ClrType, db.ToRegistryEntry(), DatabaseProvider.Sqlite);

        Assert.Equal(db.ConnectionString, context.Database.GetConnectionString());
        Assert.DoesNotContain("factory_should_never_be_used", context.Database.GetConnectionString());
    }

    [Fact]
    public void CreateInstance_ConstructedContext_CanActuallyReadAndWriteTheRegisteredDatabase()
    {
        using var db = new SqliteTestDatabase();
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);
        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors.Single(d => d.Name == "SampleAppDbContext");

        using (var writeContext = DbContextActivator.CreateInstance(descriptor.ClrType, db.ToRegistryEntry(), DatabaseProvider.Sqlite))
        {
            writeContext.Database.EnsureCreated();
            var customerType = EntitySeeding.GetEntityClrType(writeContext, "Customer");
            var customer = EntitySeeding.CreateEntity(customerType, new Dictionary<string, object?>
            {
                ["Name"] = "Ada Lovelace",
                ["Age"] = 36,
            });
            writeContext.Add(customer);
            writeContext.SaveChanges();
        }

        using var readContext = DbContextActivator.CreateInstance(descriptor.ClrType, db.ToRegistryEntry(), DatabaseProvider.Sqlite);
        var customerClrType = EntitySeeding.GetEntityClrType(readContext, "Customer");
        var setMethod = typeof(Microsoft.EntityFrameworkCore.DbContext).GetMethod("Set", Type.EmptyTypes)!.MakeGenericMethod(customerClrType);
        var dbSet = (System.Collections.IEnumerable)setMethod.Invoke(readContext, null)!;

        var names = dbSet.Cast<object>().Select(e => EntitySeeding.GetPropertyValue(e, "Name")).ToList();
        Assert.Contains("Ada Lovelace", names);
    }
}
