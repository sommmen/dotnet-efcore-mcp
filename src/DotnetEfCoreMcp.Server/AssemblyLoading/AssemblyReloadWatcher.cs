using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetEfCoreMcp.Server.AssemblyLoading;

/// <summary>Watches every currently loaded target assembly's DLL for on-disk changes (e.g. MSBuild
/// finishing a rebuild) and automatically re-invokes <see cref="AssemblyLoaderService.Load(string, string?)"/>
/// so the server keeps serving up-to-date schema/types without a manual `load_assembly` call, for
/// each named target independently. Idle for a given target until it has been loaded (manually, via
/// startup `TargetAssemblyPath`/`AssemblyLoader:Targets`, or via workspace auto-discovery); disabled
/// for a target when the server-wide <see cref="AssemblyLoaderOptions.AutoReloadEnabled"/> is
/// <c>false</c> and that target does not itself override it back on via
/// <see cref="AssemblyTargetOptions.AutoReloadEnabled"/>.
///
/// MSBuild typically writes a DLL (and its PDB) more than once in quick succession during a single
/// build, so file-change events are debounced for <see cref="DebounceDelay"/> before a reload is
/// attempted. The DLL can still be locked when the debounce window elapses (a build still in
/// progress), so a failed attempt is retried a bounded number of times with a short delay before
/// giving up and logging a warning - this reuses <see cref="AssemblyLoaderService.Load(string, string?)"/>'s
/// own file-lock probing rather than duplicating it. Any failure (locked file, bad IL mid-write,
/// etc.) leaves the previously loaded assembly serving requests; it is never treated as fatal.</summary>
public sealed class AssemblyReloadWatcher : IHostedService, IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(300);
    private const int MaxReloadAttempts = 5;

    private readonly AssemblyLoaderService _assemblyLoader;
    private readonly AssemblyLoaderOptions _options;
    private readonly ILogger<AssemblyReloadWatcher> _logger;
    private readonly object _gate = new();

    // Keyed by target name, since each named target now needs its own independently watched file,
    // debounce timer, and stopped/retargeted lifecycle rather than the single shared set of fields
    // this watcher had before multiple targets could be loaded at once.
    private readonly Dictionary<string, TargetWatchState> _states = new(StringComparer.Ordinal);
    private bool _stopped;

    public AssemblyReloadWatcher(AssemblyLoaderService assemblyLoader, AssemblyLoaderOptions options, ILogger<AssemblyReloadWatcher> logger)
    {
        _assemblyLoader = assemblyLoader;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _assemblyLoader.AssemblyLoaded += OnAssemblyLoaded;

        // Targets may already have been loaded (TargetAssemblyPath/AssemblyLoader:Targets/workspace
        // auto-discovery, or a manual load_assembly call) before this hosted service started - start
        // watching all of them now rather than waiting for the next Load() call to raise
        // AssemblyLoaded for each.
        foreach (var target in _assemblyLoader.ListTargets())
        {
            if (IsAutoReloadEnabledFor(target.AutoReloadEnabled))
            {
                Retarget(target.Name, target.Handle.AssemblyPath);
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _assemblyLoader.AssemblyLoaded -= OnAssemblyLoaded;

        lock (_gate)
        {
            _stopped = true;
            foreach (var state in _states.Values)
            {
                DisposeWatcherAndTimer(state);
            }

            _states.Clear();
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true;
            foreach (var state in _states.Values)
            {
                DisposeWatcherAndTimer(state);
            }

            _states.Clear();
        }
    }

    /// <summary>Whether auto-reload should be active for a target given its own override (if any),
    /// falling back to the server-wide <see cref="AssemblyLoaderOptions.AutoReloadEnabled"/>.</summary>
    private bool IsAutoReloadEnabledFor(bool? targetOverride) => targetOverride ?? _options.AutoReloadEnabled;

    private void OnAssemblyLoaded(AssemblyLoadedEventArgs args)
    {
        var targetOverride = _assemblyLoader.ListTargets()
            .FirstOrDefault(t => t.Name == args.TargetName)?.AutoReloadEnabled;

        if (!IsAutoReloadEnabledFor(targetOverride))
        {
            // Auto-reload disabled for this specific target (or server-wide with no per-target
            // override) - make sure any previously active watcher for it is torn down, e.g. if the
            // target was reloaded after AutoReloadEnabled was flipped off for it at runtime.
            lock (_gate)
            {
                if (_states.Remove(args.TargetName, out var state))
                {
                    DisposeWatcherAndTimer(state);
                }
            }

            return;
        }

        Retarget(args.TargetName, args.Handle.AssemblyPath);
    }

    /// <summary>(Re)points the target's <see cref="FileSystemWatcher"/> at
    /// <paramref name="assemblyPath"/>, tearing down any previous watcher for that target first. A
    /// no-op if already watching the same path for that target, which is the common case of our own
    /// automatic reload (or a repeated manual `load_assembly` of the same file) raising
    /// <see cref="AssemblyLoaderService.AssemblyLoaded"/> again.</summary>
    private void Retarget(string targetName, string assemblyPath)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            if (_states.TryGetValue(targetName, out var existing) &&
                string.Equals(existing.WatchedPath, assemblyPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (existing is not null)
            {
                DisposeWatcherAndTimer(existing);
                _states.Remove(targetName);
            }

            var directory = Path.GetDirectoryName(assemblyPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                // Shouldn't normally happen since Load() already required the file to exist, but
                // don't let a race (e.g. the containing directory removed) crash the process.
                _logger.LogDebug("Not watching '{AssemblyPath}' for changes: containing directory not found.", assemblyPath);
                return;
            }

            var state = new TargetWatchState(assemblyPath);
            _states[targetName] = state;

            try
            {
                var watcher = new FileSystemWatcher(directory, Path.GetFileName(assemblyPath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                };
                watcher.Changed += (_, _) => OnFileChanged(targetName);
                watcher.Created += (_, _) => OnFileChanged(targetName);
                watcher.Error += (_, e) => OnWatcherError(targetName, e);
                watcher.EnableRaisingEvents = true;
                state.Watcher = watcher;

                _logger.LogDebug("Watching '{AssemblyPath}' (target '{TargetName}') for changes to auto-reload it after a rebuild.", assemblyPath, targetName);
            }
            catch (Exception ex)
            {
                // FileSystemWatcher construction can throw (e.g. platform limits on watch handles).
                // Auto-reload is a convenience on top of the existing manual load_assembly tool, so
                // degrade gracefully rather than crash the server.
                _logger.LogWarning(ex, "Could not start watching '{AssemblyPath}' (target '{TargetName}') for automatic reload; use load_assembly manually after rebuilding.", assemblyPath, targetName);
            }
        }
    }

    private void OnFileChanged(string targetName)
    {
        lock (_gate)
        {
            if (_stopped || !_states.TryGetValue(targetName, out var state) || state.Watcher is null)
            {
                return;
            }

            // Debounce: MSBuild writes the DLL (and PDB) more than once per build, so restart the
            // timer on every event and only actually reload once no further events arrive for
            // DebounceDelay.
            state.DebounceTimer ??= new Timer(_ => OnDebounceElapsed(targetName), state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            state.DebounceTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnWatcherError(string targetName, ErrorEventArgs e)
    {
        // Internal FileSystemWatcher buffer overflow or similar. Log and keep serving the
        // previously loaded assembly - FileSystemWatcher keeps raising events after recovering from
        // an error, so the next build will still trigger a reload attempt.
        string? watchedPath;
        lock (_gate)
        {
            watchedPath = _states.TryGetValue(targetName, out var state) ? state.WatchedPath : null;
        }

        _logger.LogWarning(e.GetException(), "Assembly reload watcher error for '{AssemblyPath}' (target '{TargetName}').", watchedPath, targetName);
    }

    private void OnDebounceElapsed(string targetName)
    {
        string? path;
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            path = _states.TryGetValue(targetName, out var state) ? state.WatchedPath : null;
        }

        if (path is not null)
        {
            _ = ReloadWithRetryAsync(targetName, path);
        }
    }

    /// <summary>Attempts to reload <paramref name="path"/> under <paramref name="targetName"/>,
    /// retrying a bounded number of times with a short delay if the file is still locked (i.e. a
    /// build is still in progress) - the actual lock probing is entirely
    /// <see cref="AssemblyLoaderService.Load(string, string?)"/>'s, this only decides when to try
    /// again. Never throws: every failure path logs and returns.</summary>
    private async Task ReloadWithRetryAsync(string targetName, string path)
    {
        try
        {
            for (var attempt = 1; attempt <= MaxReloadAttempts; attempt++)
            {
                lock (_gate)
                {
                    if (_stopped ||
                        !_states.TryGetValue(targetName, out var state) ||
                        !string.Equals(state.WatchedPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        // Watcher was stopped or re-targeted at a different assembly since this
                        // reload was scheduled - whatever should happen next is already handled.
                        return;
                    }
                }

                try
                {
                    _assemblyLoader.Load(path, targetName == AssemblyLoaderService.DefaultTargetName ? null : targetName);
                    _logger.LogInformation("Automatically reloaded target assembly '{AssemblyPath}' (target '{TargetName}') after detecting a change on disk.", path, targetName);
                    return;
                }
                catch (AssemblyLoadFailedException ex)
                {
                    if (attempt < MaxReloadAttempts)
                    {
                        // Most commonly a file lock because the build is still mid-flight - expected
                        // and not warning-worthy on its own, so stay quiet at Debug and retry.
                        _logger.LogDebug(ex, "Automatic reload of '{AssemblyPath}' (target '{TargetName}') attempt {Attempt}/{MaxAttempts} failed, retrying.", path, targetName, attempt, MaxReloadAttempts);
                        await Task.Delay(RetryDelay).ConfigureAwait(false);
                        continue;
                    }

                    _logger.LogWarning(ex, "Automatic reload of '{AssemblyPath}' (target '{TargetName}') failed after {MaxAttempts} attempts; still serving the previously loaded assembly. Use load_assembly manually once the issue is resolved.", path, targetName, MaxReloadAttempts);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // Defense in depth: never let an unexpected exception from this background reload path
            // escape and crash the process.
            _logger.LogWarning(ex, "Unexpected error while automatically reloading '{AssemblyPath}' (target '{TargetName}').", path, targetName);
        }
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private static void DisposeWatcherAndTimer(TargetWatchState state)
    {
        if (state.Watcher is not null)
        {
            state.Watcher.EnableRaisingEvents = false;
            state.Watcher.Dispose();
            state.Watcher = null;
        }

        state.DebounceTimer?.Dispose();
        state.DebounceTimer = null;
    }

    private sealed class TargetWatchState(string watchedPath)
    {
        public string WatchedPath { get; } = watchedPath;
        public FileSystemWatcher? Watcher { get; set; }
        public Timer? DebounceTimer { get; set; }
    }
}
