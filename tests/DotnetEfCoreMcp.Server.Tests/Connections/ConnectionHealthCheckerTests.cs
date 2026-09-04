using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace DotnetEfCoreMcp.Server.Tests.Connections;

public sealed class ConnectionHealthCheckerTests
{
    [Fact]
    public async Task CheckAsync_AgainstARealReachableSqliteDatabase_ReturnsHealthy()
    {
        using var database = new SqliteTestDatabase();
        var optionsBuilder = new DbContextOptionsBuilder<ProbeDbContext>().UseSqlite(database.ConnectionString);
        using (var setupContext = new ProbeDbContext(optionsBuilder.Options))
        {
            // SQLite's CanConnectAsync opens the database file in read-only mode, which fails with
            // "unable to open database file" when the file doesn't exist yet - so the database file
            // must exist first, just as it would for a real target project's already-deployed database.
            setupContext.Database.EnsureCreated();
        }

        using var context = new ProbeDbContext(optionsBuilder.Options);

        var status = await ConnectionHealthChecker.CheckAsync(context, commandTimeoutSeconds: 30, cancellationMargin: TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(ConnectionHealthStatus.Healthy, status);
    }

    [Fact]
    public async Task CheckAsync_WhenProbeReturnsFalse_ReturnsFailed()
    {
        var status = await ConnectionHealthChecker.CheckAsync(
            probe: _ => Task.FromResult(false),
            commandTimeoutSeconds: 30,
            cancellationMargin: TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(ConnectionHealthStatus.Failed, status);
    }

    [Fact]
    public async Task CheckAsync_WhenProbeThrows_ReturnsFailedWithoutPropagatingTheException()
    {
        var status = await ConnectionHealthChecker.CheckAsync(
            probe: _ => throw new InvalidOperationException("provider-specific failure with a secret-host in it"),
            commandTimeoutSeconds: 30,
            cancellationMargin: TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(ConnectionHealthStatus.Failed, status);
    }

    [Fact]
    public async Task CheckAsync_WhenProbeNeverCompletesWithinTheConfiguredTimeout_ReturnsTimedOut()
    {
        var status = await ConnectionHealthChecker.CheckAsync(
            probe: async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return true;
            },
            commandTimeoutSeconds: 0,
            cancellationMargin: TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        Assert.Equal(ConnectionHealthStatus.TimedOut, status);
    }

    [Fact]
    public async Task CheckAsync_WhenCallerCancelsBeforeTheProbeCompletes_PropagatesCancellationRatherThanAStatus()
    {
        using var cancellationSource = new CancellationTokenSource();

        var checkTask = ConnectionHealthChecker.CheckAsync(
            probe: async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return true;
            },
            commandTimeoutSeconds: 30,
            cancellationMargin: TimeSpan.FromSeconds(30),
            cancellationSource.Token);

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checkTask);
    }

    [Fact]
    public async Task CheckAsync_WhenCallerTokenIsAlreadyCancelled_PropagatesCancellationImmediately()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConnectionHealthChecker.CheckAsync(
            probe: ct =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(true);
            },
            commandTimeoutSeconds: 30,
            cancellationMargin: TimeSpan.FromSeconds(30),
            cancellationSource.Token));
    }

    private sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : DbContext(options);
}
