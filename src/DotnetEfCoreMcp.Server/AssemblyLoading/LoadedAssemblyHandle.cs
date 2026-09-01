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

    /// <summary>Begins unloading the assembly's <see cref="AssemblyLoadContext"/>. This is
    /// asynchronous with respect to the CLR's GC - the memory isn't necessarily reclaimed the
    /// instant this call returns.</summary>
    internal void Unload() => _context.Unload();

    internal WeakReference CreateWeakContextReference() => new(_context, trackResurrection: true);
}
