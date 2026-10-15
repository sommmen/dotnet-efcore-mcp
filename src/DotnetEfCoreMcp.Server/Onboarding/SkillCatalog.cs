namespace DotnetEfCoreMcp.Server.Onboarding;

/// <summary>
/// Locates the skill folders shipped with this tool.
/// </summary>
/// <remarks>
/// Skills are plain markdown, so they are packed as content next to the server's binaries rather
/// than embedded as resources: an installed skill has to be a real directory on disk that the
/// client can scan, and copying a directory is simpler than unpacking one. The repo-relative
/// fallback mirrors <c>QueryHostLocator</c> and exists so a local solution build (F5, tests) finds
/// the same skills as a packaged <c>dnx</c> run.
/// </remarks>
public static class SkillCatalog
{
    /// <summary>The feedback skill's folder name, used to report whether it is installed.</summary>
    public const string FeedbackSkillName = "dotnet-efcore-mcp-feedback";

    /// <summary>Folder next to the server binaries that packaged skills are copied into.</summary>
    private const string BundleFolderName = "skills";

    /// <summary>
    /// Returns the directory containing the shipped skill folders, or <see langword="null"/> when
    /// none can be found.
    /// </summary>
    public static string? TryGetSkillsRoot() => TryGetSkillsRoot(AppContext.BaseDirectory);

    /// <summary>
    /// Overload taking an explicit base directory so resolution can be unit tested against a
    /// synthetic layout.
    /// </summary>
    internal static string? TryGetSkillsRoot(string baseDirectory)
    {
        // Packaged layout: the skill folders are copied next to the server's own assembly.
        var bundled = Path.Combine(baseDirectory, BundleFolderName);
        if (ContainsSkill(bundled))
        {
            return bundled;
        }

        // Local solution build: walk up to the repository root and use the source tree directly.
        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, BundleFolderName);
            if (ContainsSkill(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>Enumerates the shipped skills, newest resolution first.</summary>
    public static IReadOnlyList<string> ListSkillNames()
    {
        var root = TryGetSkillsRoot();
        return root is null ? [] : ListSkillNames(root);
    }

    /// <summary>Enumerates the skill folder names directly under <paramref name="root"/>.</summary>
    internal static IReadOnlyList<string> ListSkillNames(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return [.. Directory.EnumerateDirectories(root)
            .Where(IsSkillFolder)
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A skill is any folder holding a <c>SKILL.md</c>; that file is the contract.</summary>
    internal static bool IsSkillFolder(string directory) =>
        File.Exists(Path.Combine(directory, "SKILL.md"));

    private static bool ContainsSkill(string root) =>
        Directory.Exists(root) && Directory.EnumerateDirectories(root).Any(IsSkillFolder);
}
