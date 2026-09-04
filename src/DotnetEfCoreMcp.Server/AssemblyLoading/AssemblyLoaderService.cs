using System.Reflection;

namespace DotnetEfCoreMcp.Server.AssemblyLoading;

/// <summary>Owns loading one or more named target projects' compiled assemblies, each into its own
/// isolated, collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, and supports
/// reloading any of them (e.g. after the target project has been rebuilt) without restarting the
/// MCP server process. Thread-safe: all public members serialize on an internal lock, since a
/// reload can race with a discovery/query tool call.
///
/// Calls that omit a `targetName` behave exactly as the single-target server did before named
/// targets existed: they always act on "the current default entry", which is the internal
/// <see cref="DefaultTargetName"/> unless a caller has since registered other named targets and
/// called <see cref="SelectDefault"/> to point the default elsewhere.</summary>
public sealed class AssemblyLoaderService
{
    /// <summary>The reserved, internal target name used to model the pre-multi-target
    /// "single implicit target" behavior. Never exposed to clients as a `targetName` they could
    /// supply themselves; callers that omit `targetName` are transparently routed to whichever
    /// entry is currently the default (this name, unless changed via <see cref="SelectDefault"/>).</summary>
    internal const string DefaultTargetName = "__default__";

    private readonly object _gate = new();
    private readonly IReadOnlyList<string> _allowedRoots;
    private readonly Dictionary<string, TargetEntry> _targets = new(StringComparer.Ordinal);
    private string _defaultTargetName = DefaultTargetName;

    public AssemblyLoaderService() : this(new AssemblyLoaderOptions())
    {
    }

    public AssemblyLoaderService(AssemblyLoaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Normalized once up-front (full path, trailing separator) so containment checks in
        // Load() are simple, case-appropriate-for-the-OS prefix comparisons.
        _allowedRoots = NormalizeRoots(options.AllowedRoots);
    }

    /// <summary>The assembly currently registered under the default target, or <c>null</c> if none
    /// has been loaded yet. Preserved for callers that only ever dealt with a single implicit
    /// target; multi-target callers should use <see cref="Get"/>/<see cref="ListTargets"/> instead.</summary>
    public LoadedAssemblyHandle? Current
    {
        get
        {
            lock (_gate)
            {
                return _targets.TryGetValue(_defaultTargetName, out var entry) ? entry.Handle : null;
            }
        }
    }

    /// <summary>The name of the target that currently resolves as the default when `targetName` is
    /// omitted. Starts out as the internal <see cref="DefaultTargetName"/> and only changes via
    /// <see cref="SelectDefault"/>.</summary>
    public string CurrentDefaultTargetName
    {
        get
        {
            lock (_gate)
            {
                return _defaultTargetName;
            }
        }
    }

    /// <summary>Raised each time <see cref="Load(string, string?)"/> succeeds, after the new
    /// assembly has become registered under its target name. Used by
    /// <see cref="AssemblyReloadWatcher"/> to (re)target its per-target
    /// <see cref="System.IO.FileSystemWatcher"/> rather than polling. Handlers run outside the
    /// internal lock, so they may safely call back into this service.</summary>
    public event Action<AssemblyLoadedEventArgs>? AssemblyLoaded;

    /// <summary>Loads (or reloads, if a target of that name is already loaded) the target assembly
    /// at <paramref name="assemblyPath"/> under the given <paramref name="targetName"/>. If
    /// <paramref name="targetName"/> is omitted, the call targets (and replaces) the current default
    /// entry - by default an internal reserved name - preserving the exact single-target behavior
    /// existing callers depend on. If a target of that name was already loaded, its previous
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> is unloaded first; other registered
    /// targets are never affected.</summary>
    /// <exception cref="AssemblyLoadFailedException">
    /// The file does not exist, is locked, has an incompatible runtime/TFM, or one of its
    /// dependencies could not be resolved.
    /// </exception>
    public LoadedAssemblyHandle Load(string assemblyPath, string? targetName = null)
    {
        return LoadCore(assemblyPath, targetName, additionalAllowedRoots: null, autoReloadOverride: null);
    }

