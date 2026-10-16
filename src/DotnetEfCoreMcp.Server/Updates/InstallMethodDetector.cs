namespace DotnetEfCoreMcp.Server.Updates;

/// <summary>
/// Infers how this server was installed, so an update notice can name the right upgrade command -
/// or stay silent when it cannot.
/// </summary>
/// <remarks>
/// <para>
/// No .NET API reports the install method: the process starts after its launcher already picked a
/// package, and nothing about that choice survives into the process. Everything here is therefore a
/// heuristic over filesystem layout, with one exception - the npm wrapper sets
/// <see cref="InstallMethodVariable"/> deliberately, because we own it.
/// </para>
/// <para>
/// The bias is strongly towards <see cref="InstallMethod.Unknown"/>. This follows the GitHub CLI,
/// whose update checker is compiled out entirely unless the binary came from a channel the
/// maintainers can name, on the reasoning that an upgrade command for the wrong install method is
/// worse than no notice at all. A user told to run a command that does not apply to them has been
/// given a puzzle, not help.
/// </para>
/// </remarks>
public sealed class InstallMethodDetector
{
    /// <summary>
    /// Environment variable the npm wrapper sets to identify itself.
    /// </summary>
    /// <remarks>
    /// This is the one high-confidence signal available, and it exists only because the wrapper is
    /// ours to change. Everything else in this class is inference.
    /// </remarks>
    public const string InstallMethodVariable = "DOTNETEFCOREMCP_INSTALL_METHOD";

    /// <summary>NuGet package id this server ships as, lowercased as it appears on disk.</summary>
    internal const string PackageIdOnDisk = "dotnetefcoremcp.server";

    /// <summary>File name of a .NET local tool manifest.</summary>
    internal const string ManifestFileName = "dotnet-tools.json";

    private readonly string _baseDirectory;
    private readonly string _workingDirectory;
    private readonly Func<string, string?> _environment;

