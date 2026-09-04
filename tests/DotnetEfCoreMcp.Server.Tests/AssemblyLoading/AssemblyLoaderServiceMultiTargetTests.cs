using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.AssemblyLoading;

/// <summary>Exercises the named multi-target registry added by P2 #15: multiple distinct
/// assemblies loaded simultaneously under different names, name-collision reload-in-place,
/// unknown-name rejection, and default-target backward compatibility for callers that never pass a
/// `targetName`.</summary>
public sealed class AssemblyLoaderServiceMultiTargetTests
{
    [Fact]
    public void Load_WithoutTargetName_BehavesLikeSingleImplicitTarget()
    {
        var service = new AssemblyLoaderService();

        var handle = service.Load(FixturePaths.SampleAppDllPath);

        Assert.Same(handle, service.Current);
        Assert.Same(handle, service.Get());
        Assert.Single(service.ListTargets());
        Assert.True(service.ListTargets()[0].IsDefault);
    }

    [Fact]
    public void Load_WithDistinctTargetNames_KeepsBothLoadedAndIsolated()
    {
        var service = new AssemblyLoaderService();

        var first = service.Load(FixturePaths.SampleAppDllPath, "alpha");
        var second = service.Load(FixturePaths.NoContextAppDllPath, "beta");

        Assert.NotSame(first, second);
        Assert.Same(first, service.Get("alpha"));
        Assert.Same(second, service.Get("beta"));
        Assert.Equal("SampleApp", first.Assembly.GetName().Name);
        Assert.Equal("NoContextApp", second.Assembly.GetName().Name);

        var names = service.ListTargets().Select(t => t.Name).OrderBy(n => n).ToArray();
        Assert.Equal(["alpha", "beta"], names);
    }

    [Fact]
    public void Load_WithDistinctTargetNames_UsesSeparateAssemblyLoadContexts()
    {
        var service = new AssemblyLoaderService();

        var first = service.Load(FixturePaths.SampleAppDllPath, "alpha");
        var second = service.Load(FixturePaths.SampleAppDllPath, "beta");

        // Same source DLL loaded under two different names must still resolve to distinct CLR
        // Type identity, proving each target got its own isolated AssemblyLoadContext rather than
        // sharing one.
        Assert.NotSame(first.Assembly, second.Assembly);
        var firstType = first.Assembly.GetType("SampleApp.SampleAppDbContext");
        var secondType = second.Assembly.GetType("SampleApp.SampleAppDbContext");
        Assert.NotNull(firstType);
        Assert.NotNull(secondType);
        Assert.NotEqual(firstType, secondType);
    }

    [Fact]
    public void Load_SameTargetNameTwice_ReloadsInPlace_WithoutDisturbingOtherTargets()
    {
        var service = new AssemblyLoaderService();
        var untouched = service.Load(FixturePaths.NoContextAppDllPath, "untouched");

        var firstAlpha = service.Load(FixturePaths.SampleAppDllPath, "alpha");
        var secondAlpha = service.Load(FixturePaths.SampleAppDllPath, "alpha");

        Assert.NotSame(firstAlpha, secondAlpha);
        Assert.Same(secondAlpha, service.Get("alpha"));

        // Registering "alpha" again must not create a duplicate entry or touch "untouched".
        Assert.Equal(2, service.ListTargets().Count);
        Assert.Same(untouched, service.Get("untouched"));
    }

    [Fact]
    public void Get_UnknownTargetName_ThrowsWithoutEnumeratingOtherNames()
    {
        var service = new AssemblyLoaderService();
        service.Load(FixturePaths.SampleAppDllPath, "alpha");
        service.Load(FixturePaths.NoContextAppDllPath, "beta");

        var ex = Assert.Throws<UnknownAssemblyTargetException>(() => service.Get("does-not-exist"));

        Assert.Equal("does-not-exist", ex.RequestedName);
        Assert.DoesNotContain("alpha", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("beta", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectDefault_UnknownTargetName_Throws()
    {
        var service = new AssemblyLoaderService();
        service.Load(FixturePaths.SampleAppDllPath, "alpha");

        var ex = Assert.Throws<UnknownAssemblyTargetException>(() => service.SelectDefault("does-not-exist"));

        Assert.Equal("does-not-exist", ex.RequestedName);
    }

    [Fact]
    public void SelectDefault_ChangesWhichTargetResolvesWhenNameOmitted()
    {
        var service = new AssemblyLoaderService();
        service.Load(FixturePaths.SampleAppDllPath, "alpha");
        var beta = service.Load(FixturePaths.NoContextAppDllPath, "beta");

        service.SelectDefault("beta");

        Assert.Same(beta, service.Get());
        Assert.Same(beta, service.Current);
        Assert.Equal("beta", service.CurrentDefaultTargetName);

        // Other targets remain loaded and addressable by name; select_target only changes the
        // default, it never unloads anything.
        Assert.NotNull(service.Get("alpha"));
    }

    [Fact]
    public void ListTargets_ReportsExactlyOneEntryAsDefault()
    {
        var service = new AssemblyLoaderService();
        service.Load(FixturePaths.SampleAppDllPath, "alpha");
        service.Load(FixturePaths.NoContextAppDllPath, "beta");
        service.SelectDefault("beta");

        var targets = service.ListTargets();

        Assert.Equal(2, targets.Count);
        Assert.Single(targets, t => t.IsDefault);
        Assert.Equal("beta", targets.Single(t => t.IsDefault).Name);
    }

    [Fact]
    public void IsTargetStale_ForUnknownExplicitName_Throws()
    {
        var service = new AssemblyLoaderService();
        service.Load(FixturePaths.SampleAppDllPath, "alpha");

        Assert.Throws<UnknownAssemblyTargetException>(() => service.IsTargetStale("does-not-exist"));
    }

    [Fact]
    public void IsCurrentAssemblyStale_WhenNothingLoaded_ReturnsFalse()
    {
        var service = new AssemblyLoaderService();

        Assert.False(service.IsCurrentAssemblyStale());
    }
}
