using System.Diagnostics;
using System.Text.Json;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Maintains a bounded, recycling pool of persistent out-of-process query hosts keyed by
/// target assembly path + last-write time, and falls back to the one-shot executor when the pool is
/// saturated.</summary>
public sealed class QueryHostPool(
    QueryExecutionOptions options,
    OutOfProcessRoslynQueryExecutor fallbackExecutor,
    ILogger<QueryHostPool> logger) : IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<QueryHostPoolKey, List<PersistentQueryHostWorker>> _workers = [];
    private readonly Dictionary<QueryHostPoolKey, int> _pendingCreates = [];
    private readonly TimeSpan _idleTimeout = TimeSpan.FromSeconds(Math.Max(1, options.PoolIdleTimeoutSeconds));
    private CancellationTokenSource? _maintenanceCts;
    private Task? _maintenanceTask;
    private bool _stopping;
    private int _fallbackExecutions;
    private int _totalPendingCreates;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _maintenanceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _maintenanceTask = RunMaintenanceLoopAsync(_maintenanceCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        List<PersistentQueryHostWorker> workers;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping)
                return;

            _stopping = true;
            _maintenanceCts?.Cancel();
            workers = _workers.Values.SelectMany(static x => x).ToList();
            _workers.Clear();
            _pendingCreates.Clear();
            _totalPendingCreates = 0;
        }
        finally
        {
            _gate.Release();
        }

        if (_maintenanceTask is not null)
        {
            try
            {
                await _maintenanceTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        await RetireWorkersAsync(workers, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _maintenanceCts?.Dispose();
        _gate.Dispose();
    }

    public async Task<QueryResult> ExecuteAsync(
        LoadedAssemblyHandle target,
        Type contextType,
        ConnectionRegistryEntry entry,
        DatabaseProvider provider,
        QueryRequest request,
        CancellationToken cancellationToken)
    {
        var worker = await CheckoutWorkerAsync(target, cancellationToken).ConfigureAwait(false);
        if (worker is null)
        {
            Interlocked.Increment(ref _fallbackExecutions);
            return await fallbackExecutor.ExecuteAsync(target, contextType, entry, provider, request, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await worker.ExecuteAsync(contextType, entry, provider, request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await CheckInWorkerAsync(worker, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<QueryHostPoolSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var workers = _workers
                .OrderBy(static pair => pair.Key.TargetAssemblyPath, PathComparer)
                .ThenBy(static pair => pair.Key.TargetAssemblyLastWriteTimeUtc)
                .SelectMany(static pair => pair.Value)
                .Select(static worker => new QueryHostWorkerSnapshot(
                    worker.Key.TargetAssemblyPath,
                    worker.Key.TargetAssemblyLastWriteTimeUtc,
                    worker.ProcessId,
                    worker.QueriesServed,
                    worker.IsLeased,
                    worker.LastActivityUtc))
                .ToArray();

            return new QueryHostPoolSnapshot(
                workers.Length,
                workers.Count(static x => !x.IsLeased),
                Volatile.Read(ref _fallbackExecutions),
                workers);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PersistentQueryHostWorker?> CheckoutWorkerAsync(LoadedAssemblyHandle target, CancellationToken cancellationToken)
    {
        var key = CreateKey(target.AssemblyPath);
        List<PersistentQueryHostWorker> workersToRetire = [];
        PersistentQueryHostWorker? idleWorker = null;
        PersistentQueryHostWorker? evictedWorker = null;
        var createReserved = false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CollectExpiredIdleWorkers_NoLock(workersToRetire);

            if (TryCheckoutIdleWorker_NoLock(key, out idleWorker))
                createReserved = false;

            else if (CanCreateWorker_NoLock(key))
            {
                ReserveCreate_NoLock(key);
                createReserved = true;
            }
            else
            {
                evictedWorker = EvictLeastRecentlyUsedIdleWorker_NoLock(key);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (workersToRetire.Count != 0)
            await RetireWorkersAsync(workersToRetire, cancellationToken).ConfigureAwait(false);
        if (evictedWorker is not null)
            await RetireWorkerAsync(evictedWorker, cancellationToken).ConfigureAwait(false);
        if (idleWorker is not null)
            return idleWorker;

        if (!createReserved)
            return null;

        PersistentQueryHostWorker worker;
        try
        {
            worker = StartWorker(target, key);
        }
        catch
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ReleaseCreateReservation_NoLock(key);
            }
            finally
            {
                _gate.Release();
            }

            throw;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ReleaseCreateReservation_NoLock(key);
            if (_stopping)
            {
                worker.MarkForRetirement();
            }
            else
            {
                worker.IsLeased = true;
                GetWorkersForKey_NoLock(key).Add(worker);
                return worker;
            }
        }
        finally
        {
            _gate.Release();
        }

        await RetireWorkerAsync(worker, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async Task CheckInWorkerAsync(PersistentQueryHostWorker worker, CancellationToken cancellationToken)
    {
        var retireWorker = false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            worker.IsLeased = false;
            worker.LastActivityUtc = DateTimeOffset.UtcNow;

            if (_stopping ||
                !worker.LastCallCompletedCleanly ||
                worker.HasExited ||
                worker.ShouldRecycle(options.PoolMaxQueriesPerWorker))
            {
                retireWorker = RemoveWorker_NoLock(worker);
                worker.MarkForRetirement();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (retireWorker)
            await RetireWorkerAsync(worker, cancellationToken).ConfigureAwait(false);
    }

    private PersistentQueryHostWorker StartWorker(LoadedAssemblyHandle target, QueryHostPoolKey key)
    {
        var host = OutOfProcessRoslynQueryExecutor.ResolveHostConfiguration(options, target);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                ArgumentList =
                {
                    "exec",
                    "--runtimeconfig", host.RuntimeConfigPath,
                    "--depsfile", host.HostDepsFilePath,
                    host.HostPath,
                    "--persistent",
                    "--idle-timeout-seconds", options.PoolIdleTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        process.Start();
        logger.LogDebug("Started pooled query host worker {ProcessId} for {AssemblyPath}.", process.Id, key.TargetAssemblyPath);
        return new PersistentQueryHostWorker(key, process, options);
    }

    private async Task RunMaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(GetMaintenancePeriod());
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                List<PersistentQueryHostWorker> expiredWorkers = [];

                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    CollectExpiredIdleWorkers_NoLock(expiredWorkers);
                }
                finally
                {
                    _gate.Release();
                }

                if (expiredWorkers.Count != 0)
                    await RetireWorkersAsync(expiredWorkers, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
    }

    private TimeSpan GetMaintenancePeriod()
    {
        var seconds = Math.Clamp(options.PoolIdleTimeoutSeconds / 4, 1, 30);
        return TimeSpan.FromSeconds(seconds);
    }

    private void CollectExpiredIdleWorkers_NoLock(List<PersistentQueryHostWorker> expiredWorkers)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var list in _workers.Values)
        {
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var worker = list[i];
                if (worker.IsLeased)
                    continue;

                if (worker.HasExited || now - worker.LastActivityUtc >= _idleTimeout)
                {
                    list.RemoveAt(i);
                    worker.MarkForRetirement();
                    expiredWorkers.Add(worker);
                }
            }
        }

        RemoveEmptyWorkerLists_NoLock();
    }

    private bool TryCheckoutIdleWorker_NoLock(QueryHostPoolKey key, out PersistentQueryHostWorker? worker)
    {
        worker = null;
        if (!_workers.TryGetValue(key, out var list))
            return false;

        for (var i = list.Count - 1; i >= 0; i--)
        {
            var candidate = list[i];
            if (candidate.IsLeased)
                continue;

            if (candidate.HasExited)
            {
                list.RemoveAt(i);
                candidate.MarkForRetirement();
                continue;
            }

            candidate.IsLeased = true;
            candidate.LastActivityUtc = DateTimeOffset.UtcNow;
            worker = candidate;
            return true;
        }

        RemoveEmptyWorkerLists_NoLock();
        return false;
    }

    private bool CanCreateWorker_NoLock(QueryHostPoolKey key)
    {
        var workersForKey = _workers.TryGetValue(key, out var list) ? list.Count : 0;
        var pendingForKey = _pendingCreates.TryGetValue(key, out var pending) ? pending : 0;
        var totalWorkers = _workers.Values.Sum(static x => x.Count) + _totalPendingCreates;
        return workersForKey + pendingForKey < options.PoolMaxWorkersPerTarget &&
               totalWorkers < options.PoolMaxTotalWorkers;
    }

    private void ReserveCreate_NoLock(QueryHostPoolKey key)
    {
        _pendingCreates.TryGetValue(key, out var count);
        _pendingCreates[key] = count + 1;
        _totalPendingCreates++;
    }

    private void ReleaseCreateReservation_NoLock(QueryHostPoolKey key)
    {
        if (!_pendingCreates.TryGetValue(key, out var count))
            return;

        if (count <= 1)
            _pendingCreates.Remove(key);
        else
            _pendingCreates[key] = count - 1;

        _totalPendingCreates--;
    }

    private PersistentQueryHostWorker? EvictLeastRecentlyUsedIdleWorker_NoLock(QueryHostPoolKey requestedKey)
    {
        PersistentQueryHostWorker? selected = null;
        List<PersistentQueryHostWorker>? selectedList = null;

        foreach (var pair in _workers)
        {
            if (pair.Key.Equals(requestedKey))
                continue;

            foreach (var worker in pair.Value)
            {
                if (worker.IsLeased)
                    continue;

                if (selected is null || worker.LastActivityUtc < selected.LastActivityUtc)
                {
                    selected = worker;
                    selectedList = pair.Value;
                }
            }
        }

        if (selected is null || selectedList is null)
            return null;

        selectedList.Remove(selected);
        selected.MarkForRetirement();
        RemoveEmptyWorkerLists_NoLock();
        return selected;
    }

    private bool RemoveWorker_NoLock(PersistentQueryHostWorker worker)
    {
        if (!_workers.TryGetValue(worker.Key, out var list))
            return false;

        var removed = list.Remove(worker);
        RemoveEmptyWorkerLists_NoLock();
        return removed;
    }

    private List<PersistentQueryHostWorker> GetWorkersForKey_NoLock(QueryHostPoolKey key)
    {
        if (!_workers.TryGetValue(key, out var list))
        {
            list = [];
            _workers[key] = list;
        }

        return list;
    }

    private void RemoveEmptyWorkerLists_NoLock()
    {
        foreach (var key in _workers.Where(static pair => pair.Value.Count == 0).Select(static pair => pair.Key).ToArray())
            _workers.Remove(key);
    }

    private static QueryHostPoolKey CreateKey(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        if (OperatingSystem.IsWindows())
            fullPath = fullPath.ToUpperInvariant();

        return new QueryHostPoolKey(fullPath, File.GetLastWriteTimeUtc(fullPath));
    }

    private async Task RetireWorkersAsync(IEnumerable<PersistentQueryHostWorker> workers, CancellationToken cancellationToken)
    {
        foreach (var worker in workers)
            await RetireWorkerAsync(worker, cancellationToken).ConfigureAwait(false);
    }

    private async Task RetireWorkerAsync(PersistentQueryHostWorker worker, CancellationToken cancellationToken)
    {
        try
        {
            await worker.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            worker.Dispose();
        }
    }

    internal sealed record QueryHostPoolKey(string TargetAssemblyPath, DateTime TargetAssemblyLastWriteTimeUtc);

    internal sealed class PersistentQueryHostWorker(QueryHostPoolKey key, Process process, QueryExecutionOptions options) : IDisposable
    {
        private readonly Task<string> _stderrTask = process.StandardError.ReadToEndAsync();

        public QueryHostPoolKey Key { get; } = key;
        public Process Process { get; } = process;
        public bool IsLeased { get; set; }
        public int QueriesServed { get; private set; }
        public DateTimeOffset LastActivityUtc { get; set; } = DateTimeOffset.UtcNow;
        public bool LastCallCompletedCleanly { get; private set; }
        public int ProcessId => Process.Id;
        public bool HasExited => Process.HasExited;

        public async Task<QueryResult> ExecuteAsync(
            Type contextType,
            ConnectionRegistryEntry entry,
            DatabaseProvider provider,
            QueryRequest request,
            CancellationToken cancellationToken)
        {
            LastCallCompletedCleanly = false;

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(entry.CommandTimeoutSeconds) + options.CancellationMargin);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var payload = new OutOfProcessQueryRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                TargetAssemblyPath = Key.TargetAssemblyPath,
                ContextTypeName = contextType.FullName ?? contextType.Name,
                Connection = entry,
                Provider = provider,
                Query = request,
                Options = options,
            };

            try
            {
                if (Process.HasExited)
                    throw new QueryExecutionException("The pooled query host exited before it could accept the query.");

                await Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(payload, JsonOptions))
                    .WaitAsync(linkedCts.Token)
                    .ConfigureAwait(false);
                await Process.StandardInput.FlushAsync().WaitAsync(linkedCts.Token).ConfigureAwait(false);

                var responseLine = await Process.StandardOutput.ReadLineAsync()
                    .WaitAsync(linkedCts.Token)
                    .ConfigureAwait(false);
                if (responseLine is null)
                    throw new QueryExecutionException("The pooled query host closed its output before returning a response.");

                var response = JsonSerializer.Deserialize<OutOfProcessQueryResponse>(responseLine, JsonOptions);
                if (response is null || response.ProtocolVersion != OutOfProcessQueryRequest.CurrentProtocolVersion || response.RequestId != payload.RequestId)
                    throw new QueryExecutionException("The pooled query host returned an invalid response.");

                QueriesServed++;
                LastActivityUtc = DateTimeOffset.UtcNow;
                LastCallCompletedCleanly = true;

                if (!string.IsNullOrWhiteSpace(response.Error))
                    throw new QueryExecutionException(response.Error);
                if (response.Result is null)
                    throw new QueryExecutionException("The pooled query host did not return a result.");

                return OutOfProcessRoslynQueryExecutor.ToQueryResult(response.Result);
            }
            catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new QueryExecutionException($"Query timed out after {entry.CommandTimeoutSeconds}s.", ex);
            }
        }

        public bool ShouldRecycle(int maxQueriesPerWorker) => QueriesServed >= maxQueriesPerWorker;

        public void MarkForRetirement() => LastCallCompletedCleanly = false;

        public async Task ShutdownAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (Process.HasExited)
                    return;

                var payload = new OutOfProcessShutdownRequest();
                await Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(payload, JsonOptions))
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                await Process.StandardInput.FlushAsync().WaitAsync(cancellationToken).ConfigureAwait(false);

                using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                graceCts.CancelAfter(TimeSpan.FromSeconds(2));
                await Process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
            }
            catch
            {
                OutOfProcessRoslynQueryExecutor.KillIfRunning(Process);
                try
                {
                    await Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Caller is shutting down; best effort only.
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                    OutOfProcessRoslynQueryExecutor.KillIfRunning(Process);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            try
            {
                _stderrTask.GetAwaiter().GetResult();
            }
            catch
            {
                // Best effort only; the worker process may already have been torn down.
            }

            Process.Dispose();
        }
    }
}

internal sealed record QueryHostPoolSnapshot(
    int TotalWorkers,
    int IdleWorkers,
    int FallbackExecutions,
    IReadOnlyList<QueryHostWorkerSnapshot> Workers);

internal sealed record QueryHostWorkerSnapshot(
    string TargetAssemblyPath,
    DateTime TargetAssemblyLastWriteTimeUtc,
    int ProcessId,
    int QueriesServed,
    bool IsLeased,
    DateTimeOffset LastActivityUtc);
