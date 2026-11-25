using System.Reflection;
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
    bool LikelyContainsDbContext = false,
    bool IsTestProject = false);

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
            var cache = new DbContextMetadataCache();
            var ranked = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                .Where(path => !IsUnderExcludedDirectory(Path.GetRelativePath(root, path)))
                .SelectMany(projectPath => DiscoverProjectOutputs(projectPath, root))
                .Select(candidate => candidate with { LikelyContainsDbContext = ProbablyContainsDbContext(candidate.AssemblyPath, cache) })
                .ToArray()
                // Ranking prioritizes assemblies whose metadata describes a concrete DbContext (a fast,
                // best-effort heuristic - see ProbablyContainsDbContext), with test projects behind the
                // application assemblies that own the data access, then falls back to the
                // Debug/newest/highest-TFM tie-breakers.
                .OrderByDescending(candidate => candidate.LikelyContainsDbContext && !candidate.IsTestProject)
                .ThenByDescending(candidate => candidate.LikelyContainsDbContext)
                .ThenBy(candidate => candidate.IsTestProject)
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

    private static IEnumerable<AssemblyCandidate> DiscoverProjectOutputs(string projectPath, string workspaceRoot)
    {
        var project = ReadProject(projectPath);
        var assemblyName = project.AssemblyName ?? Path.GetFileNameWithoutExtension(projectPath);
        return DiscoverProjectBinOutputs(projectPath, assemblyName, project.IsTestProject)
            .Concat(DiscoverArtifactsOutputs(projectPath, workspaceRoot, assemblyName, project));
    }

    /// <summary>Finds output written with the SDK's artifacts layout (<c>UseArtifactsOutput</c>):
    /// <c>&lt;artifacts&gt;/bin/&lt;ProjectName&gt;/&lt;pivot&gt;/</c>, where the artifacts folder sits in
    /// any ancestor of the project up to the workspace root and may have a custom name (<c>ArtifactsPath</c>).
    /// The pivot is <c>&lt;config&gt;[_&lt;tfm&gt;][_&lt;rid&gt;]</c>; the TFM is omitted for single-target projects.</summary>
    private static IEnumerable<AssemblyCandidate> DiscoverArtifactsOutputs(
        string projectPath, string workspaceRoot, string assemblyName, ProjectInfo project)
    {
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(projectPath)!);
             directory is not null && IsWithin(directory.FullName, workspaceRoot);
             directory = directory.Parent)
        {
            foreach (var artifactsDirectory in directory.EnumerateDirectories())
            {
                if (artifactsDirectory.Name is "bin" or "obj" or ".git")
                {
                    continue;
                }

                var projectOutputDirectory = Path.Combine(artifactsDirectory.FullName, "bin", projectName);
                if (!Directory.Exists(projectOutputDirectory) || !seen.Add(projectOutputDirectory))
                {
                    continue;
                }

                foreach (var pivotDirectory in Directory.EnumerateDirectories(projectOutputDirectory))
                {
                    var assemblyPath = Path.Combine(pivotDirectory, $"{assemblyName}.dll");
                    if (!File.Exists(assemblyPath))
                    {
                        continue;
                    }

                    var pivot = Path.GetFileName(pivotDirectory).Split('_');
                    yield return new AssemblyCandidate(
                        Path.GetFullPath(assemblyPath),
                        Path.GetFullPath(projectPath),
                        pivot[0],
                        pivot.Length > 1 ? pivot[1] : project.TargetFramework ?? string.Empty,
                        File.GetLastWriteTimeUtc(assemblyPath),
                        false,
                        IsTestProject: project.IsTestProject);
                }
            }
        }
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    private static IEnumerable<AssemblyCandidate> DiscoverProjectBinOutputs(string projectPath, string assemblyName, bool isTestProject)
    {
        var binDirectory = Path.Combine(Path.GetDirectoryName(projectPath)!, "bin");
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
                false,
                IsTestProject: isTestProject);
        }
    }

    private static readonly string[] TestPackagePrefixes =
        ["Microsoft.NET.Test.Sdk", "xunit", "NUnit", "MSTest", "TUnit", "Microsoft.Testing.Platform"];

    private static readonly string[] TestNameSegments = ["Test", "Tests", "UnitTests", "IntegrationTests", "Specs"];

    private sealed record ProjectInfo(string? AssemblyName, string? TargetFramework, bool IsTestProject);

    private static ProjectInfo ReadProject(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        var elements = document.Descendants().ToArray();
        string? ReadProperty(string name) => elements
            .FirstOrDefault(element => element.Name.LocalName == name && !string.IsNullOrWhiteSpace(element.Value))
            ?.Value.Trim();
        var assemblyName = ReadProperty("AssemblyName");

        var declaredTestProject = elements.Any(element =>
            element.Name.LocalName == "IsTestProject" && element.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
        var referencesTestPackage = elements.Any(element =>
            element.Name.LocalName is "PackageReference" or "PackageVersion"
            && (element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value) is { } package
            && TestPackagePrefixes.Any(prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        var namedLikeTests = Path.GetFileNameWithoutExtension(projectPath)
            .Split('.')
            .Any(segment => TestNameSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));

        return new ProjectInfo(assemblyName, ReadProperty("TargetFramework"), declaredTestProject || referencesTestPackage || namedLikeTests);
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
    /// whether an assembly is likely to contain a concrete DbContext: reads assembly metadata with
    /// <see cref="MetadataReader"/> and walks each concrete type's base chain up to
    /// <c>Microsoft.EntityFrameworkCore.DbContext</c>. A base that lives in another assembly is
    /// followed when that assembly sits next to the candidate (the usual build-output layout), and a
    /// generic base instantiation (a <c>TypeSpecification</c>) is followed through its generic
    /// definition. Returns false (never throws) for anything that can't be read as a valid .NET
    /// assembly.</summary>
    private static bool ProbablyContainsDbContext(string assemblyPath, DbContextMetadataCache cache)
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
            var resolve = cache.CreateResolver(assemblyPath);
            foreach (var typeDefHandle in reader.TypeDefinitions)
            {
                var typeDef = reader.GetTypeDefinition(typeDefHandle);
                // Abstract and open generic types are never selectable contexts (DbContextScanner skips them).
                if ((typeDef.Attributes & TypeAttributes.Abstract) != 0 || typeDef.GetGenericParameters().Count > 0)
                {
                    continue;
                }

                if (DerivesFromDbContext(reader, typeDef.BaseType, resolve, cache, depth: 0))
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

    private static bool DerivesFromDbContext(MetadataReader reader, EntityHandle baseTypeHandle, Func<string, string?> resolve, DbContextMetadataCache cache, int depth)
    {
        // Bound recursion: a legitimate DbContext base chain is a handful of types deep at most,
        // but a pathological/corrupt assembly could in principle reference a self-cycle.
        if (depth > 16 || baseTypeHandle.IsNil)
        {
            return false;
        }

        switch (baseTypeHandle.Kind)
        {
            case HandleKind.TypeDefinition:
                return DerivesFromDbContext(reader, reader.GetTypeDefinition((TypeDefinitionHandle)baseTypeHandle).BaseType, resolve, cache, depth + 1);
            case HandleKind.TypeSpecification:
                var genericDefinition = ReadGenericDefinition(reader, (TypeSpecificationHandle)baseTypeHandle);
                return !genericDefinition.IsNil && DerivesFromDbContext(reader, genericDefinition, resolve, cache, depth + 1);
            case HandleKind.TypeReference:
                var typeReference = reader.GetTypeReference((TypeReferenceHandle)baseTypeHandle);
                var ns = reader.GetString(typeReference.Namespace);
                var name = reader.GetString(typeReference.Name);
                if (ns == DbContextNamespace && name == DbContextName)
                {
                    return true;
                }

                if (typeReference.ResolutionScope.Kind != HandleKind.AssemblyReference)
                {
                    return false;
                }

                var referencedAssembly = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)typeReference.ResolutionScope).Name);
                return resolve(referencedAssembly) is { } referencedPath
                    && cache.DerivesFromDbContext(referencedPath, ns, name, resolve, depth + 1);
            default:
                return false;
        }
    }

    private static EntityHandle ReadGenericDefinition(MetadataReader reader, TypeSpecificationHandle handle)
    {
        var signature = reader.GetBlobReader(reader.GetTypeSpecification(handle).Signature);
        if (signature.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
        {
            return default;
        }

        // Class (0x12) or value type (0x11) marker, then the generic definition's TypeDefOrRef token.
        signature.ReadByte();
        return signature.ReadTypeHandle();
    }

    private const string DbContextNamespace = "Microsoft.EntityFrameworkCore";

    private const string DbContextName = "DbContext";

    /// <summary>Memoizes whether a named type in a referenced assembly derives from <c>DbContext</c>, so
    /// scanning many candidates that share dependencies stays cheap.</summary>
    private sealed class DbContextMetadataCache
    {
        private readonly Dictionary<(string Path, string Namespace, string Name), bool> _results = new();

        /// <summary>Resolves a referenced assembly's simple name to a file the way the loader would: the
        /// candidate's own folder and <c>.deps.json</c> (including NuGet package folders), then the shared
        /// frameworks. A class library's folder does not hold its NuGet dependencies, so the folder alone is not enough.</summary>
        public Func<string, string?> CreateResolver(string assemblyPath)
        {
            var directory = Path.GetDirectoryName(assemblyPath)!;
            var probe = new Lazy<TargetDependencyProbe?>(() =>
            {
                try
                {
                    return TargetDependencyProbe.Create(assemblyPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    return null;
                }
            });

            return simpleName =>
            {
                var beside = Path.Combine(directory, simpleName + ".dll");
                return File.Exists(beside) ? beside : probe.Value?.ResolveAssembly(new AssemblyName(simpleName));
            };
        }

        public bool DerivesFromDbContext(string assemblyPath, string ns, string name, Func<string, string?> resolve, int depth)
        {
            var key = (assemblyPath.ToUpperInvariant(), ns, name);
            if (_results.TryGetValue(key, out var known))
            {
                return known;
            }

            // Pre-seeded so a cyclic reference terminates instead of recursing.
            _results[key] = false;
            var result = Compute(assemblyPath, ns, name, resolve, depth);
            _results[key] = result;
            return result;
        }

        private bool Compute(string assemblyPath, string ns, string name, Func<string, string?> resolve, int depth)
        {
            if (depth > 16 || !File.Exists(assemblyPath))
            {
                return false;
            }

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
                    if (!typeDef.GetDeclaringType().IsNil
                        || reader.GetString(typeDef.Name) != name
                        || reader.GetString(typeDef.Namespace) != ns)
                    {
                        continue;
                    }

                    return AssemblyDiscoveryService.DerivesFromDbContext(reader, typeDef.BaseType, resolve, this, depth);
                }

                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                return false;
            }
        }
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
