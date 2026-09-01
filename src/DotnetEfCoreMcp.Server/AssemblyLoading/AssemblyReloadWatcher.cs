using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetEfCoreMcp.Server.AssemblyLoading;

/// <summary>Watches the currently loaded target assembly's DLL for on-disk changes (e.g. MSBuild
/// finishing a rebuild) and automatically re-invokes <see cref="AssemblyLoaderService.Load"/> so the
/// server keeps serving up-to-date schema/types without a manual `load_assembly` call. Idle until an
/// assembly has been loaded (manually, via startup `TargetAssemblyPath`, or via workspace
/// auto-discovery); disabled entirely when <see cref="AssemblyLoaderOptions.AutoReloadEnabled"/> is
/// <c>false</c>.
///
/// MSBuild typically writes a DLL (and its PDB) more than once in quick succession during a single
/// build, so file-change events are debounced for <see cref="DebounceDelay"/> before a reload is
/// attempted. The DLL can still be locked when the debounce window elapses (a build still in
/// progress), so a failed attempt is retried a bounded number of times with a short delay before
/// giving up and logging a warning - this reuses <see cref="AssemblyLoaderService.Load"/>'s own
/// file-lock probing rather than duplicating it. Any failure (locked file, bad IL mid-write, etc.)
/// leaves the previously loaded assembly serving requests; it is never treated as fatal.</summary>
public sealed class AssemblyReloadWatcher : IHostedService, IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(300);
    private const int MaxReloadAttempts = 5;

    private readonly AssemblyLoaderService _assemblyLoader;
    private readonly AssemblyLoaderOptions _options;
    private readonly ILogger<AssemblyReloadWatcher> _logger;
    private readonly object _gate = new();

    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private string? _watchedPath;
    private bool _stopped;

    public AssemblyReloadWatcher(AssemblyLoaderService assemblyLoader, AssemblyLoaderOptions options, ILogger<AssemblyReloadWatcher> logger)
    {
        _assemblyLoader = assemblyLoader;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.AutoReloadEnabled)
        {
            _logger.LogDebug("Automatic assembly reload is disabled (AssemblyLoader:AutoReloadEnabled=false).");
            return Task.CompletedTask;
        }

        _assemblyLoader.AssemblyLoaded += OnAssemblyLoaded;

        // An assembly may already have been loaded (TargetAssemblyPath/workspace auto-discovery, or
        // a manual load_assembly call) before this hosted service started - start watching it now
        // rather than waiting for the next Load() call to raise AssemblyLoaded.
        var current = _assemblyLoader.Current;
        if (current is not null)
        {
            Retarget(current.AssemblyPath);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _assemblyLoader.AssemblyLoaded -= OnAssemblyLoaded;

        lock (_gate)
        {
            _stopped = true;
            DisposeWatcherAndTimer();
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true;
            DisposeWatcherAndTimer();
        }
    }

    private void OnAssemblyLoaded(LoadedAssemblyHandle handle) => Retarget(handle.AssemblyPath);

    /// <summary>(Re)points the <see cref="FileSystemWatcher"/> at <paramref name="assemblyPath"/>,
    /// tearing down any previous watcher first. A no-op if already watching the same path, which is
    /// the common case of our own automatic reload (or a repeated manual `load_assembly` of the same
    /// file) raising <see cref="AssemblyLoaderService.AssemblyLoaded"/> again.</summary>
    private void Retarget(string assemblyPath)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            if (string.Equals(_watchedPath, assemblyPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            DisposeWatcherAndTimer();
            _watchedPath = assemblyPath;

            var directory = Path.GetDirectoryName(assemblyPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                // Shouldn't normally happen since Load() already required the file to exist, but
                // don't let a race (e.g. the containing directory removed) crash the process.
                _logger.LogDebug("Not watching '{AssemblyPath}' for changes: containing directory not found.", assemblyPath);
                return;
            }

            try
            {
                var watcher = new FileSystemWatcher(directory, Path.GetFileName(assemblyPath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                };
                watcher.Changed += OnFileChanged;
                watcher.Created += OnFileChanged;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;

                _logger.LogDebug("Watching '{AssemblyPath}' for changes to auto-reload it after a rebuild.", assemblyPath);
            }
            catch (Exception ex)
            {
                // FileSystemWatcher construction can throw (e.g. platform limits on watch handles).
                // Auto-reload is a convenience on top of the existing manual load_assembly tool, so
                // degrade gracefully rather than crash the server.
                _logger.LogWarning(ex, "Could not start watching '{AssemblyPath}' for automatic reload; use load_assembly manually after rebuilding.", assemblyPath);
            }
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if (_stopped || _watcher is null)
            {
                return;
            }

            // Debounce: MSBuild writes the DLL (and PDB) more than once per build, so restart the
            // timer on every event and only actually reload once no further events arrive for
            // DebounceDelay.
            _debounceTimer ??= new Timer(_ => OnDebounceElapsed(), state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _debounceTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Internal FileSystemWatcher buffer overflow or similar. Log and keep serving the
        // previously loaded assembly - FileSystemWatcher keeps raising events after recovering from
        // an error, so the next build will still trigger a reload attempt.
        _logger.LogWarning(e.GetException(), "Assembly reload watcher error for '{AssemblyPath}'.", _watchedPath);
    }

    private void OnDebounceElapsed()
    {
        string? path;
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            path = _watchedPath;
        }

        if (path is not null)
        {
            _ = ReloadWithRetryAsync(path);
        }
    }

    /// <summary>Attempts to reload <paramref name="path"/>, retrying a bounded number of times with
    /// a short delay if the file is still locked (i.e. a build is still in progress) - the actual
    /// lock probing is entirely <see cref="AssemblyLoaderService.Load"/>'s, this only decides when to
    /// try again. Never throws: every failure path logs and returns.</summary>
    private async Task ReloadWithRetryAsync(string path)
    {
        try
        {
            for (var attempt = 1; attempt <= MaxReloadAttempts; attempt++)
            {
                lock (_gate)
                {
                    if (_stopped || !string.Equals(_watchedPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        // Watcher was stopped or re-targeted at a different assembly since this
                        // reload was scheduled - whatever should happen next is already handled.
                        return;
                    }
                }

                try
                {
                    _assemblyLoader.Load(path);
                    _logger.LogInformation("Automatically reloaded target assembly '{AssemblyPath}' after detecting a change on disk.", path);
                    return;
                }
                catch (AssemblyLoadFailedException ex)
                {
                    if (attempt < MaxReloadAttempts)
                    {
                        // Most commonly a file lock because the build is still mid-flight - expected
                        // and not warning-worthy on its own, so stay quiet at Debug and retry.
                        _logger.LogDebug(ex, "Automatic reload of '{AssemblyPath}' attempt {Attempt}/{MaxAttempts} failed, retrying.", path, attempt, MaxReloadAttempts);
                        await Task.Delay(RetryDelay).ConfigureAwait(false);
                        continue;
                    }

                    _logger.LogWarning(ex, "Automatic reload of '{AssemblyPath}' failed after {MaxAttempts} attempts; still serving the previously loaded assembly. Use load_assembly manually once the issue is resolved.", path, MaxReloadAttempts);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // Defense in depth: never let an unexpected exception from this background reload path
            // escape and crash the process.
            _logger.LogWarning(ex, "Unexpected error while automatically reloading '{AssemblyPath}'.", path);
        }
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private void DisposeWatcherAndTimer()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }

        _debounceTimer?.Dispose();
        _debounceTimer = null;
    }
}
