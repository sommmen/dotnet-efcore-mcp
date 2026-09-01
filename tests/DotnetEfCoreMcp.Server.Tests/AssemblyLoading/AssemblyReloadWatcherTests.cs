using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetEfCoreMcp.Server.Tests.AssemblyLoading;

/// <summary>Exercises <see cref="AssemblyReloadWatcher"/> against real temp-file copies of the
/// SampleApp fixture DLL rather than the shared fixture path directly, so these tests can freely
/// rewrite/touch the file without racing other tests or the fixture project's own pre-build
/// target.</summary>
public sealed class AssemblyReloadWatcherTests : IDisposable
{
    private readonly string _tempDirectory;

    public AssemblyReloadWatcherTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"reload-watcher-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a lingering handle (e.g. from a load context not yet fully
            // unloaded) shouldn't fail the test.
        }
    }

    [Fact]
    public async Task Watcher_DoesNothing_WhenNoAssemblyEverLoaded()
    {
        var loader = new AssemblyLoaderService();
        using var watcher = new AssemblyReloadWatcher(loader, new AssemblyLoaderOptions(), NullLogger<AssemblyReloadWatcher>.Instance);

        await watcher.StartAsync(CancellationToken.None);

        // Give it a moment to prove it stays idle (no watcher/timer created, no exception) rather
        // than asserting a negative instantly.
        await Task.Delay(100);

        Assert.Null(loader.Current);

        await watcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Watcher_Disabled_DoesNotReload_WhenFileChanges()
    {
        var dllPath = CopyFixtureDll();
        var loader = new AssemblyLoaderService();
        loader.Load(dllPath);
        var initialHandle = loader.Current;

        using var watcher = new AssemblyReloadWatcher(loader, new AssemblyLoaderOptions { AutoReloadEnabled = false }, NullLogger<AssemblyReloadWatcher>.Instance);
        await watcher.StartAsync(CancellationToken.None);

        TouchFile(dllPath);

        // No reload should occur, so Current should stay the exact same handle instance well past
        // the debounce window.
        await Task.Delay(800);
        Assert.Same(initialHandle, loader.Current);

        await watcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Watcher_ReloadsAutomatically_WhenLoadedAssemblyFileChanges()
    {
        var dllPath = CopyFixtureDll();
        var loader = new AssemblyLoaderService();
        loader.Load(dllPath);
        var initialHandle = loader.Current;

        using var watcher = new AssemblyReloadWatcher(loader, new AssemblyLoaderOptions(), NullLogger<AssemblyReloadWatcher>.Instance);
        await watcher.StartAsync(CancellationToken.None);

        TouchFile(dllPath);

        var reloaded = await WaitForConditionAsync(
            () => loader.Current is not null && !ReferenceEquals(loader.Current, initialHandle),
            timeout: TimeSpan.FromSeconds(10));

        Assert.True(reloaded, "Expected the watcher to automatically reload the assembly after the DLL changed on disk.");
        Assert.Equal("SampleApp", loader.Current!.Assembly.GetName().Name);

        await watcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Watcher_CoalescesRapidSuccessiveWrites_IntoASingleReload()
    {
        var dllPath = CopyFixtureDll();
        var loader = new AssemblyLoaderService();
        loader.Load(dllPath);

        var reloadCount = 0;
        loader.AssemblyLoaded += _ => Interlocked.Increment(ref reloadCount);

        using var watcher = new AssemblyReloadWatcher(loader, new AssemblyLoaderOptions(), NullLogger<AssemblyReloadWatcher>.Instance);
        await watcher.StartAsync(CancellationToken.None);

        // Reset the counter after the initial subscribe so we only count reloads triggered by the
        // watcher itself, then fire several rapid writes as MSBuild would (DLL + PDB, more than once).
        Interlocked.Exchange(ref reloadCount, 0);
        for (var i = 0; i < 5; i++)
        {
            TouchFile(dllPath);
            await Task.Delay(50);
        }

        var reloadedOnce = await WaitForConditionAsync(() => Volatile.Read(ref reloadCount) >= 1, timeout: TimeSpan.FromSeconds(10));
        Assert.True(reloadedOnce, "Expected at least one automatic reload after the rapid write burst.");

        // Give any further (unwanted) debounced reloads a chance to fire before asserting there was
        // only a single one for the whole burst.
        await Task.Delay(1000);
        Assert.Equal(1, Volatile.Read(ref reloadCount));

        await watcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Watcher_Retargets_WhenADifferentAssemblyIsLoaded()
    {
        var firstPath = CopyFixtureDll("first");
        var secondPath = CopyFixtureDll("second");
        var loader = new AssemblyLoaderService();
        loader.Load(firstPath);

        using var watcher = new AssemblyReloadWatcher(loader, new AssemblyLoaderOptions(), NullLogger<AssemblyReloadWatcher>.Instance);
        await watcher.StartAsync(CancellationToken.None);

        // Simulate a manual load_assembly call switching to a different target assembly.
        loader.Load(secondPath);
        var handleAfterSwitch = loader.Current;

        // Touching the now-stale first path must not trigger anything (nothing watches it anymore).
        TouchFile(firstPath);
        await Task.Delay(800);
        Assert.Same(handleAfterSwitch, loader.Current);

        // Touching the newly loaded second path should now trigger an automatic reload.
        TouchFile(secondPath);
        var reloaded = await WaitForConditionAsync(
            () => loader.Current is not null && !ReferenceEquals(loader.Current, handleAfterSwitch),
            timeout: TimeSpan.FromSeconds(10));

        Assert.True(reloaded, "Expected the watcher to re-target itself at the newly loaded assembly's file.");

        await watcher.StopAsync(CancellationToken.None);
    }

    private string CopyFixtureDll(string? suffix = null)
    {
        var fileName = suffix is null ? "SampleApp.dll" : $"SampleApp-{suffix}.dll";
        var destination = Path.Combine(_tempDirectory, fileName);
        File.Copy(FixturePaths.SampleAppDllPath, destination);
        return destination;
    }

    private static void TouchFile(string path)
    {
        // The target assembly is loaded from a stream, so the file is not locked by the running
        // context — mimic MSBuild by overwriting in place with the existing bytes (preserving
        // the file identity/size so the watcher's Changed event still fires reliably).
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes);
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }
}
