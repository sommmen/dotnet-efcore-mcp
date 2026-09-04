using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetEfCoreMcp.Server.Tests.AssemblyLoading;

/// <summary>Exercises <see cref="AssemblyReloadWatcher"/>'s per-target isolation added by P2 #15:
/// each named target gets its own file watcher, so a change to one target's assembly reloads only
/// that target and leaves the others untouched.</summary>
public sealed class AssemblyReloadWatcherMultiTargetTests : IDisposable
{
    private readonly string _tempDirectory;

    public AssemblyReloadWatcherMultiTargetTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"reload-watcher-multitarget-test-{Guid.NewGuid():N}");
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
    public async Task Watcher_ReloadsOnlyTheChangedTarget_LeavingOtherTargetsUntouched()
    {
        var alphaPath = CopyFixtureDll("alpha");
        var betaPath = CopyFixtureDll("beta");
        var loader = new AssemblyLoaderService();
        loader.Load(alphaPath, "alpha");
        loader.Load(betaPath, "beta");

        var initialAlpha = loader.Get("alpha");
        var initialBeta = loader.Get("beta");

        using var watcher = new AssemblyReloadWatcher(loader, new AssemblyLoaderOptions(), NullLogger<AssemblyReloadWatcher>.Instance);
        await watcher.StartAsync(CancellationToken.None);

        TouchFile(alphaPath);

        var alphaReloaded = await WaitForConditionAsync(
            () => loader.Get("alpha") is not null && !ReferenceEquals(loader.Get("alpha"), initialAlpha),
            timeout: TimeSpan.FromSeconds(10));

        Assert.True(alphaReloaded, "Expected touching alpha's file to reload only the alpha target.");
        Assert.Same(initialBeta, loader.Get("beta"));

        await watcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Watcher_HonorsPerTargetAutoReloadOverride_WhenServerWideDefaultIsEnabled()
    {
        var alphaPath = CopyFixtureDll("alpha");
        var betaPath = CopyFixtureDll("beta");
        var loader = new AssemblyLoaderService();
        loader.Load(alphaPath, "alpha", new AssemblyTargetOptions { Path = alphaPath, AutoReloadEnabled = false });
        loader.Load(betaPath, "beta");

        var initialAlpha = loader.Get("alpha");
        var initialBeta = loader.Get("beta");

        using var watcher = new AssemblyReloadWatcher(loader, new AssemblyLoaderOptions(), NullLogger<AssemblyReloadWatcher>.Instance);
        await watcher.StartAsync(CancellationToken.None);

        TouchFile(alphaPath);
        TouchFile(betaPath);

        var betaReloaded = await WaitForConditionAsync(
            () => loader.Get("beta") is not null && !ReferenceEquals(loader.Get("beta"), initialBeta),
            timeout: TimeSpan.FromSeconds(10));

        Assert.True(betaReloaded, "Expected beta (auto-reload not overridden) to reload automatically.");
        Assert.Same(initialAlpha, loader.Get("alpha"));

        await watcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Watcher_HonorsPerTargetAutoReloadOverride_WhenServerWideDefaultIsDisabled()
    {
        var alphaPath = CopyFixtureDll("alpha");
        var betaPath = CopyFixtureDll("beta");
        var loader = new AssemblyLoaderService();
        loader.Load(alphaPath, "alpha", new AssemblyTargetOptions { Path = alphaPath, AutoReloadEnabled = true });
        loader.Load(betaPath, "beta");

        var initialAlpha = loader.Get("alpha");
        var initialBeta = loader.Get("beta");

        using var watcher = new AssemblyReloadWatcher(loader, new AssemblyLoaderOptions { AutoReloadEnabled = false }, NullLogger<AssemblyReloadWatcher>.Instance);
        await watcher.StartAsync(CancellationToken.None);

        TouchFile(alphaPath);

        var alphaReloaded = await WaitForConditionAsync(
            () => loader.Get("alpha") is not null && !ReferenceEquals(loader.Get("alpha"), initialAlpha),
            timeout: TimeSpan.FromSeconds(10));

        Assert.True(alphaReloaded, "Expected alpha (auto-reload explicitly overridden to true) to reload despite the server-wide default being disabled.");

        TouchFile(betaPath);
        await Task.Delay(800);
        Assert.Same(initialBeta, loader.Get("beta"));

        await watcher.StopAsync(CancellationToken.None);
    }

    private string CopyFixtureDll(string suffix)
    {
        var fileName = $"SampleApp-{suffix}.dll";
        var destination = Path.Combine(_tempDirectory, fileName);
        File.Copy(FixturePaths.SampleAppDllPath, destination);
        return destination;
    }

    private static void TouchFile(string path)
    {
        // Rewrite the file's own contents (rather than just poking its timestamp) so its
        // last-write-time reliably advances regardless of filesystem timestamp resolution (and so
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
