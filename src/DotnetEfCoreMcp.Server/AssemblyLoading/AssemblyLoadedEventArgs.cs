namespace DotnetEfCoreMcp.Server.AssemblyLoading;

/// <summary>Carries the name of the target that was (re)loaded alongside its resulting handle, so
/// <see cref="AssemblyReloadWatcher"/> can multiplex a separate watcher per named target instead of
/// assuming there is only ever one loaded assembly.</summary>
public sealed class AssemblyLoadedEventArgs(string targetName, LoadedAssemblyHandle handle) : EventArgs
{
    /// <summary>The logical target name the assembly was registered under. Equal to
    /// <see cref="AssemblyLoaderService.DefaultTargetName"/> for calls that omit a target name
    /// (today's single-target behavior).</summary>
    public string TargetName { get; } = targetName;

    public LoadedAssemblyHandle Handle { get; } = handle;
}
