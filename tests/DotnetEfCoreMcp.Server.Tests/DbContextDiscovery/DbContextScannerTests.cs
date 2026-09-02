using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.DbContextDiscovery;

public sealed class DbContextScannerTests
{
    [Fact]
    public void FindDbContextTypes_DiscoversAllFiveFixtureContexts()
    {
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);

        var scan = DbContextScanner.FindDbContextTypes(handle.Assembly);

        var names = scan.Descriptors.Select(d => d.Name).ToHashSet();
        Assert.Contains("SampleAppDbContext", names);
        Assert.Contains("LegacyOnConfiguringDbContext", names);
        Assert.Contains("FactoryOnlyDbContext", names);
        Assert.Contains("WarningsConfiguredDbContext", names);
        Assert.Contains("NonGenericOptionsDbContext", names);
        Assert.Equal(5, scan.Descriptors.Count);
        Assert.Empty(scan.TypeLoadWarnings);
    }

    [Fact]
    public void FindDbContextTypes_PartialTypeLoadFailure_RetainsContextsAndReportsWarnings()
    {
        var sourceDirectory = Path.GetDirectoryName(FixturePaths.BrokenDependencyAppDllPath)!;
        var scratchDirectory = Path.Combine(Path.GetTempPath(), $"dotnet-efcore-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchDirectory);

        try
        {
            foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory))
            {
                File.Copy(sourceFile, Path.Combine(scratchDirectory, Path.GetFileName(sourceFile)));
            }

            File.Delete(Path.Combine(scratchDirectory, "BrokenDependencyApp.Dependency.dll"));

            var (descriptorNames, typeLoadWarnings) = LoadAndScanThenUnload(Path.Combine(scratchDirectory, "BrokenDependencyApp.dll"));

            Assert.Contains(descriptorNames, name => name == "GoodDbContext");
            Assert.Contains(typeLoadWarnings, warning => warning.Contains("failed to load", StringComparison.Ordinal));
            Assert.Contains(typeLoadWarnings, warning => warning.Contains("Type load error:", StringComparison.Ordinal));

            // The scratch DLLs are locked by the collectible AssemblyLoadContext until it is
            // unloaded and collected; force a full GC before deleting the scratch directory in
            // the finally block below, otherwise cleanup can hit an UnauthorizedAccessException
            // on Windows while the files are still memory-mapped. The load/scan/unload work is
            // isolated in a non-inlined helper so no Assembly/Type references from the collectible
            // AssemblyLoadContext remain rooted on this method's stack while we collect.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Sleep(50);
            }
        }
        finally
        {
            DeleteDirectoryWithRetry(scratchDirectory);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (IReadOnlyList<string> DescriptorNames, IReadOnlyList<string> TypeLoadWarnings) LoadAndScanThenUnload(string assemblyPath)
    {
        var service = new AssemblyLoaderService();
        var handle = service.Load(assemblyPath);
        var scan = DbContextScanner.FindDbContextTypes(handle.Assembly);

        var result = (
            DescriptorNames: (IReadOnlyList<string>)scan.Descriptors.Select(d => d.Name).ToList(),
            TypeLoadWarnings: (IReadOnlyList<string>)scan.TypeLoadWarnings.ToList());

        service.Unload();
        return result;
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        const int maxAttempts = 20;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(200);
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(200);
            }
        }
    }

    [Fact]
    public void FindDbContextTypes_ClassifiesSampleAppDbContextAsOptionsConstructor()
    {
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);

        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors
            .Single(d => d.Name == "SampleAppDbContext");

        Assert.Equal(DbContextConstructionKind.OptionsConstructor, descriptor.ConstructionKind);
    }

    [Fact]
    public void FindDbContextTypes_ClassifiesLegacyOnConfiguringAsParameterless()
    {
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);

        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors
            .Single(d => d.Name == "LegacyOnConfiguringDbContext");

        Assert.Equal(DbContextConstructionKind.ParameterlessOnConfiguring, descriptor.ConstructionKind);
    }

    [Fact]
    public void FindDbContextTypes_ClassifiesFactoryOnlyAsDesignTimeFactory()
    {
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);

        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors
            .Single(d => d.Name == "FactoryOnlyDbContext");

        Assert.Equal(DbContextConstructionKind.DesignTimeFactory, descriptor.ConstructionKind);
    }

    [Fact]
    public void FindDbContextTypes_DoesNotIncludeTheFactoryTypeItself()
    {
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);

        var descriptors = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors;

        Assert.DoesNotContain(descriptors, d => d.Name == "FactoryOnlyDbContextFactory");
    }
}
