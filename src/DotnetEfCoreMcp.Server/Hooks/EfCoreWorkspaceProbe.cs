namespace DotnetEfCoreMcp.Server.Hooks;

/// <summary>
/// Cheap heuristic for "does this workspace have anything to do with EF Core".
/// </summary>
/// <remarks>
/// A user-scoped <c>SessionStart</c> hook fires in every session on the machine, so it needs a way
/// to stay quiet in the repositories where EF Core guidance would be noise. This deliberately reads
/// project files rather than compiled output: the hook runs before the agent has done anything, so
/// there may be no build yet, and a scan has to finish in well under the client's hook timeout.
/// </remarks>
public static class EfCoreWorkspaceProbe
{
    /// <summary>Marker that identifies an EF Core dependency in a project or props file.</summary>
    private const string EfCoreMarker = "Microsoft.EntityFrameworkCore";

    /// <summary>
    /// Directories deep enough to find a project in a conventional <c>src/</c> layout, but shallow
    /// enough that a large monorepo does not blow the hook timeout.
    /// </summary>
    private const int MaxDepth = 4;

    /// <summary>Bound on files inspected, so a pathological tree cannot stall the session.</summary>
    private const int MaxFilesInspected = 400;

    private static readonly string[] CandidatePatterns =
        ["*.csproj", "Directory.Packages.props", "Directory.Build.props", "packages.lock.json"];

    /// <summary>
    /// Returns <see langword="true"/> when a project file under <paramref name="workspace"/>
    /// references EF Core.
    /// </summary>
    public static bool LooksLikeEfCoreWorkspace(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            return false;
        }

        var inspected = 0;

        try
        {
            foreach (var directory in EnumerateDirectories(workspace, MaxDepth))
            {
                foreach (var pattern in CandidatePatterns)
                {
                    foreach (var file in SafeEnumerateFiles(directory, pattern))
                    {
                        if (++inspected > MaxFilesInspected)
                        {
                            return false;
                        }

                        if (MentionsEfCore(file))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreadable tree is treated as "not EF Core": staying silent is always safe, while
            // throwing from a session-start hook is not.
        }

        return false;
    }

    private static IEnumerable<string> EnumerateDirectories(string root, int depth)
    {
        yield return root;

        if (depth <= 0)
        {
            yield break;
        }

        string[] children;
        try
        {
            children = Directory.GetDirectories(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);

            // Build output and dependency caches contain thousands of files and never hold the
            // project's own EF Core reference, so skipping them is what keeps this fast.
            if (name.StartsWith('.')
                || name is "bin" or "obj" or "node_modules" or "packages" or "artifacts" or "dist" or "target")
            {
                continue;
            }

            foreach (var descendant in EnumerateDirectories(child, depth - 1))
            {
                yield return descendant;
            }
        }
    }

    private static string[] SafeEnumerateFiles(string directory, string pattern)
    {
        try
        {
            return Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool MentionsEfCore(string file)
    {
        try
        {
            // Project and props files are small; a lock file can be large, so cap the read rather
            // than pulling a multi-megabyte file into memory inside a hook.
            using var reader = new StreamReader(file);
            var buffer = new char[64 * 1024];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);

            return read > 0
                && new string(buffer, 0, read).Contains(EfCoreMarker, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
