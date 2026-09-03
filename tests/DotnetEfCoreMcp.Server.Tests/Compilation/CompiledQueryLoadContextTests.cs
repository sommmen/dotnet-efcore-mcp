using System.Reflection;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.Compilation;

/// <summary>Unit tests for <see cref="CompiledQueryLoadContext"/>'s assembly resolution override -
/// the piece of the Roslyn user-query pipeline responsible for making sure a compiled query
/// assembly shares exactly the same <c>DbContext</c>/entity/EF Core types as the target project
/// and the server itself (see the "shared assembly identity" design in
/// <c>docs/development/roslyn-user-query.md</c>). These are true unit tests: they exercise
/// <see cref="CompiledQueryLoadContext"/> directly (it is <c>internal</c>, visible to this test
/// assembly via <c>InternalsVisibleTo</c>) against a real <see cref="LoadedAssemblyHandle"/> for
/// the SampleApp fixture, without going through the full compile-and-execute pipeline that
/// <c>RoslynQueryExecutorTests</c> already covers end-to-end.</summary>
public sealed class CompiledQueryLoadContextTests
{
    // CompiledQueryLoadContext.Load(AssemblyName) is a protected override, invoked by the CLR's
    // own assembly-resolution machinery in production. It is exercised here via reflection (rather
    // than the public AssemblyLoadContext.LoadFromAssemblyName wrapper) so the test asserts exactly
    // what Load itself returns - LoadFromAssemblyName has its own additional fallback behavior
    // (e.g. resolving already-loaded default-context assemblies, or throwing
    // FileNotFoundException for a null result) that would obscure that.
    private static readonly MethodInfo LoadMethod = typeof(CompiledQueryLoadContext)
        .GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static LoadedAssemblyHandle LoadSampleApp() =>
        new AssemblyLoaderService().Load(FixturePaths.SampleAppDllPath);

    private static CompiledQueryLoadContext CreateContext(LoadedAssemblyHandle target) =>
        new(target.Context, target.LoadedAssemblyPaths, $"CompiledQueryLoadContextTests.{Guid.NewGuid():N}");

    private static Assembly? InvokeLoad(CompiledQueryLoadContext context, AssemblyName assemblyName) =>
        (Assembly?)LoadMethod.Invoke(context, [assemblyName]);

    [Fact]
    public void Load_TargetAssemblyName_ReturnsSameInstanceAlreadyLoadedInTargetContext()
    {
        var target = LoadSampleApp();
        var loadContext = CreateContext(target);

        // "SampleApp" (the target's own main assembly) is already loaded into target.Context, so
        // Load() must reuse that exact instance rather than loading a second copy - a second copy
        // would be type-identity-incompatible with the DbContext type the server already resolved
        // from target.Assembly.
        var resolved = InvokeLoad(loadContext, new AssemblyName(target.Assembly.GetName().Name!));

        Assert.Same(target.Assembly, resolved);

        target.Unload();
    }

    [Fact]
    public void Load_SharedFrameworkAssemblyName_ReturnsNullToDelegateToDefaultContext()
    {
        var target = LoadSampleApp();
        var loadContext = CreateContext(target);

        Assert.Contains("Microsoft.EntityFrameworkCore", SharedFrameworkAssemblyNames.Value);

        var resolved = InvokeLoad(loadContext, new AssemblyName("Microsoft.EntityFrameworkCore"));

        Assert.Null(resolved);

        target.Unload();
    }

    [Fact]
    public void Load_UnknownAssemblyName_ReturnsNull()
    {
        var target = LoadSampleApp();
        var loadContext = CreateContext(target);

        var resolved = InvokeLoad(loadContext, new AssemblyName("Some.Totally.Unknown.Assembly.Name"));

        Assert.Null(resolved);

        target.Unload();
    }

    [Fact]
    public void Load_DependencyKnownByPathButNotYetLoaded_ResolvesViaTargetAndReturnsSameInstance()
    {
        // PackageDependencyApp depends on Newtonsoft.Json, which is not shipped in its own output
        // folder (see TargetDependencyProbeTests) and is not in SharedFrameworkAssemblyNames, so
        // TargetAssemblyLoadContext resolves it from the NuGet restore folder into *this* target
        // context the first time it is touched.
        var target = new AssemblyLoaderService().Load(FixturePaths.PackageDependencyAppDllPath);
        var contextType = target.Assembly.GetType("PackageDependencyApp.PackageDependencyDbContext", throwOnError: true)!;
        var jsonMethod = contextType.GetMethod("DescribeAsJson")!;
        Assert.Equal("Newtonsoft.Json", jsonMethod.ReturnType.Assembly.GetName().Name);

        var dependencyName = target.LoadedAssemblyPaths
            .Select(Path.GetFileNameWithoutExtension)
            .FirstOrDefault(name => name == "Newtonsoft.Json");
        Assert.NotNull(dependencyName);
        Assert.DoesNotContain(dependencyName, SharedFrameworkAssemblyNames.Value, StringComparer.OrdinalIgnoreCase);

        // A fresh CompiledQueryLoadContext has not resolved Newtonsoft.Json itself yet, so Load()
        // must go through the "known by path, resolve via the target context, then reuse" branch
        // rather than the "already loaded in the target context" fast path exercised above.
        var loadContext = CreateContext(target);
        var resolved = InvokeLoad(loadContext, new AssemblyName(dependencyName!));

        Assert.NotNull(resolved);
        Assert.Contains(target.Context.Assemblies, assembly => ReferenceEquals(assembly, resolved));

        target.Unload();
    }

    [Fact]
    public void LoadCompiledAssembly_ValidPeBytes_LoadsIntoThisContext()
    {
        var target = LoadSampleApp();
        var loadContext = CreateContext(target);
        var peBytes = File.ReadAllBytes(typeof(CompiledQueryLoadContextTests).Assembly.Location);

        using var peStream = new MemoryStream(peBytes);
        var loaded = loadContext.LoadCompiledAssembly(peStream, pdbStream: null);

        Assert.Equal(typeof(CompiledQueryLoadContextTests).Assembly.GetName().Name, loaded.GetName().Name);
        Assert.Same(loadContext, System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(loaded));

        loadContext.Unload();
        target.Unload();
    }

    [Fact]
    public void Unload_AfterUse_DoesNotThrowAndContextBecomesCollectible()
    {
        var weakContextRef = LoadUseAndUnload();

        for (var attempt = 0; attempt < 10 && weakContextRef.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(50);
        }

        Assert.False(weakContextRef.IsAlive);
    }

    // Isolated in a non-inlined helper so no strong reference to the CompiledQueryLoadContext (or
    // any Assembly/Type it resolved) remains rooted on the calling test method's stack, which
    // would otherwise keep the collectible AssemblyLoadContext alive across the GC retry loop.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference LoadUseAndUnload()
    {
        var target = LoadSampleApp();
        var loadContext = CreateContext(target);

        _ = InvokeLoad(loadContext, new AssemblyName(target.Assembly.GetName().Name!));
        var weakContextRef = new WeakReference(loadContext, trackResurrection: true);

        loadContext.Unload();
        target.Unload();

        return weakContextRef;
    }
}
