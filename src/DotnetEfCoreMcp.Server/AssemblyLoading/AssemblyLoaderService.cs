using System.Reflection;

namespace DotnetEfCoreMcp.Server.AssemblyLoading;

/// <summary>Owns loading a single target project's compiled assembly into an isolated,
/// collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, and supports reloading it
/// (e.g. after the target project has been rebuilt) without restarting the MCP server process.
/// Thread-safe: all public members serialize on an internal lock, since a reload can race with a
/// discovery/query tool call.</summary>
public sealed class AssemblyLoaderService
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<string> _allowedRoots;
    private LoadedAssemblyHandle? _current;
    private DateTimeOffset _loadedFileWriteTimeUtc;

    public AssemblyLoaderService() : this(new AssemblyLoaderOptions())
    {
    }

    public AssemblyLoaderService(AssemblyLoaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Normalized once up-front (full path, trailing separator) so containment checks in
        // Load() are simple, case-appropriate-for-the-OS prefix comparisons.
        _allowedRoots = options.AllowedRoots
            .Select(root => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToList();
    }

    /// <summary>The currently loaded assembly, or <c>null</c> if none has been loaded yet.</summary>
    public LoadedAssemblyHandle? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Raised each time <see cref="Load"/> succeeds, after the new assembly has become
    /// <see cref="Current"/>. Used by <see cref="AssemblyReloadWatcher"/> to (re)target its
    /// <see cref="System.IO.FileSystemWatcher"/> at whichever file is currently loaded, without
    /// polling <see cref="Current"/>. Handlers run outside the internal lock, so they may safely
    /// call back into this service.</summary>
    public event Action<LoadedAssemblyHandle>? AssemblyLoaded;

    /// <summary>Loads (or reloads, if an assembly is already loaded) the target assembly at
    /// <paramref name="assemblyPath"/>. If an assembly was already loaded, its previous
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> is unloaded first.</summary>
    /// <exception cref="AssemblyLoadFailedException">
    /// The file does not exist, is locked, has an incompatible runtime/TFM, or one of its
    /// dependencies could not be resolved.
    /// </exception>
    public LoadedAssemblyHandle Load(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);

        var fullPath = Path.GetFullPath(assemblyPath);

        if (_allowedRoots.Count > 0 && !_allowedRoots.Any(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AssemblyLoadFailedException(
                $"Target assembly path '{fullPath}' is outside the configured allowed roots. Configure `AssemblyLoader:AllowedRoots` to include this location, or point at an assembly under an already-allowed root.");
        }

        if (!File.Exists(fullPath))
        {
            throw new AssemblyLoadFailedException(
                $"Target assembly not found at '{fullPath}'. Build the target project first (e.g. `dotnet build`) and point at its bin/<Configuration>/<TFM>/*.dll output.");
        }

        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(fullPath);
            // Proactively probe for a file lock (e.g. the target project is mid-rebuild) so we can
            // surface a clear, actionable error instead of an opaque IOException deep inside the
            // loader.
            using var probe = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (IOException ex)
        {
            throw new AssemblyLoadFailedException(
                $"Target assembly at '{fullPath}' could not be read (it may be locked by an in-progress build). Wait for the build to finish and try again.",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new AssemblyLoadFailedException(
                $"Access to target assembly at '{fullPath}' was denied.",
                ex);
        }

        LoadedAssemblyHandle handle;
        lock (_gate)
        {
            var previous = _current;

            var contextName = $"TargetAssembly_{Guid.NewGuid():N}";
            var context = new TargetAssemblyLoadContext(fullPath, contextName);

            Assembly assembly;
            try
            {
                assembly = context.LoadMainAssembly(fullPath);
            }
            catch (BadImageFormatException ex)
            {
                context.Unload();
                throw new AssemblyLoadFailedException(
                    $"'{fullPath}' is not a valid managed assembly, or targets an incompatible platform/bitness for this process.",
                    ex);
            }
            catch (FileLoadException ex)
            {
                context.Unload();
                throw new AssemblyLoadFailedException(
                    $"Failed to load '{fullPath}': {ex.Message}. This often means a dependency (e.g. an EF Core provider package) is missing from the target project's output folder, or its version is incompatible with the server's own EF Core version.",
                    ex);
            }
            catch (Exception ex)
            {
                context.Unload();
                throw new AssemblyLoadFailedException($"Failed to load '{fullPath}': {ex.Message}", ex);
            }

            handle = new LoadedAssemblyHandle(context, assembly, fullPath, DateTimeOffset.UtcNow);
            _current = handle;
            _loadedFileWriteTimeUtc = fileInfo.LastWriteTimeUtc;

            if (previous is not null)
            {
                previous.Unload();
            }
        }

        // Raised outside the lock so handlers (e.g. AssemblyReloadWatcher re-targeting its
        // FileSystemWatcher) can't deadlock by calling back into this service.
        AssemblyLoaded?.Invoke(handle);
        return handle;
    }

    /// <summary>Resolves <paramref name="migrationsAssembly"/> - a simple assembly name (e.g.
    /// <c>"OPG.AuthApi"</c>) or a path to a compiled DLL - into an <see cref="Assembly"/> loaded
    /// into the same <see cref="System.Runtime.Loader.AssemblyLoadContext"/> as
    /// <paramref name="handle"/>'s main target assembly. Loading into the same context is required
    /// so EF Core's migration-to-<see cref="Microsoft.EntityFrameworkCore.DbContext"/> matching
    /// (which compares <see cref="Type"/> references, not names) succeeds regardless of whether the
    /// migrations live alongside the context or in a separate assembly.
    ///
    /// A simple name is resolved the same way the loaded target's own dependencies are (via the
    /// target's <c>AssemblyDependencyResolver</c>/<c>TargetDependencyProbe</c> against its own
    /// .deps.json) - this covers the common case where the migrations assembly is already a
    /// project or package reference of the loaded target (e.g. a web API project that references a
    /// shared data-access library and is itself the assembly passed to <see cref="Load"/>). A value
    /// that looks like a path is instead loaded explicitly by file, subject to the same
    /// <c>AssemblyLoader:AllowedRoots</c> containment check as <see cref="Load"/>, since it is
    /// another arbitrary-file-load primitive.</summary>
    /// <exception cref="AssemblyLoadFailedException">
    /// The name could not be resolved as a dependency of the loaded target, the path is outside the
    /// configured allowed roots, or the file does not exist or fails to load.
    /// </exception>
    public Assembly ResolveMigrationsAssembly(LoadedAssemblyHandle handle, string migrationsAssembly)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsAssembly);

        var looksLikePath =
            migrationsAssembly.Contains(Path.DirectorySeparatorChar) ||
            migrationsAssembly.Contains(Path.AltDirectorySeparatorChar) ||
            migrationsAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

        if (!looksLikePath)
        {
            try
            {
                return handle.Context.LoadFromAssemblyName(new AssemblyName(migrationsAssembly));
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                throw new AssemblyLoadFailedException(
                    $"Migrations assembly '{migrationsAssembly}' could not be resolved as a dependency of the currently loaded target assembly ('{handle.AssemblyPath}'). " +
                    "If it is not a project or package reference of the loaded assembly, pass its compiled DLL path instead.",
                    ex);
            }
        }

        var fullPath = Path.GetFullPath(migrationsAssembly);

        if (_allowedRoots.Count > 0 && !_allowedRoots.Any(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AssemblyLoadFailedException(
                $"Migrations assembly path '{fullPath}' is outside the configured allowed roots. Configure `AssemblyLoader:AllowedRoots` to include this location, or point at an assembly under an already-allowed root.");
        }

        if (!File.Exists(fullPath))
        {
            throw new AssemblyLoadFailedException(
                $"Migrations assembly not found at '{fullPath}'. Build the target project first (e.g. `dotnet build`) and point at its bin/<Configuration>/<TFM>/*.dll output.");
        }

        try
        {
            return handle.Context.LoadAdditionalAssembly(fullPath);
        }
        catch (BadImageFormatException ex)
        {
            throw new AssemblyLoadFailedException(
                $"'{fullPath}' is not a valid managed assembly, or targets an incompatible platform/bitness for this process.",
                ex);
        }
        catch (Exception ex)
        {
            throw new AssemblyLoadFailedException($"Failed to load migrations assembly '{fullPath}': {ex.Message}", ex);
        }
    }

    /// <summary>Returns <c>true</c> if the currently loaded assembly's on-disk file has been
    /// modified (e.g. by a rebuild) since it was loaded, meaning a call to <see cref="Load"/> with
    /// the same path would pick up newer code.</summary>
    public bool IsCurrentAssemblyStale()
    {
        lock (_gate)
        {
            if (_current is null)
            {
                return false;
            }

            if (!File.Exists(_current.AssemblyPath))
            {
                return true;
            }

            return new FileInfo(_current.AssemblyPath).LastWriteTimeUtc > _loadedFileWriteTimeUtc;
        }
    }

    /// <summary>Unloads the currently loaded assembly, if any, leaving no assembly loaded.</summary>
    public void Unload()
    {
        lock (_gate)
        {
            _current?.Unload();
            _current = null;
        }
    }
}
