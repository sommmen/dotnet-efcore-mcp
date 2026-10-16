using DotnetEfCoreMcp.Server.Updates;

namespace DotnetEfCoreMcp.Server.Tests.Updates;

/// <summary>
/// Covers the update checker end to end, with the feed stubbed out.
/// </summary>
/// <remarks>
/// The behavior these tests pin down hardest is silence: an update check is cosmetic, so no
/// failure of it - offline, slow, throwing, gated off - may ever surface to a caller.
/// </remarks>
public class UpdateCheckerTests : IDisposable
{
    private static readonly string GlobalToolPath = Path.Combine(
        "Users", "dev", ".dotnet", "tools", ".store", "dotnetefcoremcp.server", "0.1.32-preview");

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "efcore-update-checker", Guid.NewGuid().ToString("n"));

    /// <summary>A feed that returns a fixed list, or fails in a chosen way.</summary>
    private sealed class StubLookup(IReadOnlyList<string>? versions, Exception? failure = null)
        : INuGetVersionLookup
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken)
        {
            CallCount++;

            return failure is not null
                ? Task.FromException<IReadOnlyList<string>>(failure)
                : Task.FromResult(versions ?? []);
        }
    }

    private UpdateChecker Create(
        INuGetVersionLookup lookup,
        string? installedVersion = "0.1.32-preview",
        string? baseDirectory = null,
        IDictionary<string, string?>? environment = null)
    {
        var variables = environment ?? new Dictionary<string, string?>();
        string? Read(string name) => variables.TryGetValue(name, out var value) ? value : null;

        return new UpdateChecker(
            lookup,
            new UpdateCheckCache(Path.Combine(_root, "update-check.json"), TimeProvider.System),
            new UpdateCheckGate(enabledByConfiguration: true, Read),
            new InstallMethodDetector(baseDirectory ?? GlobalToolPath, Path.Combine(_root, "cwd"), Read),
            installedVersion);
    }

    [Fact]
    public async Task Reports_an_available_update_with_its_upgrade_command()
    {
        var result = await Create(new StubLookup(["0.1.32-preview", "0.1.39-preview"])).CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Checked);
        Assert.True(result.UpdateAvailable);
        Assert.Equal("0.1.39-preview", result.LatestVersion);
        Assert.Equal("dotnet tool update --global DotnetEfCoreMcp.Server --prerelease", result.UpgradeCommand);
        Assert.Contains("0.1.32-preview -> 0.1.39-preview", result.Notice);
    }

    [Fact]
    public async Task Reports_no_update_when_already_current()
    {
        var result = await Create(new StubLookup(["0.1.20-preview", "0.1.32-preview"])).CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Checked);
        Assert.False(result.UpdateAvailable);
        Assert.Null(result.Notice);
    }

    [Fact]
    public async Task Stays_silent_when_the_feed_is_unreachable()
    {
        var result = await Create(new StubLookup(versions: [])).CheckAsync(TestContext.Current.CancellationToken);

        // Indistinguishable from "already current" to a caller, which is correct: in neither case
        // is there anything useful to say.
        Assert.False(result.Checked);
        Assert.Null(result.Notice);
    }

    [Fact]
    public async Task Stays_silent_when_the_lookup_throws()
    {
        var result = await Create(new StubLookup(null, new InvalidOperationException("boom"))).CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Checked);
        Assert.Null(result.Notice);
    }

    [Fact]
    public async Task Does_not_call_the_feed_when_gated_off()
    {
        var lookup = new StubLookup(["0.1.39-preview"]);

        var result = await Create(
                lookup,
                environment: new Dictionary<string, string?>
                {
                    [UpdateCheckGate.DisableVariable] = "1",
                })
            .CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, lookup.CallCount);
        Assert.False(result.Checked);
    }

    [Fact]
    public async Task Does_not_call_the_feed_for_an_unknown_install_method()
    {
        var lookup = new StubLookup(["0.1.39-preview"]);

        var result = await Create(lookup, baseDirectory: @"C:\Repos\app\bin\Debug\net10.0").CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, lookup.CallCount);
        Assert.Equal(InstallMethod.Unknown, result.InstallMethod);
    }

    [Fact]
    public async Task Does_not_call_the_feed_for_a_dnx_launch()
    {
        var lookup = new StubLookup(["0.1.39-preview"]);

        // dnx refetches on every run, so there is nothing to tell the user.
        var result = await Create(
                lookup,
                baseDirectory: Path.Combine(
                    "Users", "dev", ".nuget", "packages", "dotnetefcoremcp.server", "0.1.32-preview"))
            .CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, lookup.CallCount);
        Assert.Equal(InstallMethod.Dnx, result.InstallMethod);
    }

    [Fact]
    public async Task Serves_a_second_check_from_the_cache()
    {
        var lookup = new StubLookup(["0.1.39-preview"]);
        var checker = Create(lookup);

        var first = await checker.CheckAsync(TestContext.Current.CancellationToken);
        var second = await checker.CheckAsync(TestContext.Current.CancellationToken);

        // One request, two answers. This is what keeps per-session server launches off the network.
        Assert.Equal(1, lookup.CallCount);
        Assert.Equal(first.LatestVersion, second.LatestVersion);
        Assert.True(second.UpdateAvailable);
    }

    [Fact]
    public async Task Does_not_cache_a_failed_lookup()
    {
        var failing = new StubLookup(versions: []);
        var checker = Create(failing);

        await checker.CheckAsync(TestContext.Current.CancellationToken);
        await checker.CheckAsync(TestContext.Current.CancellationToken);

        // Caching a failure would suppress checks for a full day because the network blipped.
        Assert.Equal(2, failing.CallCount);
    }

    [Fact]
    public async Task Stays_silent_for_a_build_without_packaging_metadata()
    {
        var lookup = new StubLookup(["1.0.0"]);

        var result = await Create(lookup, installedVersion: null).CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, lookup.CallCount);
        Assert.False(result.Checked);
        Assert.Null(result.Notice);
    }

    [Fact]
    public async Task Never_offers_a_preview_to_a_stable_install()
    {
        var result = await Create(new StubLookup(["1.0.0", "1.1.0-preview"]), installedVersion: "1.0.0")
            .CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Checked);
        Assert.False(result.UpdateAvailable);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