    /// <summary>Startup-seeding overload used by <c>Program.cs</c> to load a named target from
    /// <see cref="AssemblyLoaderOptions.Targets"/>, applying that target's optional narrowing
    /// <see cref="AssemblyTargetOptions.AllowedRoots"/>/<see cref="AssemblyTargetOptions.AutoReloadEnabled"/>
    /// overrides.</summary>
    internal LoadedAssemblyHandle Load(string assemblyPath, string targetName, AssemblyTargetOptions targetOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        ArgumentNullException.ThrowIfNull(targetOptions);

        return LoadCore(assemblyPath, targetName, targetOptions.AllowedRoots, targetOptions.AutoReloadEnabled);
    }

    private LoadedAssemblyHandle LoadCore(string assemblyPath, string? targetName, IReadOnlyList<string>? additionalAllowedRoots, bool? autoReloadOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);

        var fullPath = Path.GetFullPath(assemblyPath);
        ValidateAllowedRoots(fullPath, additionalAllowedRoots);

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
        string resolvedName;
        lock (_gate)
        {
            resolvedName = string.IsNullOrWhiteSpace(targetName) ? _defaultTargetName : targetName;
            var previous = _targets.TryGetValue(resolvedName, out var previousEntry) ? previousEntry : null;

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
            _targets[resolvedName] = new TargetEntry(handle, fileInfo.LastWriteTimeUtc, autoReloadOverride);

            previous?.Handle.Unload();
        }

