using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.AssemblyLoading;

public sealed class AssemblyLoaderServiceTests
{
    [Fact]
    public void Load_ValidAssembly_ReturnsHandleWithExpectedAssembly()
    {
        var service = new AssemblyLoaderService();

        var handle = service.Load(FixturePaths.SampleAppDllPath);

        Assert.NotNull(handle);
        Assert.Equal("SampleApp", handle.Assembly.GetName().Name);
        Assert.Same(handle, service.Current);
    }

    [Fact]
    public void Load_PathOutsideAllowedRoots_ThrowsAssemblyLoadFailedException()
    {
        var restrictedRoot = Path.Combine(Path.GetTempPath(), $"allowed-root-{Guid.NewGuid():N}");
        var service = new AssemblyLoaderService(new AssemblyLoaderOptions { AllowedRoots = [restrictedRoot] });

        var ex = Assert.Throws<AssemblyLoadFailedException>(() => service.Load(FixturePaths.SampleAppDllPath));

        Assert.Contains("outside the configured allowed roots", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_PathInsideAllowedRoots_Succeeds()
    {
        var allowedRoot = Path.GetDirectoryName(FixturePaths.SampleAppDllPath)!;
        var service = new AssemblyLoaderService(new AssemblyLoaderOptions { AllowedRoots = [allowedRoot] });

        var handle = service.Load(FixturePaths.SampleAppDllPath);

        Assert.Equal("SampleApp", handle.Assembly.GetName().Name);
    }

    [Fact]
    public void Load_PathInsideAllowedRootsButDifferentCasing_BehaviorMatchesPlatformCaseSensitivity()
    {
        // Windows/macOS file systems are case-insensitive, so a root that differs from the actual
        // path only in casing should still be treated as containing it there. Linux file systems
        // are case-sensitive, so the same casing difference must NOT be treated as containment -
        // otherwise the AllowedRoots restriction on arbitrary DLL loading could be bypassed by an
        // attacker-controlled path that merely differs in case from an allowed root.
        var allowedRoot = Path.GetDirectoryName(FixturePaths.SampleAppDllPath)!;
        var differentlyCasedRoot = allowedRoot == allowedRoot.ToUpperInvariant()
            ? allowedRoot.ToLowerInvariant()
            : allowedRoot.ToUpperInvariant();
        var service = new AssemblyLoaderService(new AssemblyLoaderOptions { AllowedRoots = [differentlyCasedRoot] });

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            var handle = service.Load(FixturePaths.SampleAppDllPath);
            Assert.Equal("SampleApp", handle.Assembly.GetName().Name);
        }
        else
        {
            var ex = Assert.Throws<AssemblyLoadFailedException>(() => service.Load(FixturePaths.SampleAppDllPath));
            Assert.Contains("outside the configured allowed roots", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Load_MissingFile_ThrowsAssemblyLoadFailedExceptionWithActionableMessage()
    {
        var service = new AssemblyLoaderService();
        var missingPath = Path.Combine(AppContext.BaseDirectory, $"does-not-exist-{Guid.NewGuid():N}.dll");

        var ex = Assert.Throws<AssemblyLoadFailedException>(() => service.Load(missingPath));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(missingPath, ex.Message);
    }

    [Fact]
    public void Load_CalledTwice_ReplacesPreviousHandle()
    {
        var service = new AssemblyLoaderService();

        var first = service.Load(FixturePaths.SampleAppDllPath);
        var second = service.Load(FixturePaths.SampleAppDllPath);

        Assert.NotSame(first, second);
        Assert.Same(second, service.Current);
    }

    [Fact]
    public void Unload_ClearsCurrent()
    {
        var service = new AssemblyLoaderService();
        service.Load(FixturePaths.SampleAppDllPath);

        service.Unload();

        Assert.Null(service.Current);
    }

    [Fact]
    public void IsCurrentAssemblyStale_FalseImmediatelyAfterLoad()
    {
        var service = new AssemblyLoaderService();
        service.Load(FixturePaths.SampleAppDllPath);

        Assert.False(service.IsCurrentAssemblyStale());
    }

    [Fact]
    public void IsCurrentAssemblyStale_FalseBeforeAnyLoad()
    {
        var service = new AssemblyLoaderService();

        Assert.False(service.IsCurrentAssemblyStale());
    }
}
