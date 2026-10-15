namespace DotnetEfCoreMcp.Server.Onboarding;

/// <summary>
/// The outcome of installing, removing, or inspecting one skill at one destination.
/// </summary>
/// <param name="SkillName">The skill folder name.</param>
/// <param name="Path">The destination that was inspected or written.</param>
/// <param name="Changed">Whether anything on disk changed.</param>
/// <param name="Message">A human-readable summary for CLI and tool output.</param>
public sealed record SkillInstallResult(string SkillName, string Path, bool Changed, string Message);

/// <summary>
/// Installs the shipped skill folders into the directories agent CLIs scan.
/// </summary>
/// <remarks>
/// <para>
/// This duplicates what <c>skills/install-skills.ps1</c> does, deliberately. The script requires a
/// clone of this repository, which the people who need the feedback skill most - someone who
/// installed the MCP server with <c>dnx</c> and hit a wall in their own codebase - do not have. Both
/// write the same marker file, so either can manage an install the other created.
/// </para>
/// <para>
/// A destination that is not marked as ours is never touched. Silently replacing a hand-written
/// skill of the same name would be a data-loss bug, so an unmanaged collision is reported and
/// skipped instead.
/// </para>
/// </remarks>
public sealed class SkillInstaller
{
    /// <summary>
    /// Marker file written into every install, identifying it as managed by this tool.
    /// </summary>
    /// <remarks>
    /// Shared with <c>skills/install-skills.ps1</c>/<c>.sh</c> so the two installers recognize each
    /// other's work rather than refusing to replace it.
    /// </remarks>
    internal const string MarkerFileName = ".dotnet-efcore-mcp-skill-install";

    /// <summary>Skill-development scaffolding that users of a skill have no use for.</summary>
    private static readonly string[] ExcludedFolders = ["evals"];

    private readonly string? _sourceRoot;

    /// <summary>Creates an installer over the shipped skills.</summary>
    /// <param name="sourceRoot">Overrides the skill source directory, for tests.</param>
    public SkillInstaller(string? sourceRoot = null) =>
        _sourceRoot = sourceRoot ?? SkillCatalog.TryGetSkillsRoot();

    /// <summary>Whether any skills are available to install.</summary>
    public bool HasSkills => AvailableSkills.Count > 0;

    /// <summary>The names of the skills this installer can install.</summary>
    public IReadOnlyList<string> AvailableSkills =>
        _sourceRoot is null ? [] : SkillCatalog.ListSkillNames(_sourceRoot);

    /// <summary>
    /// Resolves the directory a client scans for skills.
    /// </summary>
    /// <param name="scope">Whether to resolve the repo-local or per-user directory.</param>
    /// <param name="repositoryRoot">Repository root, used only by <see cref="InstallScope.Workspace"/>.</param>
    /// <remarks>
    /// One directory serves every client in each scope. <c>~/.agents/skills</c> and
    /// <c>.github/skills</c> are both read by Copilot CLI, Claude Code, and Codex, so installing
    /// once there avoids three copies of the same markdown drifting apart.
    /// </remarks>
    public static string GetSkillsDirectory(InstallScope scope, string repositoryRoot) => scope switch
    {
        InstallScope.Workspace => Path.Combine(repositoryRoot, ".github", "skills"),
        InstallScope.User => Path.Combine(UserDirectory.Resolve(), ".agents", "skills"),
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported install scope."),
    };

    /// <summary>Installs every shipped skill, or just <paramref name="skillName"/> when given.</summary>
    public IReadOnlyList<SkillInstallResult> Install(InstallScope scope, string repositoryRoot,
        string? skillName = null, bool force = false)
    {
        var destinationRoot = GetSkillsDirectory(scope, repositoryRoot);

        return Run(skillName, source =>
        {
            var name = Path.GetFileName(source)!;
            var destination = Path.Combine(destinationRoot, name);

            if (Directory.Exists(destination))
            {
                if (!IsManaged(destination))
                {
                    return new SkillInstallResult(name, destination, Changed: false,
                        $"Skipped {destination}: it exists and was not installed by dotnet-efcore-mcp. "
                        + "Remove it first if you want this skill to replace it.");
                }

                if (!force && IsUpToDate(source, destination))
                {
                    return new SkillInstallResult(name, destination, Changed: false,
                        $"Already up to date: {destination}");
                }

                Directory.Delete(destination, recursive: true);
                CopySkill(source, destination);
                return new SkillInstallResult(name, destination, Changed: true, $"Updated {destination}");
            }

            CopySkill(source, destination);
            return new SkillInstallResult(name, destination, Changed: true, $"Installed {destination}");
        });
    }

