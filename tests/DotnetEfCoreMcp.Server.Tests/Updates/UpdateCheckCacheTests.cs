using DotnetEfCoreMcp.Server.Updates;

namespace DotnetEfCoreMcp.Server.Tests.Updates;

/// <summary>
/// A clock the test moves by hand, so freshness can be asserted without sleeping.
/// </summary>
/// <remarks>
/// Hand-rolled rather than pulling in <c>Microsoft.Extensions.TimeProvider.Testing</c>, which is
/// not otherwise used in this solution and would be a new dependency for a handful of assertions.
/// </remarks>
internal sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan amount) => _now = _now.Add(amount);
}

/// <summary>
/// Covers the on-disk throttle that keeps update checks to at most one per channel interval.
/// </summary>
/// <remarks>
/// Without this cache every server start would hit the network, and an MCP server starts far more
/// often than a CLI is invoked because clients launch one per session.
/// </remarks>
public class UpdateCheckCacheTests : IDisposable
{
    private const string Version = "0.1.32-preview";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "efcore-update-cache", Guid.NewGuid().ToString("n"));

    private readonly MutableTimeProvider _time =
        new(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    private UpdateCheckCache Create() =>
        new(Path.Combine(_root, "update-check.json"), _time);

    [Fact]
    public void Round_trips_a_result()
    {
        var cache = Create();
        cache.Write(Version, "0.1.39-preview");

        var entry = cache.Read();

        Assert.NotNull(entry);
        Assert.Equal(Version, entry.InstalledVersion);
        Assert.Equal("0.1.39-preview", entry.LatestVersion);
    }

    [Fact]
    public void Reports_a_missing_file_as_a_miss()
    {
        Assert.Null(Create().Read());
        Assert.False(Create().IsFresh(null, Version));
    }

    [Fact]
    public void Treats_a_corrupt_file_as_a_miss_rather_than_failing()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "update-check.json"), "{ this is not json");

        // A cosmetic feature must never be able to throw during startup.
        Assert.Null(Create().Read());
    }

    [Fact]
    public void Considers_a_recent_entry_fresh()
    {
        var cache = Create();
        cache.Write(Version, "0.1.39-preview");

        _time.Advance(TimeSpan.FromHours(23));

        Assert.True(cache.IsFresh(cache.Read(), Version));
    }

    [Fact]
    public void Considers_an_entry_stale_once_the_preview_interval_elapses()
    {
        var cache = Create();
        cache.Write(Version, "0.1.39-preview");

        _time.Advance(TimeSpan.FromHours(25));

        Assert.False(cache.IsFresh(cache.Read(), Version));
    }

    [Fact]
    public void Keeps_a_stable_entry_fresh_for_a_week()
    {
        var cache = Create();
        cache.Write("1.0.0", "1.1.0");

        _time.Advance(TimeSpan.FromDays(6));
        Assert.True(cache.IsFresh(cache.Read(), "1.0.0"));

        _time.Advance(TimeSpan.FromDays(2));
        Assert.False(cache.IsFresh(cache.Read(), "1.0.0"));
    }

    [Fact]
    public void Treats_an_entry_for_a_different_version_as_stale()
    {
        var cache = Create();
        cache.Write(Version, "0.1.39-preview");

        // The user upgraded, so the cached answer is about a version they no longer run.
        Assert.False(cache.IsFresh(cache.Read(), "0.1.39-preview"));
    }

    [Fact]
    public void Treats_a_future_timestamp_as_stale()
    {
        var cache = Create();
        cache.Write(Version, "0.1.39-preview");

        // Clock skew, or a cache file written by another machine. Trusting it would suppress
        // checks indefinitely.
        _time.Advance(TimeSpan.FromHours(-5));

        Assert.False(cache.IsFresh(cache.Read(), Version));
    }

    [Fact]
    public void Records_that_nothing_newer_was_found()
    {
        var cache = Create();
        cache.Write(Version, latestVersion: null);

        var entry = cache.Read();

        // "Checked, nothing newer" must be cached, or an up-to-date user would re-check endlessly.
        Assert.NotNull(entry);
        Assert.Null(entry.LatestVersion);
        Assert.True(cache.IsFresh(entry, Version));
    }

    [Fact]
    public void Creates_the_containing_directory_on_first_write()
    {
        Assert.False(Directory.Exists(_root));

        Create().Write(Version, "0.1.39-preview");

        Assert.True(File.Exists(Path.Combine(_root, "update-check.json")));
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
