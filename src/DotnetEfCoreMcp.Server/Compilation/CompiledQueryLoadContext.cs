using System.Reflection;
using System.Runtime.Loader;
using DotnetEfCoreMcp.Server.AssemblyLoading;

namespace DotnetEfCoreMcp.Server.Compilation;

/// <summary>A collectible, isolated <see cref="AssemblyLoadContext"/> used to load a single
/// Roslyn-compiled <c>run_query</c> assembly (see <c>docs/development/roslyn-user-query.md</c>).
/// One instance is created per query request and disposed/unloaded immediately after the query
/// finishes, independent of the target project assembly's own lifetime.
///
/// This needs exactly the same "shared assembly identity" behavior as
/// <see cref="TargetAssemblyLoadContext"/> (it must see the *same* <c>DbContext</c>/entity types
/// the target assembly and the server both see), so it shares
/// <see cref="SharedFrameworkAssemblyNames"/>. Unlike <see cref="TargetAssemblyLoadContext"/>, it
/// does not need <see cref="TargetDependencyProbe"/>'s NuGet-cache/probing-root logic, because a
/// compiled query assembly only ever references assemblies already resolved once for the target
/// (the reference list is closed by construction - see the compiler's
/// <c>MetadataReference</c>-building step) - so any non-shared dependency it needs is resolved by
/// simple name against the target's own already-loaded assembly paths instead.</summary>
internal sealed class CompiledQueryLoadContext : AssemblyLoadContext
{
    private readonly TargetAssemblyLoadContext _targetContext;
    private readonly IReadOnlyDictionary<string, string> _targetAssemblyPathsByName;

    public CompiledQueryLoadContext(TargetAssemblyLoadContext targetContext, IReadOnlyCollection<string> targetLoadedAssemblyPaths, string name)
        : base(name, isCollectible: true)
    {
        _targetContext = targetContext;

        // Keyed by simple assembly name (case-insensitively) so Load(AssemblyName) can look a
        // referenced dependency up in O(1); last-one-wins on an (extremely unlikely) duplicate
        // simple name is an acceptable simplification here since the target's own dependency
        // closure was already de-duplicated by TargetAssemblyLoadContext.
        _targetAssemblyPathsByName = targetLoadedAssemblyPaths.ToDictionary(
            path => Path.GetFileNameWithoutExtension(path),
            path => path,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Loads the compiled query assembly's raw bytes (produced by
    /// <c>CSharpCompilation.Emit</c> into an in-memory stream) into this context.</summary>
    public Assembly LoadCompiledAssembly(Stream peStream, Stream? pdbStream) =>
        pdbStream is null ? LoadFromStream(peStream) : LoadFromStream(peStream, pdbStream);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null)
        {
            return null;
        }

        if (SharedFrameworkAssemblyNames.Value.Contains(assemblyName.Name))
        {
            // Returning null here delegates resolution to the default load context, where the
            // server's own PackageReference-restored copy is already loaded.
            return null;
        }

        // Reuse the target context's already-loaded assembly instance. Loading a second copy into
        // this context would make DbContextOptions<TContext> and entity types incompatible with
        // the types the server constructed from the target context.
        var targetAssembly = _targetContext.Assemblies.FirstOrDefault(assembly =>
            string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
        if (targetAssembly is not null)
        {
            return targetAssembly;
        }

        // A referenced target dependency may not have been loaded yet. Trigger its normal target
        // resolution path, then return that exact assembly instance if it becomes available.
        if (_targetAssemblyPathsByName.ContainsKey(assemblyName.Name))
        {
            try
            {
                _targetContext.LoadFromAssemblyName(assemblyName);
            }
            catch (FileNotFoundException)
            {
                return null;
            }

            return _targetContext.Assemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }
}