        // Raised outside the lock so handlers (e.g. AssemblyReloadWatcher re-targeting its
        // FileSystemWatcher) can't deadlock by calling back into this service.
        AssemblyLoaded?.Invoke(new AssemblyLoadedEventArgs(resolvedName, handle));
        return handle;
    }

    /// <summary>Resolves the loaded assembly for <paramref name="targetName"/>.
    /// If <paramref name="targetName"/> is supplied but no such target is registered, throws
    /// <see cref="UnknownAssemblyTargetException"/> (which deliberately does not enumerate other
    /// registered target names). If <paramref name="targetName"/> is omitted, resolves the current
    /// default target, falling back to the sole registered target if there is exactly one and no
    /// default has been established yet; returns <c>null</c> if nothing can be resolved (e.g.
    /// nothing has ever been loaded).</summary>
    public LoadedAssemblyHandle? Get(string? targetName = null)
    {
        lock (_gate)
        {
            return ResolveEntry(targetName)?.Handle;
        }
    }

    /// <summary>Must be called while holding <see cref="_gate"/>.</summary>
    private TargetEntry? ResolveEntry(string? targetName)
    {
        if (!string.IsNullOrWhiteSpace(targetName))
        {
            if (_targets.TryGetValue(targetName, out var named))
            {
                return named;
            }

            throw new UnknownAssemblyTargetException(targetName);
        }

        if (_targets.TryGetValue(_defaultTargetName, out var byDefault))
        {
            return byDefault;
        }

        // No explicit default resolved (e.g. everything so far was registered under explicit
        // names and select_target was never called) - fall back to the sole target, if there is
        // exactly one, matching the "implicitly the first/only registered target" resolution rule.
        return _targets.Count == 1 ? _targets.Values.Single() : null;
    }

    /// <summary>Returns a snapshot of every currently registered target.</summary>
    public IReadOnlyList<AssemblyTargetInfo> ListTargets()
    {
        lock (_gate)
        {
            return _targets
                .Select(pair => new AssemblyTargetInfo(
                    pair.Key,
                    pair.Value.Handle,
                    IsDefault: pair.Key == _defaultTargetName,
                    AutoReloadEnabled: pair.Value.AutoReloadEnabledOverride))
                .ToList();
        }
    }

    /// <summary>Makes <paramref name="targetName"/> the target that resolves whenever
    /// `targetName` is omitted from a tool call.</summary>
    /// <exception cref="UnknownAssemblyTargetException">No target with that name is registered.</exception>
    public void SelectDefault(string targetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);

        lock (_gate)
        {
            if (!_targets.ContainsKey(targetName))
            {
                throw new UnknownAssemblyTargetException(targetName);
            }

            _defaultTargetName = targetName;
        }
    }

    /// <summary>Returns <c>true</c> if the resolved target's on-disk file has been modified (e.g.
    /// by a rebuild) since it was loaded, meaning a call to <see cref="Load(string, string?)"/>
    /// with the same path would pick up newer code. Returns <c>false</c> if nothing can be
    /// resolved for <paramref name="targetName"/> (mirrors the pre-multi-target "nothing loaded"
    /// behavior) rather than throwing, except when an explicit, unknown name is supplied.</summary>
    public bool IsTargetStale(string? targetName = null)
    {
        lock (_gate)
        {
            var entry = ResolveEntry(targetName);
            if (entry is null)
            {
                return false;
            }

            if (!File.Exists(entry.Handle.AssemblyPath))
            {
                return true;
            }

            return new FileInfo(entry.Handle.AssemblyPath).LastWriteTimeUtc > entry.LoadedFileWriteTimeUtc;
        }
    }

    /// <summary>Returns <c>true</c> if the currently loaded default-target assembly's on-disk file
    /// has been modified since it was loaded. Preserved for single-target callers;
    /// equivalent to <c>IsTargetStale(null)</c>.</summary>
    public bool IsCurrentAssemblyStale() => IsTargetStale(null);

    /// <summary>Unloads the current default target, if any, leaving no assembly loaded under that
    /// name. Other registered named targets are unaffected.</summary>
    public void Unload()
    {
        lock (_gate)
        {
            if (_targets.Remove(_defaultTargetName, out var entry))
            {
                entry.Handle.Unload();
            }
        }
    }

    private void ValidateAllowedRoots(string fullPath, IReadOnlyList<string>? additionalAllowedRoots)
    {
        if (_allowedRoots.Count > 0 && !MatchesAnyRoot(fullPath, _allowedRoots))
        {
            throw new AssemblyLoadFailedException(
                $"Target assembly path '{fullPath}' is outside the configured allowed roots. Configure `AssemblyLoader:AllowedRoots` to include this location, or point at an assembly under an already-allowed root.");
        }

        // Per-target AllowedRoots overrides may only narrow, never widen, the server-wide roots -
        // enforced simply by additionally requiring containment within them when present.
        if (additionalAllowedRoots is { Count: > 0 })
        {
            var normalized = NormalizeRoots(additionalAllowedRoots);
            if (!MatchesAnyRoot(fullPath, normalized))
            {
                throw new AssemblyLoadFailedException(
                    $"Target assembly path '{fullPath}' is outside this target's configured allowed roots.");
            }
        }
    }

    private static bool MatchesAnyRoot(string fullPath, IReadOnlyList<string> roots) =>
        roots.Any(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> NormalizeRoots(IReadOnlyList<string> roots) =>
        roots
            .Select(root => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToList();

    private sealed record TargetEntry(LoadedAssemblyHandle Handle, DateTimeOffset LoadedFileWriteTimeUtc, bool? AutoReloadEnabledOverride);
}

/// <summary>A snapshot describing one registered target, returned by
/// <see cref="AssemblyLoaderService.ListTargets"/> for the `list_loaded_assemblies` tool and for
/// <see cref="AssemblyReloadWatcher"/> to enumerate what it needs to watch.</summary>
/// <param name="Name">The raw registry key - equal to
/// <see cref="AssemblyLoaderService.DefaultTargetName"/> for the reserved implicit target.</param>
/// <param name="Handle">The currently loaded assembly for this target.</param>
/// <param name="IsDefault">Whether this target currently resolves when `targetName` is omitted.</param>
/// <param name="AutoReloadEnabled">This target's auto-reload override, or <c>null</c> to defer to
/// the server-wide <see cref="AssemblyLoaderOptions.AutoReloadEnabled"/> default.</param>
public sealed record AssemblyTargetInfo(string Name, LoadedAssemblyHandle Handle, bool IsDefault, bool? AutoReloadEnabled);
