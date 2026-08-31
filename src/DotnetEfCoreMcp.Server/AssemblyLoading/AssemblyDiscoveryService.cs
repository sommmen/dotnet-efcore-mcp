using System.Xml.Linq;

namespace DotnetEfCoreMcp.Server.AssemblyLoading;

public sealed record AssemblyCandidate(
    string AssemblyPath,
    string ProjectPath,
    string Configuration,
    string TargetFramework,
    DateTime LastWriteTimeUtc,
    bool IsPreferred);

public sealed class AssemblyDiscoveryException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>Discovers project output assemblies under a workspace and ranks the most useful
/// development output first. Only assemblies corresponding to projects in the workspace are
/// returned, which avoids treating copied NuGet dependencies as target candidates.</summary>
public sealed class AssemblyDiscoveryService
{
    public IReadOnlyList<AssemblyCandidate> Discover(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        string root;
        try
        {
            root = Path.GetFullPath(workspacePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AssemblyDiscoveryException($"Workspace path '{workspacePath}' is invalid.", ex);
        }

        if (!Directory.Exists(root))
        {
            throw new AssemblyDiscoveryException($"Workspace directory '{root}' does not exist.");
        }

        try
        {
            var ranked = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                .Where(path => !IsUnderExcludedDirectory(Path.GetRelativePath(root, path)))
                .SelectMany(DiscoverProjectOutputs)
                .OrderBy(candidate => ConfigurationRank(candidate.Configuration))
                .ThenByDescending(candidate => candidate.LastWriteTimeUtc)
                .ThenByDescending(candidate => TargetFrameworkVersion(candidate.TargetFramework))
                .ThenBy(candidate => candidate.AssemblyPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return ranked.Select((candidate, index) => candidate with { IsPreferred = index == 0 }).ToArray();
        }
        catch (AssemblyDiscoveryException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            throw new AssemblyDiscoveryException($"Could not inspect projects under workspace '{root}'.", ex);
        }
    }

    private static IEnumerable<AssemblyCandidate> DiscoverProjectOutputs(string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var assemblyName = ReadAssemblyName(projectPath) ?? Path.GetFileNameWithoutExtension(projectPath);
        var binDirectory = Path.Combine(projectDirectory, "bin");
        if (!Directory.Exists(binDirectory))
        {
            yield break;
        }

        foreach (var assemblyPath in Directory.EnumerateFiles(binDirectory, $"{assemblyName}.dll", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(binDirectory, assemblyPath);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Length < 3 || segments.Any(segment => segment.Equals("ref", StringComparison.OrdinalIgnoreCase) || segment.Equals("refint", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var configuration = segments[0];
            var targetFramework = segments[1];
            yield return new AssemblyCandidate(
                Path.GetFullPath(assemblyPath),
                Path.GetFullPath(projectPath),
                configuration,
                targetFramework,
                File.GetLastWriteTimeUtc(assemblyPath),
                false);
        }
    }

    private static string? ReadAssemblyName(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        return document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "AssemblyName" && !string.IsNullOrWhiteSpace(element.Value))
            ?.Value.Trim();
    }

    private static bool IsUnderExcludedDirectory(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                            segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                            segment.Equals(".git", StringComparison.OrdinalIgnoreCase));

    private static int ConfigurationRank(string configuration) =>
        configuration.Equals("Debug", StringComparison.OrdinalIgnoreCase) ? 0 :
        configuration.Equals("Release", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

    private static Version TargetFrameworkVersion(string targetFramework)
    {
        var versionStart = targetFramework.IndexOfAny("0123456789".ToCharArray());
        if (versionStart < 0)
        {
            return new Version();
        }

        var versionText = new string(targetFramework[versionStart..]
            .TakeWhile(character => char.IsDigit(character) || character == '.')
            .ToArray());
        return Version.TryParse(versionText, out var version) ? version : new Version();
    }
}
