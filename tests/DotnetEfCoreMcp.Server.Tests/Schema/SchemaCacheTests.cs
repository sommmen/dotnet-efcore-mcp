using DotnetEfCoreMcp.Server.Schema;

namespace DotnetEfCoreMcp.Server.Tests.Schema;

/// <summary>Covers <see cref="SchemaCache"/>'s cache-key behavior: it must key on both the DbContext
/// CLR type and the connection name, so two registered connections sharing a DbContext type (e.g.
/// different providers/databases) never reuse each other's built schema.</summary>
public sealed class SchemaCacheTests
{
    [Fact]
    public void GetOrBuild_WithSameTypeButDifferentConnectionNames_BuildsAndCachesSeparately()
    {
        var cache = new SchemaCache();
        var buildCount = 0;

        var forAlpha = cache.GetOrBuild(typeof(SchemaCacheTests), "Alpha", () =>
        {
            buildCount++;
            return new SchemaDto("Alpha", []);
        });
        var forBeta = cache.GetOrBuild(typeof(SchemaCacheTests), "Beta", () =>
        {
            buildCount++;
            return new SchemaDto("Beta", []);
        });

        Assert.Equal(2, buildCount);
        Assert.Equal("Alpha", forAlpha.ContextName);
        Assert.Equal("Beta", forBeta.ContextName);
    }

    [Fact]
    public void GetOrBuild_WithSameTypeAndSameConnectionName_BuildsOnlyOnce()
    {
        var cache = new SchemaCache();
        var buildCount = 0;

        var first = cache.GetOrBuild(typeof(SchemaCacheTests), "Alpha", () =>
        {
            buildCount++;
            return new SchemaDto("Alpha", []);
        });
        var second = cache.GetOrBuild(typeof(SchemaCacheTests), "Alpha", () =>
        {
            buildCount++;
            return new SchemaDto("Alpha", []);
        });

        Assert.Equal(1, buildCount);
        Assert.Same(first, second);
    }

    [Fact]
    public void TryGet_WithDifferentConnectionNameThanWasCached_ReturnsFalse()
    {
        var cache = new SchemaCache();
        cache.GetOrBuild(typeof(SchemaCacheTests), "Alpha", () => new SchemaDto("Alpha", []));

        var found = cache.TryGet(typeof(SchemaCacheTests), "Beta", out var schema);

        Assert.False(found);
        Assert.Null(schema);
    }

    [Fact]
    public void TryGet_WithSameConnectionNameAsWasCached_ReturnsTrue()
    {
        var cache = new SchemaCache();
        cache.GetOrBuild(typeof(SchemaCacheTests), "Alpha", () => new SchemaDto("Alpha", []));

        var found = cache.TryGet(typeof(SchemaCacheTests), "Alpha", out var schema);

        Assert.True(found);
        Assert.NotNull(schema);
        Assert.Equal("Alpha", schema!.ContextName);
    }

    [Fact]
    public async Task GetOrBuild_WithConcurrentCallsForSameKey_BuildsExactlyOnce()
    {
        var cache = new SchemaCache();
        var buildCount = 0;
        var ready = new ManualResetEventSlim(false);

        SchemaDto Factory()
        {
            Interlocked.Increment(ref buildCount);
            // Give every racing caller a chance to reach the cache before the first build
            // completes, so a naive `ConcurrentDictionary.GetOrAdd` factory (which may run more
            // than once under a race) would be exposed by this test.
            ready.Wait(TimeSpan.FromSeconds(5));
            return new SchemaDto("Alpha", []);
        }

        var starter = Task.Run(() =>
        {
            var result = cache.GetOrBuild(typeof(SchemaCacheTests), "Alpha", Factory);
            return result;
        });

        // Wait until the factory has started running before launching the racing callers.
        SpinWait.SpinUntil(() => Volatile.Read(ref buildCount) > 0, TimeSpan.FromSeconds(5));

        var racers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => cache.GetOrBuild(typeof(SchemaCacheTests), "Alpha", Factory)))
            .ToArray();

        ready.Set();

        var starterResult = await starter;
        var racerResults = await Task.WhenAll(racers);

        Assert.Equal(1, buildCount);
        Assert.All(racerResults, r => Assert.Same(starterResult, r));
    }
}
