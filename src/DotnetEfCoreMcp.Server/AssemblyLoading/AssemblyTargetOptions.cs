namespace DotnetEfCoreMcp.Server.AssemblyLoading;

/// <summary>Startup-seeding configuration for a single named target in
/// <see cref="AssemblyLoaderOptions.Targets"/>. Purely a registration convenience - the same
/// target can also be registered/replaced at runtime via `load_assembly`'s `targetName`
/// parameter.</summary>
public sealed class AssemblyTargetOptions
{
    /// <summary>Absolute or relative path to this target's compiled assembly DLL, loaded at
    /// startup under this entry's key as the target name.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Optional per-target override of <see cref="AssemblyLoaderOptions.AllowedRoots"/>.
    /// May only narrow, never widen, the server-wide allowed roots - the server-wide list is
    /// always enforced in addition to this one.</summary>
    public IReadOnlyList<string>? AllowedRoots { get; init; }

    /// <summary>Optional per-target override of <see cref="AssemblyLoaderOptions.AutoReloadEnabled"/>.
    /// When unset, the server-wide setting applies to this target.</summary>
    public bool? AutoReloadEnabled { get; init; }
}
