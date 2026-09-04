namespace DotnetEfCoreMcp.Server.AssemblyLoading;

/// <summary>Server-wide restrictions on which assembly paths <see cref="AssemblyLoaderService.Load"/>
/// will accept. `load_assembly` takes an arbitrary path from the MCP client and loads it into the
/// server process (a code-execution primitive via module initializers/type loaders), so this is the
/// primary control for constraining that surface in less-trusted deployments.</summary>
public sealed class AssemblyLoaderOptions
{
    /// <summary>Absolute directory paths that loaded assemblies must reside under (recursively).
    /// Empty (the default) means unrestricted, which is appropriate for the intended trusted
    /// local/dev usage - an MCP client running a query is presumed to already be operating with the
    /// same trust level as whoever launched the server. Configure this to lock the server down to a
    /// known set of project output directories in any less-trusted deployment.</summary>
    public IReadOnlyList<string> AllowedRoots { get; init; } = [];

    /// <summary>Whether <see cref="AssemblyReloadWatcher"/> should automatically reload the
    /// currently loaded assembly when its DLL changes on disk (e.g. after MSBuild finishes a
    /// rebuild). Defaults to <c>true</c>; set to <c>false</c> to disable hot-reloading in
    /// less-trusted deployments where a target project's output shouldn't be re-executed without an
    /// explicit `load_assembly` call.</summary>
    public bool AutoReloadEnabled { get; init; } = true;

    /// <summary>Optional startup-seeding map of named targets, keyed by the same logical
    /// `targetName` string used by `load_assembly`/`list_contexts`/`get_schema`/`run_query`/
    /// `run_sql_query`. Each entry is loaded at startup under its key as the target name, in
    /// addition to (or instead of) the single-target `TargetAssemblyPath`/`WorkspacePath`
    /// convenience. Purely a registration convenience - the same targets can also be registered or
    /// replaced at runtime via `load_assembly`'s `targetName` parameter.</summary>
    public IReadOnlyDictionary<string, AssemblyTargetOptions> Targets { get; init; } =
        new Dictionary<string, AssemblyTargetOptions>();
}
