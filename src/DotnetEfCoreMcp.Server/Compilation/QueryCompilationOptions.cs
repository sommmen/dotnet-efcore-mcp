namespace DotnetEfCoreMcp.Server.Compilation;

/// <summary>Server-side configuration for the Roslyn/LINQPad-style query execution engine.
/// See <c>docs/development/roslyn-user-query.md</c>.</summary>
public sealed class QueryCompilationOptions
{
    /// <summary>Extra assembly simple names (already loaded somewhere in the current process, e.g.
    /// via the default <see cref="System.Runtime.Loader.AssemblyLoadContext"/>) to add as
    /// <see cref="Microsoft.CodeAnalysis.MetadataReference"/>s for every compiled query, on top of
    /// the target assembly's own dependency closure and the built-in BCL/EF Core allowlist. Empty
    /// by default. This is server-side configuration only - a request can never add references,
    /// mirroring the existing "no per-request override of security-relevant caps" convention used
    /// for <see cref="Querying.QueryExecutionOptions.MaxTake"/> et al.</summary>
    public IReadOnlyList<string> AdditionalReferenceAssemblyNames { get; init; } = Array.Empty<string>();

    /// <summary>Wall-clock budget for compiling (parsing + binding + emitting) a single query, as a
    /// defense against pathological input (e.g. extremely deep generic nesting) causing the
    /// compiler itself to take an unreasonable amount of time. Does not include load/execution
    /// time.</summary>
    public int CompileTimeoutSeconds { get; init; } = 10;
}
