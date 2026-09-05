using System.Diagnostics;
using System.Text.Json;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetEfCoreMcp.Server.Tests.Querying;

public sealed class OutOfProcessRoslynQueryExecutorTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();
    private readonly LoadedAssemblyHandle _handle;
    private readonly Type _contextType;

    public OutOfProcessRoslynQueryExecutorTests()
    {
        _handle = new AssemblyLoaderService().Load(FixturePaths.SampleAppDllPath);
        _contextType = DbContextScanner.FindDbContextTypes(_handle.Assembly).Descriptors.Single(d => d.Name == "SampleAppDbContext").ClrType;

        using var context = DbContextActivator.CreateInstance(_contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite);
        context.Database.EnsureCreated();
        var customerType = EntitySeeding.GetEntityClrType(context, "Customer");
        context.Add(EntitySeeding.CreateEntity(customerType, new Dictionary<string, object?> { ["Name"] = "Alice", ["Age"] = 30 }));
        context.Add(EntitySeeding.CreateEntity(customerType, new Dictionary<string, object?> { ["Name"] = "Bob", ["Age"] = 15 }));
        context.SaveChanges();
    }

    public void Dispose()
    {
        _handle.Unload();
        _db.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_SequenceQuery_MaterializesRowsInIsolatedHost()
    {
        var result = await CreateOneShotExecutor().ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Where(c => c.Age >= 18).Select(c => c.Name)" }, CancellationToken.None);

        Assert.False(result.IsScalar);
        Assert.Equal(1, result.RowCount);
        Assert.Equal("Alice", result.Rows.Single()["Value"]);
    }

    [Fact]
    public async Task ExecuteAsync_StatementQuery_ReturnsScalarFromIsolatedHost()
    {
        var result = await CreateOneShotExecutor().ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "var adults = Customers.Count(c => c.Age >= 18);\nreturn adults;" }, CancellationToken.None);

        Assert.True(result.IsScalar);
        Assert.Equal(1, result.Scalar);
    }

    [Fact]
    public async Task ExecuteAsync_MissingHost_ThrowsConfigurationError()
    {
        var executor = new OutOfProcessRoslynQueryExecutor(new QueryExecutionOptions
        {
            Mode = QueryExecutionMode.OutOfProcess,
            OutOfProcessHostPath = Path.Combine(AppContext.BaseDirectory, "missing-query-host.dll"),
        });

        var exception = await Assert.ThrowsAsync<QueryExecutionException>(() => executor.ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers" }, CancellationToken.None));

        Assert.Equal("The configured out-of-process query host was not found.", exception.Message);
    }

    [Fact]
    public async Task PooledMode_MatchesOneShotCorrectness()
    {
        var sequence = new QueryRequest { Query = "Customers.Where(c => c.Age >= 18).Select(c => new { c.Name, c.Age })" };
        var scalar = new QueryRequest { Query = "return Customers.Count(c => c.Age >= 18);" };

        var oneShotExecutor = CreateOneShotExecutor();
        var expectedSequence = await oneShotExecutor.ExecuteAsync(_handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite, sequence, CancellationToken.None);
        var expectedScalar = await oneShotExecutor.ExecuteAsync(_handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite, scalar, CancellationToken.None);

        await using var pool = await CreateStartedPoolAsync();
        var pooledExecutor = new PooledOutOfProcessRoslynQueryExecutor(pool);

        var actualSequence = await pooledExecutor.ExecuteAsync(_handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite, sequence, CancellationToken.None);
        var actualScalar = await pooledExecutor.ExecuteAsync(_handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite, scalar, CancellationToken.None);

        AssertEquivalent(expectedSequence, actualSequence);
        AssertEquivalent(expectedScalar, actualScalar);
    }

    [Fact]
    public async Task PooledMode_WarmCall_IsMeaningfullyFasterThanFirstCall()
    {
        await using var pool = await CreateStartedPoolAsync();
        var executor = new PooledOutOfProcessRoslynQueryExecutor(pool);
        var request = new QueryRequest { Query = "Customers.Where(c => c.Age >= 18).Select(c => new { c.Name, c.Age })" };

        var first = await MeasureAsync(() => executor.ExecuteAsync(_handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite, request, CancellationToken.None));
        var second = await MeasureAsync(() => executor.ExecuteAsync(_handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite, request, CancellationToken.None));
        var third = await MeasureAsync(() => executor.ExecuteAsync(_handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite, request, CancellationToken.None));

        var warm = second < third ? second : third;
        Assert.True(warm < first / 2, $"Expected a warm pooled call to be < half of the first call. First={first.TotalMilliseconds:n0}ms Warm={warm.TotalMilliseconds:n0}ms");
    }

    [Fact]
    public async Task PooledMode_Timeout_KillsWorkerAndNextQueryCreatesReplacement()
    {
        var options = CreatePooledOptions(cancellationMargin: TimeSpan.FromSeconds(1));

        await using var pool = await CreateStartedPoolAsync(options);

        var firstWorkerPid = await ExecuteProcessIdQueryAsync(pool, _handle, _contextType, commandTimeoutSeconds: 30);

        var timeout = await Assert.ThrowsAsync<QueryExecutionException>(() => pool.ExecuteAsync(
            _handle,
            _contextType,
            _db.ToRegistryEntry(commandTimeoutSeconds: 1),
            DatabaseProvider.Sqlite,
            new QueryRequest { Query = "while (true)\n{\n}\n" },
            CancellationToken.None));

        Assert.Equal("Query timed out after 1s.", timeout.Message);
        Assert.Equal(0, (await pool.GetSnapshotAsync()).TotalWorkers);

        var replacementWorkerPid = await ExecuteProcessIdQueryAsync(pool, _handle, _contextType, commandTimeoutSeconds: 30);
        Assert.NotEqual(firstWorkerPid, replacementWorkerPid);
    }

    [Fact]
    public async Task PooledMode_RecyclesWorker_AfterConfiguredQueryLimit()
    {
        var options = CreatePooledOptions(poolMaxQueriesPerWorker: 2, poolMaxWorkersPerTarget: 1);

        await using var pool = await CreateStartedPoolAsync(options);

        var firstPid = await ExecuteProcessIdQueryAsync(pool, _handle, _contextType);
        var secondPid = await ExecuteProcessIdQueryAsync(pool, _handle, _contextType);

        Assert.Equal(firstPid, secondPid);
        Assert.Equal(0, (await pool.GetSnapshotAsync()).TotalWorkers);

        var thirdPid = await ExecuteProcessIdQueryAsync(pool, _handle, _contextType);
        Assert.NotEqual(firstPid, thirdPid);
    }

    [Fact]
    public async Task PooledMode_UsesDistinctSubPools_ForDifferentAssemblyPaths()
    {
        using var copiedFixture = CopySampleAppBuild();
        var otherHandle = new AssemblyLoaderService().Load(copiedFixture.DllPath);
        try
        {
            var otherContextType = DbContextScanner.FindDbContextTypes(otherHandle.Assembly).Descriptors.Single(d => d.Name == "SampleAppDbContext").ClrType;

            var options = CreatePooledOptions(poolMaxWorkersPerTarget: 1, poolMaxTotalWorkers: 2);

            await using var pool = await CreateStartedPoolAsync(options);

            var firstPid = await ExecuteProcessIdQueryAsync(pool, _handle, _contextType);
            var secondPid = await ExecuteProcessIdQueryAsync(pool, otherHandle, otherContextType);
            var firstPidAgain = await ExecuteProcessIdQueryAsync(pool, _handle, _contextType);
            var secondPidAgain = await ExecuteProcessIdQueryAsync(pool, otherHandle, otherContextType);

            Assert.Equal(firstPid, firstPidAgain);
            Assert.Equal(secondPid, secondPidAgain);
            Assert.NotEqual(firstPid, secondPid);

            var snapshot = await pool.GetSnapshotAsync();
            Assert.Equal(2, snapshot.TotalWorkers);
            Assert.Equal(2, snapshot.Workers.Select(static w => w.TargetAssemblyPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            otherHandle.Unload();
        }
    }

    [Fact]
    public async Task PooledMode_GlobalCap_OverflowsToOneShotFallback()
    {
        using var copiedFixture = CopySampleAppBuild();
        var otherHandle = new AssemblyLoaderService().Load(copiedFixture.DllPath);
        try
        {
            var otherContextType = DbContextScanner.FindDbContextTypes(otherHandle.Assembly).Descriptors.Single(d => d.Name == "SampleAppDbContext").ClrType;

            var options = CreatePooledOptions(poolMaxWorkersPerTarget: 1, poolMaxTotalWorkers: 1);

            await using var pool = await CreateStartedPoolAsync(options);

            await ExecuteProcessIdQueryAsync(pool, _handle, _contextType);
            await ExecuteProcessIdQueryAsync(pool, otherHandle, otherContextType);

            var snapshot = await pool.GetSnapshotAsync();
            Assert.True(snapshot.TotalWorkers <= 1);
            Assert.Equal(1, snapshot.FallbackExecutions);
        }
        finally
        {
            otherHandle.Unload();
        }
    }

    [Fact]
    public async Task PersistentWorker_SelfTerminates_AfterIdleTimeout()
    {
        var hostConfiguration = OutOfProcessRoslynQueryExecutor.ResolveHostConfiguration(CreatePooledOptions(), _handle);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                ArgumentList =
                {
                    "exec",
                    "--runtimeconfig", hostConfiguration.RuntimeConfigPath,
                    "--depsfile", hostConfiguration.HostDepsFilePath,
                    hostConfiguration.HostPath,
                    "--persistent",
                    "--idle-timeout-seconds", "1",
                },
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        process.Start();
        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(6));

        Assert.True(process.HasExited);
        Assert.Equal(0, process.ExitCode);
    }

    private static OutOfProcessRoslynQueryExecutor CreateOneShotExecutor() => new(CreateOneShotOptions());

    private static QueryExecutionOptions CreateOneShotOptions() => new()
    {
        Mode = QueryExecutionMode.OutOfProcess,
        OutOfProcessHostPath = FixturePaths.QueryHostDllPath,
    };

    private static QueryExecutionOptions CreatePooledOptions(
        int poolMaxWorkersPerTarget = 2,
        int poolMaxTotalWorkers = 8,
        int poolMaxQueriesPerWorker = 50,
        int poolIdleTimeoutSeconds = 300,
        TimeSpan? cancellationMargin = null) => new()
    {
        Mode = QueryExecutionMode.Pooled,
        OutOfProcessHostPath = FixturePaths.QueryHostDllPath,
        PoolMaxWorkersPerTarget = poolMaxWorkersPerTarget,
        PoolMaxTotalWorkers = poolMaxTotalWorkers,
        PoolMaxQueriesPerWorker = poolMaxQueriesPerWorker,
        PoolIdleTimeoutSeconds = poolIdleTimeoutSeconds,
        CancellationMargin = cancellationMargin ?? TimeSpan.FromSeconds(1),
    };

    private static async Task<QueryHostPool> CreateStartedPoolAsync(QueryExecutionOptions? options = null)
    {
        var effectiveOptions = options ?? CreatePooledOptions();
        var pool = new QueryHostPool(effectiveOptions, new OutOfProcessRoslynQueryExecutor(effectiveOptions), NullLogger<QueryHostPool>.Instance);
        await pool.StartAsync(CancellationToken.None);
        return pool;
    }

    private async Task<int> ExecuteProcessIdQueryAsync(QueryHostPool pool, LoadedAssemblyHandle handle, Type contextType, int commandTimeoutSeconds = 30)
    {
        var result = await pool.ExecuteAsync(
            handle,
            contextType,
            _db.ToRegistryEntry(commandTimeoutSeconds: commandTimeoutSeconds),
            DatabaseProvider.Sqlite,
            new QueryRequest { Query = "return global::System.Environment.ProcessId;" },
            CancellationToken.None);

        return Assert.IsType<int>(result.Scalar);
    }

    private static void AssertEquivalent(QueryResult expected, QueryResult actual)
    {
        Assert.Equal(expected.Entity, actual.Entity);
        Assert.Equal(expected.RowCount, actual.RowCount);
        Assert.Equal(expected.EffectiveTake, actual.EffectiveTake);
        Assert.Equal(expected.HasMoreRows, actual.HasMoreRows);
        Assert.Equal(expected.IsScalar, actual.IsScalar);
        Assert.Equal(JsonSerializer.Serialize(expected.Scalar), JsonSerializer.Serialize(actual.Scalar));
        Assert.Equal(JsonSerializer.Serialize(expected.Rows), JsonSerializer.Serialize(actual.Rows));
    }

    private static async Task<TimeSpan> MeasureAsync(Func<Task> action)
    {
        var stopwatch = Stopwatch.StartNew();
        await action();
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private static CopiedFixture CopySampleAppBuild()
    {
        var sourceDirectory = Path.GetDirectoryName(FixturePaths.SampleAppDllPath)!;
        var targetDirectory = Path.Combine(AppContext.BaseDirectory, "CopiedFixtures", Guid.NewGuid().ToString("N"));
        CopyDirectory(sourceDirectory, targetDirectory);
        return new CopiedFixture(targetDirectory, Path.Combine(targetDirectory, Path.GetFileName(FixturePaths.SampleAppDllPath)));
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, directory)));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            File.Copy(file, Path.Combine(targetDirectory, relativePath), overwrite: true);
        }
    }

    private sealed class CopiedFixture(string directoryPath, string dllPath) : IDisposable
    {
        public string DllPath { get; } = dllPath;

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(directoryPath))
                    Directory.Delete(directoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
