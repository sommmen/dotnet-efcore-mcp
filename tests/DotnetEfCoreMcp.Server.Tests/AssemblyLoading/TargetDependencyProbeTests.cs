using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.AssemblyLoading;

/// <summary>Covers loading a target whose dependencies are not present in its own output folder.
/// A plain class library gets no probing paths baked into its build output, so
/// <see cref="System.Runtime.Loader.AssemblyDependencyResolver"/> resolves none of its NuGet
/// packages and the resolution has to fall through to <see cref="TargetDependencyProbe"/>.</summary>
public sealed class TargetDependencyProbeTests
{
    [Fact]
    public void PackageDependencyAppFixture_DoesNotShipItsDependencies()
    {
        // Guards the premise of the tests below: if a future SDK change (or an accidental
        // CopyLocalLockFileAssemblies) started copying packages into bin/, these tests would
        // silently stop covering the probe.
        var outputDirectory = Path.GetDirectoryName(FixturePaths.PackageDependencyAppDllPath)!;

        Assert.False(File.Exists(Path.Combine(outputDirectory, "Newtonsoft.Json.dll")));
        Assert.False(File.Exists(Path.Combine(outputDirectory, "Microsoft.AspNetCore.Http.Abstractions.dll")));
    }

    [Fact]
    public void Load_TargetWithUncopiedPackageDependencies_DiscoversDbContextWithoutTypeLoadWarnings()
    {
        var service = new AssemblyLoaderService();

        var handle = service.Load(FixturePaths.PackageDependencyAppDllPath);
        var scan = DbContextScanner.FindDbContextTypes(handle.Assembly);

        Assert.Empty(scan.TypeLoadWarnings);
        Assert.Contains(scan.Descriptors, d => d.FullName == "PackageDependencyApp.PackageDependencyDbContext");
    }

    [Fact]
    public void Load_TargetWithUncopiedPackageDependencies_ResolvesNuGetPackageFromRestoreFolder()
    {
        var service = new AssemblyLoaderService();

        var handle = service.Load(FixturePaths.PackageDependencyAppDllPath);
        var contextType = handle.Assembly.GetType("PackageDependencyApp.PackageDependencyDbContext", throwOnError: true)!;
        var jsonMethod = contextType.GetMethod("DescribeAsJson")!;

        // Forces Newtonsoft.Json to actually load: the return type cannot be materialised
        // unless the probe found the package DLL outside the target's output folder.
        Assert.Equal("Newtonsoft.Json", jsonMethod.ReturnType.Assembly.GetName().Name);
    }

    [Fact]
    public void Load_TargetReferencingAspNetCoreSharedFramework_ResolvesFrameworkAssembly()
    {
        var service = new AssemblyLoaderService();

        var handle = service.Load(FixturePaths.PackageDependencyAppDllPath);
        var contextType = handle.Assembly.GetType("PackageDependencyApp.PackageDependencyDbContext", throwOnError: true)!;
        var tenantMethod = contextType.GetMethod("ReadTenant")!;

        // HttpContext lives only in the installed Microsoft.AspNetCore.App shared framework,
        // which is neither in the target's output folder nor in its .deps.json.
        Assert.Equal("HttpContext", tenantMethod.GetParameters()[0].ParameterType.Name);
    }

    [Fact]
    public void Create_ForTargetWithoutProbingPaths_StillFindsPackageProbingRoots()
    {
        var probe = TargetDependencyProbe.Create(FixturePaths.PackageDependencyAppDllPath);

        Assert.NotEmpty(probe.ProbingRoots);
        Assert.All(probe.ProbingRoots, root => Assert.True(Directory.Exists(root)));
    }

    [Fact]
    public void ResolveAssembly_UnknownAssembly_ReturnsNull()
    {
        var probe = TargetDependencyProbe.Create(FixturePaths.PackageDependencyAppDllPath);

        Assert.Null(probe.ResolveAssembly(new System.Reflection.AssemblyName($"Not.A.Real.Assembly.{Guid.NewGuid():N}")));
    }
}
