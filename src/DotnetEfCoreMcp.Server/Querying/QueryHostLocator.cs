namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Locates the <c>DotnetEfCoreMcp.QueryHost</c> executable that ships alongside this
/// server so <see cref="QueryExecutionOptions.OutOfProcessHostPath"/> does not have to be
/// configured manually for the common case. The packaged NuGet tool copies the query host's
/// publish output into a <c>queryhost/</c> subfolder next to the server's own binaries (see
/// the QueryHost bundling target in DotnetEfCoreMcp.Server.csproj); when running from a full
/// solution build (e.g. `dotnet run`/tests), the query host instead lives under the sibling
/// project's own build output.</summary>
public static class QueryHostLocator
{
    private const string HostFileName = "DotnetEfCoreMcp.QueryHost.dll";

    /// <summary>Returns the full path to the bundled query host DLL if one can be found next to
    /// this server's own binaries or, as a build-time fallback, next to the sibling
    /// <c>DotnetEfCoreMcp.QueryHost</c> project's output; otherwise <see langword="null"/>.</summary>
    public static string? TryGetBundledHostPath() => TryGetBundledHostPath(AppContext.BaseDirectory);

    /// <summary>Overload taking an explicit base directory (instead of
    /// <see cref="AppContext.BaseDirectory"/>) so the resolution logic can be unit tested against
    /// a synthetic directory layout.</summary>
    internal static string? TryGetBundledHostPath(string baseDirectory)
    {
        // Packaged layout: the query host's publish output is copied into a "queryhost"
        // subfolder next to the server's own assembly.
        var bundled = Path.Combine(baseDirectory, "queryhost", HostFileName);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        // Local solution build (no explicit bundling step has run): fall back to the sibling
        // project's own bin output, mirroring the repo layout used by tests.
        foreach (var config in new[] { "Debug", "Release" })
        {
            var candidate = Path.GetFullPath(Path.Combine(
                baseDirectory, "..", "..", "..", "..",
                "DotnetEfCoreMcp.QueryHost", "bin", config, "net10.0", HostFileName));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
