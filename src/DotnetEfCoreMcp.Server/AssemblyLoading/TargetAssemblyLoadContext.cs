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
    /// <summary>Assembly (simple) names that MUST resolve to the copy already loaded in the
    /// default load context rather than a second copy loaded from the target project's own
    /// output folder. Without this, reflection checks like <c>typeof(DbContext).IsAssignableFrom</c>
    /// would fail even for a real DbContext type, because the target's copy of
    /// Microsoft.EntityFrameworkCore.dll would produce a *different*, type-identity-incompatible
    /// <see cref="Type"/> for <c>DbContext</c> than the one our server code references. Sharing
    /// these assemblies assumes the target project's EF Core major version is compatible with the
    /// server's own referenced EF Core version (both net10.0 / EF Core 10.x for this MVP); a
    /// mismatched major version is out of scope and will likely surface as a
    /// <see cref="System.IO.FileLoadException"/> or <see cref="MissingMethodException"/> at
    /// construction time rather than being detected up front.</summary>
    private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.EntityFrameworkCore",
        "Microsoft.EntityFrameworkCore.Abstractions",
        "Microsoft.EntityFrameworkCore.Relational",
        "Microsoft.EntityFrameworkCore.Sqlite",
        "Microsoft.EntityFrameworkCore.SqlServer",
        "Microsoft.AspNetCore.Identity",
        "Microsoft.AspNetCore.Identity.EntityFrameworkCore",
        "Microsoft.Extensions.Identity.Core",
        "Microsoft.Extensions.Identity.Stores",
        "Microsoft.Data.Sqlite",
        "SQLitePCLRaw.core",
        "SQLitePCLRaw.provider.e_sqlite3",
        "SQLitePCLRaw.batteries_v2",
        "Npgsql",
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        "System.Linq.Dynamic.Core",
        "Microsoft.Extensions.Logging.Abstractions",
    };

    private readonly AssemblyDependencyResolver _resolver;
    private readonly TargetDependencyProbe _probe;

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

    /// <summary>Reads an assembly (and optional symbols) from disk into a stream and loads it into
    /// this context. The file streams are disposed immediately after loading, so the build output
    /// is never locked by this context and MSBuild can freely replace it between rebuilds.</summary>
    private Assembly LoadAssemblyFromStream(string assemblyPath)
    {
        using var assemblyStream = File.OpenRead(assemblyPath);
        var symbolPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (!File.Exists(symbolPath))
        {
            return LoadFromStream(assemblyStream);
        }

        using var symbolStream = File.OpenRead(symbolPath);
        return LoadFromStream(assemblyStream, symbolStream);
    }

    public Assembly LoadMainAssembly(string assemblyPath) => LoadAssemblyFromStream(assemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && SharedAssemblyNames.Contains(assemblyName.Name))
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
