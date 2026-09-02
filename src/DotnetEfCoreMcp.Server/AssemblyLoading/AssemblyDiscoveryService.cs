using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace DotnetEfCoreMcp.Server.AssemblyLoading;

public sealed record AssemblyCandidate(
    string AssemblyPath,
    string ProjectPath,
    string Configuration,
    string TargetFramework,
    DateTime LastWriteTimeUtc,
    bool IsPreferred,
    bool LikelyContainsDbContext = false);

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
                .Select(candidate => candidate with { LikelyContainsDbContext = ProbablyContainsDbContext(candidate.AssemblyPath) })
                // Ranking prioritizes assemblies whose metadata references DbContext (a fast,
                // best-effort heuristic - see ProbablyContainsDbContext) so agents scanning a
                // monorepo with hundreds of built outputs (test projects, tools, unrelated APIs)
                // see the assemblies that actually matter first, before falling back to the
                // pre-existing Debug/newest/highest-TFM tie-breakers.
                .OrderByDescending(candidate => candidate.LikelyContainsDbContext)
                .ThenBy(candidate => ConfigurationRank(candidate.Configuration))
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

    /// <summary>Best-effort, fast (no assembly load, no target-context involvement) check for
    /// whether an assembly is likely to contain a DbContext-derived type: reads the assembly's
    /// metadata tables directly via <see cref="MetadataReader"/> and looks for any type
    /// definition whose base type - walked up the TypeDef/TypeRef chain within this same
    /// assembly's metadata - is literally named "DbContext". This is intentionally shallow (it
    /// cannot see DbContext types defined in referenced assemblies, generic base type
    /// instantiations, or types loaded via reflection-only tricks) but is enough to deprioritize
    /// the overwhelming majority of non-DbContext builds (test projects, tools, unrelated APIs)
    /// in a large monorepo without paying the cost of loading every candidate into an
    /// AssemblyLoadContext just to rank them. Returns false (never throws) for anything that
    /// can't be read as a valid .NET assembly.</summary>
    private static bool ProbablyContainsDbContext(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                return false;
            }

            var reader = peReader.GetMetadataReader();
            foreach (var typeDefHandle in reader.TypeDefinitions)
            {
                var typeDef = reader.GetTypeDefinition(typeDefHandle);
                if (IsOrDerivesFromDbContext(reader, typeDef, depth: 0))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return false;
        }
    }

    private static bool IsOrDerivesFromDbContext(MetadataReader reader, TypeDefinition typeDef, int depth)
    {
        // Bound recursion: a legitimate DbContext base chain is a handful of types deep at most,
        // but a pathological/corrupt assembly could in principle reference a self-cycle.
        if (depth > 16)
        {
            return false;
        }

        var baseTypeHandle = typeDef.BaseType;
        if (baseTypeHandle.IsNil)
        {
            return false;
        }

        var baseTypeName = baseTypeHandle.Kind switch
        {
            HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)baseTypeHandle).Name),
            HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)baseTypeHandle).Name),
            _ => null,
        };

        if (baseTypeName == "DbContext")
        {
            return true;
        }

        // The base type is only further inspectable if it's itself defined in this same
        // assembly's metadata (a TypeReference to another assembly can't be walked further
        // without loading that assembly too, which this best-effort scan intentionally avoids).
        if (baseTypeHandle.Kind == HandleKind.TypeDefinition)
        {
            return IsOrDerivesFromDbContext(reader, reader.GetTypeDefinition((TypeDefinitionHandle)baseTypeHandle), depth + 1);
        }

        return false;
    }

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
