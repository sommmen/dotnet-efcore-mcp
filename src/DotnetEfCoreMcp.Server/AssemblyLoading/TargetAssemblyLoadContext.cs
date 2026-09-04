using System.Reflection;
using System.Runtime.Loader;

namespace DotnetEfCoreMcp.Server.AssemblyLoading;

/// <summary>A collectible, isolated <see cref="AssemblyLoadContext"/> used to load a single target
/// project's compiled output. Being collectible lets the server unload a previously loaded target
/// assembly (e.g. after the target project is rebuilt) without restarting the whole MCP server
/// process. Dependency resolution uses <see cref="AssemblyDependencyResolver"/> against the target
/// assembly's own <c>.deps.json</c> so its EF Core / provider DLLs sitting alongside it in the same
/// output folder are found automatically instead of falling back to whatever happens to already be
/// loaded in the default context, and falls back to <see cref="TargetDependencyProbe"/> for the
/// NuGet-cache and shared-framework assets <see cref="AssemblyDependencyResolver"/> cannot see.</summary>
internal sealed class TargetAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly TargetDependencyProbe _probe;

    // Every path this context has itself loaded from disk (the main assembly plus any
    // non-shared dependency resolved via _resolver/_probe). Populated only by
    // LoadAssemblyFromStream, so it always reflects exactly what was loaded *into this context*
    // - shared assemblies resolved by returning null from Load() (and therefore satisfied by the
    // default context) are deliberately excluded, since those already have a MetadataReference
    // available through the server's own PackageReference-restored copies. A thread-safe
    // collection is used because AssemblyLoadContext.Load can be re-entered (e.g. resolving one
    // dependency's own dependencies) while another thread is concurrently probing the target.
    private readonly System.Collections.Concurrent.ConcurrentBag<string> _loadedAssemblyPaths = new();

    // Simple name -> the exact path it was loaded from, used by LoadAdditionalAssembly to detect
    // when a caller requests a different DLL path that happens to share a simple name with
    // something already loaded into this context (e.g. a same-named assembly at a different
    // location) so that case can fail fast instead of silently substituting the wrong assembly.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _loadedAssemblyPathsByName = new(StringComparer.OrdinalIgnoreCase);

    public TargetAssemblyLoadContext(string mainAssemblyPath, string name)
        : base(name, isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _probe = TargetDependencyProbe.Create(mainAssemblyPath);
    }

    /// <summary>Non-fatal problems found while preparing dependency resolution for this target
    /// (e.g. a required shared framework that is not installed). Surfaced as load warnings so a
    /// partially resolvable target explains itself instead of failing opaquely at type-load
    /// time.</summary>
    public IReadOnlyList<string> DependencyDiagnostics => _probe.Diagnostics;

    /// <summary>File-system paths of every assembly actually loaded into this context so far (the
    /// main target assembly plus any non-shared dependency resolved for it), in no particular
    /// order and without duplicates. Used to build a curated <c>MetadataReference</c> list for
    /// compiling user-authored queries against this exact target - see
    /// <c>docs/development/roslyn-user-query.md</c>. Does not include assemblies satisfied by the
    /// default load context (see <see cref="SharedAssemblyNames"/>); callers needing those already
    /// have a same-identity copy available via the server's own package references.</summary>
    public IReadOnlyCollection<string> LoadedAssemblyPaths => _loadedAssemblyPaths;

    /// <summary>Reads an assembly (and optional symbols) from disk into a stream and loads it into
    /// this context. The file streams are disposed immediately after loading, so the build output
    /// is never locked by this context and MSBuild can freely replace it between rebuilds.</summary>
    private Assembly LoadAssemblyFromStream(string assemblyPath)
    {
        using var assemblyStream = File.OpenRead(assemblyPath);
        var symbolPath = Path.ChangeExtension(assemblyPath, ".pdb");
        Assembly assembly;
        if (!File.Exists(symbolPath))
        {
            assembly = LoadFromStream(assemblyStream);
        }
        else
        {
            using var symbolStream = File.OpenRead(symbolPath);
            assembly = LoadFromStream(assemblyStream, symbolStream);
        }

        _loadedAssemblyPaths.Add(assemblyPath);
        if (assembly.GetName().Name is { } loadedSimpleName)
        {
            // TryAdd (not the indexer) so the *first* path a simple name was loaded from is the
            // one collision detection compares against for the lifetime of this context. Using
            // the indexer here would let any later load of the same simple name (e.g. a
            // dependency resolved a second time via Load() below) silently overwrite the
            // recorded path, defeating LoadAdditionalAssembly's same-name/different-path check.
            _loadedAssemblyPathsByName.TryAdd(loadedSimpleName, assemblyPath);
        }

        return assembly;
    }

    public Assembly LoadMainAssembly(string assemblyPath) => LoadAssemblyFromStream(assemblyPath);

    /// <summary>Loads an assembly other than the main target assembly into this same context by
    /// explicit file path - used to resolve a <c>migrationsAssembly</c> parameter that names a DLL
    /// not already a resolvable dependency of the main target (see
    /// <see cref="AssemblyLoaderService.ResolveMigrationsAssembly"/>). Idempotent: if an assembly
    /// with the same simple name was already loaded into this context from the *same* path (e.g.
    /// because it's a dependency of the main target, or a previous call already loaded it), the
    /// existing instance is returned instead of loading a second, distinctly-identified copy - EF
    /// Core matches migrations to a <see cref="DbContext"/> by <see cref="Type"/> reference
    /// equality, so a second copy of the same assembly would silently fail to associate. If the
    /// simple name instead collides with an assembly already loaded from a *different* path,
    /// silently returning that unrelated assembly could produce wrong migrations, so this throws
    /// instead.</summary>
    /// <exception cref="AssemblyLoadFailedException">A same-named assembly is already loaded into
    /// this context from a different path than <paramref name="assemblyPath"/>.</exception>
    // File systems are case-insensitive on Windows/macOS but case-sensitive on Linux. Comparing
    // paths with OrdinalIgnoreCase unconditionally would treat two distinct files on Linux that
    // differ only in casing as "the same" path, silently substituting the wrong assembly instead
    // of the fail-fast collision error below.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public Assembly LoadAdditionalAssembly(string assemblyPath)
    {
        var simpleName = AssemblyName.GetAssemblyName(assemblyPath).Name;
        if (simpleName is not null && _loadedAssemblyPathsByName.TryGetValue(simpleName, out var loadedFromPath))
        {
            if (!string.Equals(loadedFromPath, assemblyPath, PathComparison))
            {
                throw new AssemblyLoadFailedException(
                    $"An assembly named '{simpleName}' is already loaded into this target from a different path ('{loadedFromPath}'). " +
                    $"It cannot also be loaded from '{assemblyPath}' - use a distinctly-named assembly, or ensure the migrations assembly and the main target assembly's dependencies do not collide.");
            }

            var alreadyLoaded = Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
            if (alreadyLoaded is not null)
            {
                return alreadyLoaded;
            }
        }

        return LoadAssemblyFromStream(assemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && SharedFrameworkAssemblyNames.Value.Contains(assemblyName.Name))
        {
            // Returning null here delegates resolution to the default load context, where the
            // server's own PackageReference-restored copy is already loaded.
            return null;
        }

        // Only assemblies that can be located for this specific target (its own code, and any
        // dependency not already shared above) are loaded into this isolated context. Dependencies
        // are loaded from stream (not path) so rebuilds can replace them without fighting an open
        // file lock.
        //
        // AssemblyDependencyResolver is tried first because it applies the host's own asset
        // selection rules, but it only succeeds when the target output carries probing-path
        // information. For a plain class library it returns null for every NuGet package, so
        // TargetDependencyProbe re-resolves those against the restore package folders and the
        // installed shared frameworks. Returning null from here lets anything neither can find
        // (System.*, Microsoft.Extensions.*, our own server assembly) fall back to the default
        // context, which is the correct home for genuinely shared framework types.
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName) ?? _probe.ResolveAssembly(assemblyName);
        return assemblyPath is not null ? LoadAssemblyFromStream(assemblyPath) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName)
            ?? _probe.ResolveUnmanagedDll(unmanagedDllName);
        if (libraryPath is null)
        {
            return IntPtr.Zero;
        }

        return LoadUnmanagedDllFromPath(libraryPath);
    }
}
