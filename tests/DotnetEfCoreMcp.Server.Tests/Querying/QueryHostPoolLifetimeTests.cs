using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetEfCoreMcp.Server.Tests.Querying;

/// <summary>Covers the pool's hosted-service/disposal contract. The DI container registers the pool
/// both as a singleton and as a hosted service, so the host stops it and then disposes it, and the
/// container may dispose it more than once - every one of those sequences has to complete without
/// throwing, otherwise shutdown faults and buffered stdout is lost.</summary>
public sealed class QueryHostPoolLifetimeTests
{
    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var pool = CreatePool();
        await pool.StartAsync(CancellationToken.None);

        await pool.DisposeAsync();
        await pool.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_AfterDispose_DoesNotThrow()
    {
        var pool = CreatePool();
        await pool.StartAsync(CancellationToken.None);
        await pool.DisposeAsync();

        await pool.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_ThenDisposeAsync_CompletesCleanly()
    {
        var pool = CreatePool();
        await pool.StartAsync(CancellationToken.None);

        await pool.StopAsync(CancellationToken.None);
        await pool.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_ConcurrentWithDisposeAsync_CompletesCleanly()
    {
        var pool = CreatePool();
        await pool.StartAsync(CancellationToken.None);

        var stopTask = pool.StopAsync(CancellationToken.None);
        var disposeTask = pool.DisposeAsync().AsTask();

        await Task.WhenAll(stopTask, disposeTask);
    }

    private static QueryHostPool CreatePool()
    {
        var options = new QueryExecutionOptions
        {
            Mode = QueryExecutionMode.OutOfProcess,
            OutOfProcessHostPath = FixturePaths.QueryHostDllPath,
        };

        return new QueryHostPool(options, new OutOfProcessRoslynQueryExecutor(options), NullLogger<QueryHostPool>.Instance);
    }
}
