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
