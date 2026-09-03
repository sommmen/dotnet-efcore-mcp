using System.Reflection;
using System.Runtime.Loader;

namespace DotnetEfCoreMcp.Server.AssemblyLoading;

/// <summary>A handle to a currently loaded target assembly plus the collectible
/// <see cref="AssemblyLoadContext"/> it lives in. Disposing/unloading is a two-step, GC-driven
/// process: call <see cref="Unload"/> to trigger unloading and let go of the strong references,
/// then the underlying <see cref="AssemblyLoadContext"/> can only actually be collected once all
/// other references (including any surfaced <see cref="Type"/>/<see cref="Assembly"/> objects) are
/// released and a GC has run - callers must not cache <see cref="Assembly"/> or <see cref="Type"/>
/// instances from a handle after calling <see cref="Unload"/>.</summary>
public sealed class LoadedAssemblyHandle
{
    private readonly TargetAssemblyLoadContext _context;

    internal LoadedAssemblyHandle(TargetAssemblyLoadContext context, Assembly assembly, string assemblyPath, DateTimeOffset loadedAtUtc)
    {
        _context = context;
        Assembly = assembly;
        AssemblyPath = assemblyPath;
        LoadedAtUtc = loadedAtUtc;
    }

    public Assembly Assembly { get; }

    public string AssemblyPath { get; }

    public DateTimeOffset LoadedAtUtc { get; }

    /// <summary>Non-fatal problems found while preparing dependency resolution for this target
    /// (e.g. a shared framework the target needs that is not installed). Empty for a target whose
    /// dependencies could all be located.</summary>
    public IReadOnlyList<string> DependencyDiagnostics => _context.DependencyDiagnostics;

    /// <summary>File-system paths of every assembly loaded into this target's context so far (the
    /// target assembly itself plus its non-shared dependencies). Used by the Roslyn query compiler
    /// to build a curated <c>MetadataReference</c> list for compiling user-authored queries against
    /// this exact target - see <c>docs/development/roslyn-user-query.md</c>.</summary>
    public IReadOnlyCollection<string> LoadedAssemblyPaths => _context.LoadedAssemblyPaths;

    /// <summary>The target's own <see cref="AssemblyLoadContext"/>, exposed internally so the
    /// Roslyn query compiler's per-request <c>CompiledQueryLoadContext</c> can fall back to it when
    /// resolving assemblies the compiled query assembly references but that are not in the shared
    /// default context (mirroring the "reuse the already-resolved dependency set" design in
    /// <c>docs/development/roslyn-user-query.md</c>). Not exposed publicly for the same reason
    /// <see cref="CreateWeakContextReference"/> exists instead of a public <c>Context</c> property:
    /// long-lived external references to a collectible context would defeat unloading.</summary>
    internal TargetAssemblyLoadContext Context => _context;

    /// <summary>Begins unloading the assembly's <see cref="AssemblyLoadContext"/>. This is
    /// asynchronous with respect to the CLR's GC - the memory isn't necessarily reclaimed the
    /// instant this call returns.</summary>
    internal void Unload() => _context.Unload();

    internal WeakReference CreateWeakContextReference() => new(_context, trackResurrection: true);
}