    /// <summary>
    /// Creates a detector that inspects the running process.
    /// </summary>
    public InstallMethodDetector()
        : this(AppContext.BaseDirectory, Directory.GetCurrentDirectory(), Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>
    /// Creates a detector over explicit inputs, so tests can exercise each branch against a
    /// synthetic directory layout instead of the machine's real install state.
    /// </summary>
    /// <param name="baseDirectory">Directory the server assembly was loaded from.</param>
    /// <param name="workingDirectory">Directory to search upwards from for a tool manifest.</param>
    /// <param name="environment">Environment variable lookup.</param>
    internal InstallMethodDetector(
        string baseDirectory,
        string workingDirectory,
        Func<string, string?> environment)
    {
        _baseDirectory = baseDirectory;
        _workingDirectory = workingDirectory;
        _environment = environment;
    }

    /// <summary>
    /// Determines the install method.
    /// </summary>
    /// <returns>
    /// The detected method, or <see cref="InstallMethod.Unknown"/> when the evidence is ambiguous.
    /// </returns>
    public InstallMethod Detect()
    {
        // The wrapper's own declaration wins over any inference: it knows, we only guess. It is
        // also checked first because an npm-launched server sits in the global tools directory and
        // would otherwise be misread as a plain global install.
        var declared = _environment(InstallMethodVariable);
        if (!string.IsNullOrWhiteSpace(declared))
        {
            if (declared.Trim().Equals("npm", StringComparison.OrdinalIgnoreCase))
            {
                return InstallMethod.Npm;
            }

            // An unrecognized value means someone is doing something we do not understand. Staying
            // silent is the safe reading of that.
            return InstallMethod.Unknown;
        }

        var segments = SplitPath(_baseDirectory);

        // Global and local tool installs both unpack into a `.store` directory; `dnx` runs straight
        // out of the NuGet package cache. The two layouts are distinguishable by the folder the
        // package sits under, which is why this checks the containing structure rather than just
        // looking for the package id somewhere in the path.
        if (ContainsSegment(segments, ".store"))
        {
            // A tool manifest naming this package means the install is manifest-scoped. Checking
            // the manifest rather than the path is deliberate: both install kinds share the same
            // `.store` layout, so the manifest is the only thing that distinguishes them.
            return HasLocalToolManifest() ? InstallMethod.LocalTool : InstallMethod.GlobalTool;
        }

        if (IsPackageCachePath(segments))
        {
            return InstallMethod.Dnx;
        }

        return InstallMethod.Unknown;
    }

    /// <summary>
    /// Returns the command that upgrades this install, or <see langword="null"/> when no notice
    /// should be shown for it.
    /// </summary>
    /// <param name="method">The detected install method.</param>
    /// <param name="includePrerelease">
    /// Whether to ask for prerelease versions, which is required while this package publishes
    /// preview builds only.
    /// </param>
    /// <returns>
    /// The upgrade command, or <see langword="null"/> for methods that update themselves
    /// (<see cref="InstallMethod.Npm"/>, <see cref="InstallMethod.Dnx"/>) or cannot be named
    /// (<see cref="InstallMethod.Unknown"/>).
    /// </returns>
    public static string? GetUpgradeCommand(InstallMethod method, bool includePrerelease)
    {
        var prerelease = includePrerelease ? " --prerelease" : string.Empty;

        return method switch
        {
            InstallMethod.GlobalTool => $"dotnet tool update --global DotnetEfCoreMcp.Server{prerelease}",
            InstallMethod.LocalTool => $"dotnet tool update --local DotnetEfCoreMcp.Server{prerelease}",

            // dnx refetches the newest matching version on every invocation (measured - see
            // docs/development/update-notification.md), and the npm wrapper updates the .NET tool
            // before starting it. Both are already current; a notice would be pure noise.
            InstallMethod.Dnx or InstallMethod.Npm => null,
            _ => null,
        };
    }

    /// <summary>
    /// Reports whether an update notice should be shown for <paramref name="method"/>.
    /// </summary>
    public static bool ShouldNotify(InstallMethod method) =>
        GetUpgradeCommand(method, includePrerelease: false) is not null;

    /// <summary>
    /// Walks up from the working directory looking for a tool manifest that lists this package.
    /// </summary>
    /// <remarks>
    /// Matches the .NET CLI's own manifest search: <c>.config/dotnet-tools.json</c> in each
    /// ancestor directory. The package id check matters because an unrelated manifest in a parent
    /// directory must not make a global install look local.
    /// </remarks>
    private bool HasLocalToolManifest()
    {
        var directory = TryGetDirectory(_workingDirectory);

        while (directory is not null)
        {
            var manifest = Path.Combine(directory.FullName, ".config", ManifestFileName);

            if (File.Exists(manifest) && ManifestMentionsPackage(manifest))
            {
                return true;
            }

            directory = directory.Parent;
        }

        return false;
    }

    private static bool ManifestMentionsPackage(string manifestPath)
    {
        try
        {
            // A substring match is enough and is intentionally more forgiving than parsing: the
            // question is only "does this manifest concern our package", and a malformed or
            // unexpected manifest shape should fall back to the global reading rather than throw.
            return File.ReadAllText(manifestPath)
                .Contains(PackageIdOnDisk, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Detection is advisory; an unreadable manifest must never break startup.
            return false;
        }
    }

    private static DirectoryInfo? TryGetDirectory(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? null : new DirectoryInfo(path);
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reports whether the path looks like the NuGet package cache, which is where
    /// <c>dnx</c>/<c>dotnet tool exec</c> runs a tool from.
    /// </summary>
    /// <remarks>
    /// Requires the package id to sit directly under a <c>packages</c> directory, matching the
    /// cache's <c>packages/&lt;id&gt;/&lt;version&gt;/</c> layout. Looking for the id alone would
    /// also match a build output directory that merely happens to be named after the package.
    /// </remarks>
    private static bool IsPackageCachePath(IReadOnlyList<string> segments)
    {
        for (var index = 1; index < segments.Count; index++)
        {
            if (segments[index].Equals(PackageIdOnDisk, StringComparison.OrdinalIgnoreCase)
                && segments[index - 1].Equals("packages", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] SplitPath(string path) =>
        string.IsNullOrWhiteSpace(path)
            ? []
            : path.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

    private static bool ContainsSegment(IReadOnlyList<string> segments, string segment)
    {
        for (var index = 0; index < segments.Count; index++)
        {
            if (segments[index].Equals(segment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
