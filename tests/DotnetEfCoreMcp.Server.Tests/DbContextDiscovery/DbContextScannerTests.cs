using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.DbContextDiscovery;

public sealed class DbContextScannerTests
{
    [Fact]
    public void FindDbContextTypes_DiscoversAllThreeFixtureContexts()
    {
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);

        var descriptors = DbContextScanner.FindDbContextTypes(handle.Assembly);

        var names = descriptors.Select(d => d.Name).ToHashSet();
        Assert.Contains("SampleAppDbContext", names);
        Assert.Contains("LegacyOnConfiguringDbContext", names);
        Assert.Contains("FactoryOnlyDbContext", names);
        Assert.Equal(3, descriptors.Count);
    }

    [Fact]
    public void FindDbContextTypes_ClassifiesSampleAppDbContextAsOptionsConstructor()
    {
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);

        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly)
            .Single(d => d.Name == "SampleAppDbContext");

        Assert.Equal(DbContextConstructionKind.OptionsConstructor, descriptor.ConstructionKind);
    }

    [Fact]
    public void FindDbContextTypes_ClassifiesLegacyOnConfiguringAsParameterless()
    {
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);

        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly)
            .Single(d => d.Name == "LegacyOnConfiguringDbContext");

        Assert.Equal(DbContextConstructionKind.ParameterlessOnConfiguring, descriptor.ConstructionKind);
    }

    [Fact]
    public void FindDbContextTypes_ClassifiesFactoryOnlyAsDesignTimeFactory()
    {
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);

        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly)
            .Single(d => d.Name == "FactoryOnlyDbContext");

        Assert.Equal(DbContextConstructionKind.DesignTimeFactory, descriptor.ConstructionKind);
    }

    [Fact]
    public void FindDbContextTypes_DoesNotIncludeTheFactoryTypeItself()
    {
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);

        var descriptors = DbContextScanner.FindDbContextTypes(handle.Assembly);

        Assert.DoesNotContain(descriptors, d => d.Name == "FactoryOnlyDbContextFactory");
    }
}