    /// <summary>Removes previously installed skills, leaving unmanaged directories alone.</summary>
    public IReadOnlyList<SkillInstallResult> Uninstall(InstallScope scope, string repositoryRoot,
        string? skillName = null)
    {
        var destinationRoot = GetSkillsDirectory(scope, repositoryRoot);

        return Run(skillName, source =>
        {
            var name = Path.GetFileName(source)!;
            var destination = Path.Combine(destinationRoot, name);

            if (!Directory.Exists(destination))
            {
                return new SkillInstallResult(name, destination, Changed: false, $"Nothing to remove: {destination}");
            }

            if (!IsManaged(destination))
            {
                return new SkillInstallResult(name, destination, Changed: false,
                    $"Skipped {destination}: not installed by dotnet-efcore-mcp.");
            }

            Directory.Delete(destination, recursive: true);
            return new SkillInstallResult(name, destination, Changed: true, $"Removed {destination}");
        });
    }

    /// <summary>Reports whether each shipped skill is currently installed.</summary>
    public IReadOnlyList<SkillInstallResult> Status(InstallScope scope, string repositoryRoot,
        string? skillName = null)
    {
        var destinationRoot = GetSkillsDirectory(scope, repositoryRoot);

        return Run(skillName, source =>
        {
            var name = Path.GetFileName(source)!;
            var destination = Path.Combine(destinationRoot, name);

            var message = !Directory.Exists(destination)
                ? "not installed"
                : !IsManaged(destination)
                    ? "present but not managed by dotnet-efcore-mcp"
                    : IsUpToDate(source, destination)
                        ? "installed"
                        : "installed (outdated)";

            return new SkillInstallResult(name, destination, Changed: false, message);
        });
    }

    /// <summary>
    /// Whether <paramref name="skillName"/> is installed and current at the given scope.
    /// </summary>
    public bool IsInstalled(InstallScope scope, string repositoryRoot, string skillName)
    {
        var destination = Path.Combine(GetSkillsDirectory(scope, repositoryRoot), skillName);
        return Directory.Exists(destination) && SkillCatalog.IsSkillFolder(destination);
    }

    private IReadOnlyList<SkillInstallResult> Run(string? skillName, Func<string, SkillInstallResult> action)
    {
        var available = _sourceRoot is null ? [] : SkillCatalog.ListSkillNames(_sourceRoot);

        // "Nothing is bundled" and "that skill does not exist" need different messages: the first is
        // a packaging situation the user cannot fix by correcting a name, so it points at the repo's
        // install script instead of listing alternatives.
        if (available.Count == 0)
        {
            return
            [
                new SkillInstallResult(skillName ?? "(all)", "(none)", Changed: false,
                    "No skills are bundled with this build, so there is nothing to install. "
                    + "Install the skills from a clone with ./skills/install-skills.ps1 instead."),
            ];
        }

        var sources = available
            .Where(name => skillName is null || name.Equals(skillName, StringComparison.OrdinalIgnoreCase))
            .Select(name => Path.Combine(_sourceRoot!, name))
            .ToList();

        if (sources.Count == 0)
        {
            return
            [
                new SkillInstallResult(skillName ?? "(all)", "(none)", Changed: false,
                    $"Unknown skill '{skillName}'. Available: {string.Join(", ", available)}"),
            ];
        }

        return [.. sources.Select(action)];
    }

    private static bool IsManaged(string destination) =>
        File.Exists(Path.Combine(destination, MarkerFileName));

    /// <summary>
    /// Compares the installed copy against the source by content.
    /// </summary>
    /// <remarks>
    /// Timestamps are not usable here: a packaged install's files carry the package's extraction
    /// time, not the skill's, so every run would look stale. Skills are a handful of small markdown
    /// files, so hashing the copied set is cheap and exact.
    /// </remarks>
    private static bool IsUpToDate(string source, string destination)
    {
        var expected = EnumerateSkillFiles(source)
            .ToDictionary(
                file => Path.GetRelativePath(source, file).Replace('\\', '/'),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);

        var actual = EnumerateSkillFiles(destination)
            .Where(file => !string.Equals(Path.GetFileName(file), MarkerFileName, StringComparison.Ordinal))
            .ToDictionary(
                file => Path.GetRelativePath(destination, file).Replace('\\', '/'),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);

        if (expected.Count != actual.Count)
        {
            return false;
        }

        foreach (var (relativePath, content) in expected)
        {
            if (!actual.TryGetValue(relativePath, out var installed) || !content.AsSpan().SequenceEqual(installed))
            {
                return false;
            }
        }

        return true;
    }

    private static void CopySkill(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in EnumerateSkillFiles(source))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }

        File.WriteAllText(
            Path.Combine(destination, MarkerFileName),
            "Installed by dotnet-efcore-mcp. Safe to delete; the installer will treat this skill as "
            + "unmanaged afterwards and refuse to replace it." + Environment.NewLine);
    }

    private static IEnumerable<string> EnumerateSkillFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => !IsExcluded(root, file));

    private static bool IsExcluded(string root, string file) =>
        Path.GetRelativePath(root, file)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => ExcludedFolders.Contains(segment, StringComparer.OrdinalIgnoreCase));
}
