using System.Reflection;
using System.Runtime.Loader;

namespace DotnetEfCoreMcp.Server.AssemblyLoading;

/// <summary>A collectible, isolated <see cref="AssemblyLoadContext"/> used to load a single target
/// project's compiled output. Being collectible lets the server unload a previously loaded target
/// assembly (e.g. after the target project is rebuilt) without restarting the whole MCP server
/// process. Dependency resolution uses <see cref="AssemblyDependencyResolver"/> against the target
/// assembly's own <c>.deps.json</c> so its EF Core / provider DLLs sitting alongside it in the same
/// output folder are found automatically instead of falling back to whatever happens to already be
/// loaded in the default context.</summary>
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
        "Microsoft.Data.Sqlite",
        "SQLitePCLRaw.core",
        "SQLitePCLRaw.provider.e_sqlite3",
        "SQLitePCLRaw.batteries_v2",
        "Npgsql",
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        "System.Linq.Dynamic.Core",
    };

    private readonly AssemblyDependencyResolver _resolver;

    public TargetAssemblyLoadContext(string mainAssemblyPath, string name)
        : base(name, isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && SharedAssemblyNames.Contains(assemblyName.Name))
        {
            // Returning null here delegates resolution to the default load context, where the
            // server's own PackageReference-restored copy is already loaded.
            return null;
        }

        // Let other framework/shared assemblies (System.*, Microsoft.Extensions.*, our own MCP
        // server assembly, etc.) continue to resolve against the default load context too. Only
        // assemblies the resolver can specifically locate next to the target DLL (the target's
        // own code and any dependency not already shared above) are loaded into this isolated
        // context.
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is not null ? LoadFromAssemblyPath(assemblyPath) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is not null ? LoadUnmanagedDllFromPath(libraryPath) : IntPtr.Zero;
    }
}
