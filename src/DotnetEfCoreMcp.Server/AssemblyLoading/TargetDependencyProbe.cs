using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DotnetEfCoreMcp.Server.AssemblyLoading;

/// <summary>Resolves a target assembly's dependencies the way the .NET host (<c>hostpolicy</c>)
/// does, for the cases <see cref="System.Runtime.Loader.AssemblyDependencyResolver"/> cannot cover.
///
/// <para><see cref="System.Runtime.Loader.AssemblyDependencyResolver"/> only resolves NuGet package
/// assets when the target's output folder carries probing-path information (a
/// <c>.runtimeconfig.dev.json</c>, or <c>additionalProbingPaths</c> baked into the runtime config).
/// A plain class library built with <c>dotnet build</c> has neither, and the SDK also does not copy
/// NuGet DLLs next to the library output unless <c>CopyLocalLockFileAssemblies</c> is set. The
/// result is an output folder holding only project-reference DLLs, where every <c>type: "package"</c>
/// entry in <c>.deps.json</c> resolves to <c>null</c> and hundreds of types fail to load.</para>
///
/// <para>This probe closes that gap by reading the target's <c>.deps.json</c> for the package
/// relative paths and combining them with the same probing roots <c>dotnet-ef</c> passes to
/// <c>dotnet exec --additionalprobingpath</c> (the <c>packageFolders</c> recorded by restore in
/// <c>obj/project.assets.json</c>, falling back to the conventional NuGet global-packages folder).
/// It additionally maps the frameworks declared in the target's <c>.runtimeconfig.json</c> (most
/// importantly <c>Microsoft.AspNetCore.App</c>, which is never present in <c>.deps.json</c>) onto
/// the installed shared-framework directories.</para></summary>
internal sealed class TargetDependencyProbe
{
    /// <summary>Placeholder asset name used by <c>.deps.json</c> to mean "this library contributes
    /// no file for this asset type"; it must never be treated as a real DLL path.</summary>
    private const string EmptyAssetPlaceholder = "_._";

    /// <summary>Managed assembly simple name (case-insensitive) to the candidate file paths that
    /// <c>.deps.json</c> says could provide it, in probing-root priority order.</summary>
    private readonly Dictionary<string, List<string>> _managedAssets;

    /// <summary>Native library name without extension to its candidate file paths.</summary>
    private readonly Dictionary<string, List<string>> _nativeAssets;

    /// <summary>Directories probed by simple file name for anything <see cref="_managedAssets"/>
    /// does not describe - the output folder itself plus the shared-framework directories selected
    /// from the target's runtime config. Shared-framework assemblies are deliberately absent from
    /// <c>.deps.json</c>, so a flat by-name probe is the only way to find them.</summary>
    private readonly List<string> _flatProbeDirectories;

    private TargetDependencyProbe(
        Dictionary<string, List<string>> managedAssets,
        Dictionary<string, List<string>> nativeAssets,
        List<string> flatProbeDirectories,
        IReadOnlyList<string> probingRoots,
        IReadOnlyList<string> diagnostics)
    {
        _managedAssets = managedAssets;
        _nativeAssets = nativeAssets;
        _flatProbeDirectories = flatProbeDirectories;
        ProbingRoots = probingRoots;
        Diagnostics = diagnostics;
    }

    /// <summary>The package probing roots this probe resolves <c>.deps.json</c> package paths
    /// against, in priority order. Exposed for diagnostics/troubleshooting.</summary>
    public IReadOnlyList<string> ProbingRoots { get; }

    /// <summary>Non-fatal problems encountered while building the probe (e.g. an unreadable
    /// <c>.deps.json</c>). Resolution degrades to whatever could be parsed rather than failing the
    /// whole load.</summary>
    public IReadOnlyList<string> Diagnostics { get; }

    public static TargetDependencyProbe Create(string mainAssemblyPath)
    {
        var appDirectory = Path.GetDirectoryName(Path.GetFullPath(mainAssemblyPath))!;
        var diagnostics = new List<string>();

        var probingRoots = BuildProbingRoots(appDirectory);
        var managedAssets = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var nativeAssets = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        var depsPath = Path.ChangeExtension(mainAssemblyPath, ".deps.json");
        if (File.Exists(depsPath))
        {
            try
            {
                ReadDependencyManifest(depsPath, appDirectory, probingRoots, managedAssets, nativeAssets);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                diagnostics.Add($"Could not read '{Path.GetFileName(depsPath)}': {ex.Message}");
            }
        }

        var flatProbeDirectories = new List<string> { appDirectory };
        flatProbeDirectories.AddRange(ResolveSharedFrameworkDirectories(mainAssemblyPath, diagnostics));

        return new TargetDependencyProbe(
            managedAssets, nativeAssets, flatProbeDirectories, probingRoots, diagnostics);
    }

    public string? ResolveAssembly(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { Length: > 0 } simpleName)
        {
            return null;
        }

        if (_managedAssets.TryGetValue(simpleName, out var candidates))
        {
            var match = candidates.FirstOrDefault(File.Exists);
            if (match is not null)
            {
                return match;
            }
        }

        return ProbeFlatDirectories(simpleName + ".dll");
    }

    public string? ResolveUnmanagedDll(string unmanagedDllName)
    {
        var lookupKey = Path.GetFileNameWithoutExtension(unmanagedDllName);
        if (_nativeAssets.TryGetValue(lookupKey, out var candidates))
        {
            var match = candidates.FirstOrDefault(File.Exists);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private string? ProbeFlatDirectories(string fileName)
    {
        foreach (var directory in _flatProbeDirectories)
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Collects the directories that NuGet package assets may live under, mirroring the
    /// <c>--additionalprobingpath</c> arguments <c>dotnet-ef</c> derives from the project's assets
    /// file. Restore records the folders it actually used, so preferring them over a hard-coded
    /// convention keeps custom <c>NUGET_PACKAGES</c>/fallback-folder setups working.</summary>
    private static List<string> BuildProbingRoots(string appDirectory)
    {
        var roots = new List<string>();

        AddRange(ReadRuntimeConfigProbingPaths(appDirectory));
        AddRange(ReadPackageFoldersFromAssetsFile(appDirectory));

        if (Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } nugetPackages)
        {
            Add(nugetPackages);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (userProfile.Length > 0)
        {
            Add(Path.Combine(userProfile, ".nuget", "packages"));
        }

        return roots;

        void AddRange(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                Add(path);
            }
        }

        void Add(string path)
        {
            string normalized;
            try
            {
                normalized = Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch (ArgumentException)
            {
                return;
            }

            if (Directory.Exists(normalized) && !roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(normalized);
            }
        }
    }

    /// <summary>Reads <c>additionalProbingPaths</c> from a <c>*.runtimeconfig.dev.json</c> next to
    /// the target, if the SDK produced one. Present for apps built to run, absent for the plain
    /// class-library builds this probe exists to support.</summary>
    private static IEnumerable<string> ReadRuntimeConfigProbingPaths(string appDirectory)
    {
        foreach (var devConfigPath in SafeEnumerateFiles(appDirectory, "*.runtimeconfig.dev.json"))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(File.ReadAllText(devConfigPath));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            using (document)
            {
                if (document.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions)
                    && runtimeOptions.TryGetProperty("additionalProbingPaths", out var paths)
                    && paths.ValueKind == JsonValueKind.Array)
                {
                    foreach (var path in paths.EnumerateArray())
                    {
                        if (path.GetString() is { Length: > 0 } value)
                        {
                            yield return value;
                        }
                    }
                }
            }
        }
    }

    /// <summary>Finds the project's <c>obj/project.assets.json</c> by walking up from the output
    /// folder (typically <c>&lt;project&gt;/bin/&lt;config&gt;/&lt;tfm&gt;</c>) and returns the
    /// <c>packageFolders</c> restore wrote into it.</summary>
    private static IEnumerable<string> ReadPackageFoldersFromAssetsFile(string appDirectory)
    {
        var assetsFile = FindAssetsFile(appDirectory);
        if (assetsFile is null)
        {
            yield break;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(assetsFile));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using (document)
        {
            if (document.RootElement.TryGetProperty("packageFolders", out var packageFolders)
                && packageFolders.ValueKind == JsonValueKind.Object)
            {
                foreach (var folder in packageFolders.EnumerateObject())
                {
                    yield return folder.Name;
                }
            }
        }
    }

    private static string? FindAssetsFile(string appDirectory)
    {
        // bin/<config>/<tfm>[/<rid>] means the project directory is up to four levels up; a couple
        // of extra levels cost nothing and tolerate custom output layouts.
        var directory = new DirectoryInfo(appDirectory);
        for (var depth = 0; depth < 6 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "obj", "project.assets.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void ReadDependencyManifest(
        string depsPath,
        string appDirectory,
        IReadOnlyList<string> probingRoots,
        Dictionary<string, List<string>> managedAssets,
        Dictionary<string, List<string>> nativeAssets)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(depsPath));
        var root = document.RootElement;

        if (!root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var runtimeTargetName = root.TryGetProperty("runtimeTarget", out var runtimeTarget)
            && runtimeTarget.TryGetProperty("name", out var runtimeTargetNameElement)
                ? runtimeTargetNameElement.GetString()
                : null;

        JsonElement targetLibraries = default;
        var foundTarget = false;
        if (runtimeTargetName is not null && targets.TryGetProperty(runtimeTargetName, out targetLibraries))
        {
            foundTarget = true;
        }
        else
        {
            // Without a runtimeTarget hint, the RID-qualified target (if any) is the runtime graph;
            // otherwise there is only one target to choose from.
            foreach (var candidate in targets.EnumerateObject())
            {
                targetLibraries = candidate.Value;
                foundTarget = true;
                if (candidate.Name.Contains('/'))
                {
                    break;
                }
            }
        }

        if (!foundTarget || targetLibraries.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        root.TryGetProperty("libraries", out var libraries);
        var ridCandidates = BuildRuntimeIdentifierCandidates();

        foreach (var library in targetLibraries.EnumerateObject())
        {
            var (libraryType, libraryPath) = ReadLibraryMetadata(libraries, library.Name);

            if (library.Value.TryGetProperty("runtime", out var runtimeAssets)
                && runtimeAssets.ValueKind == JsonValueKind.Object)
            {
                foreach (var asset in runtimeAssets.EnumerateObject())
                {
                    AddAsset(managedAssets, asset.Name, libraryType, libraryPath, appDirectory, probingRoots);
                }
            }

            if (library.Value.TryGetProperty("native", out var nativeLibraryAssets)
                && nativeLibraryAssets.ValueKind == JsonValueKind.Object)
            {
                foreach (var asset in nativeLibraryAssets.EnumerateObject())
                {
                    AddAsset(nativeAssets, asset.Name, libraryType, libraryPath, appDirectory, probingRoots);
                }
            }

            if (library.Value.TryGetProperty("runtimeTargets", out var ridAssets)
                && ridAssets.ValueKind == JsonValueKind.Object)
            {
                foreach (var asset in ridAssets.EnumerateObject())
                {
                    var rid = asset.Value.TryGetProperty("rid", out var ridElement) ? ridElement.GetString() : null;
                    if (rid is not null && !ridCandidates.Contains(rid))
                    {
                        continue;
                    }

                    var assetType = asset.Value.TryGetProperty("assetType", out var assetTypeElement)
                        ? assetTypeElement.GetString()
                        : "runtime";
                    var destination = string.Equals(assetType, "native", StringComparison.OrdinalIgnoreCase)
                        ? nativeAssets
                        : managedAssets;

                    AddAsset(destination, asset.Name, libraryType, libraryPath, appDirectory, probingRoots);
                }
            }
        }
    }

    private static (string? Type, string? Path) ReadLibraryMetadata(JsonElement libraries, string libraryKey)
    {
        if (libraries.ValueKind != JsonValueKind.Object || !libraries.TryGetProperty(libraryKey, out var library))
        {
            return (null, null);
        }

        var type = library.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        var path = library.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;

        // Restore omits "path" when it matches the lowercased "name/version" key.
        return (type, path ?? (string.Equals(type, "package", StringComparison.OrdinalIgnoreCase)
            ? libraryKey.ToLowerInvariant()
            : null));
    }

    private static void AddAsset(
        Dictionary<string, List<string>> assets,
        string relativeAssetPath,
        string? libraryType,
        string? libraryPath,
        string appDirectory,
        IReadOnlyList<string> probingRoots)
    {
        var fileName = Path.GetFileName(relativeAssetPath);
        if (fileName.Length == 0 || fileName == EmptyAssetPlaceholder)
        {
            return;
        }

        var key = Path.GetFileNameWithoutExtension(fileName);
        if (!assets.TryGetValue(key, out var candidates))
        {
            candidates = [];
            assets[key] = candidates;
        }

        // Project references and anything the SDK copied locally sit directly in the output folder;
        // it wins so a freshly rebuilt output is preferred over a stale cached copy.
        AddCandidate(candidates, Path.Combine(appDirectory, fileName));

        if (!string.Equals(libraryType, "package", StringComparison.OrdinalIgnoreCase) || libraryPath is null)
        {
            return;
        }

        var nativeRelativePath = relativeAssetPath.Replace('/', Path.DirectorySeparatorChar);
        var nativeLibraryPath = libraryPath.Replace('/', Path.DirectorySeparatorChar);
        foreach (var root in probingRoots)
        {
            AddCandidate(candidates, Path.Combine(root, nativeLibraryPath, nativeRelativePath));
        }
    }

    private static void AddCandidate(List<string> candidates, string candidate)
    {
        if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(candidate);
        }
    }

    private static HashSet<string> BuildRuntimeIdentifierCandidates()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "any", "base" };

        var rid = RuntimeInformation.RuntimeIdentifier;
        candidates.Add(rid);

        // A crude RID-graph walk: "win-x64" also matches "win", and the architecture-less/portable
        // forms packages commonly publish. Full RID-graph fidelity is hostpolicy's job; getting the
        // common desktop/CI cases right is enough for design-time reflection.
        var dashIndex = rid.IndexOf('-');
        if (dashIndex > 0)
        {
            candidates.Add(rid[..dashIndex]);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            candidates.Add("win");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            candidates.Add("osx");
            candidates.Add("unix");
        }
        else
        {
            candidates.Add("linux");
            candidates.Add("unix");
        }

        candidates.Add($"{(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" : "unix")}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}");
        return candidates;
    }

    /// <summary>Maps the frameworks the target was built against onto installed shared-framework
    /// directories. This is what makes an ASP.NET Core-referencing target loadable from a plain
    /// console host: <c>Microsoft.AspNetCore.App</c> assemblies appear in neither the output folder
    /// nor <c>.deps.json</c>.
    ///
    /// <para><c>Microsoft.NETCore.App</c> is deliberately excluded - the process has already picked
    /// its own runtime, and probing a second copy of <c>System.*</c> into the isolated context would
    /// create type-identity conflicts rather than fix anything.</para></summary>
    private static List<string> ResolveSharedFrameworkDirectories(string mainAssemblyPath, List<string> diagnostics)
    {
        var directories = new List<string>();
        var sharedRoots = GetSharedFrameworkRoots();

        foreach (var (name, version) in ReadFrameworkReferences(mainAssemblyPath, diagnostics))
        {
            if (string.Equals(name, "Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var resolved = sharedRoots
                .Select(root => SelectFrameworkVersionDirectory(Path.Combine(root, name), version))
                .FirstOrDefault(directory => directory is not null);

            if (resolved is not null && !directories.Contains(resolved, StringComparer.OrdinalIgnoreCase))
            {
                directories.Add(resolved);
            }
            else if (resolved is null)
            {
                diagnostics.Add(
                    $"The target requires shared framework '{name}' {version}, but no matching " +
                    "installation was found; types from it will fail to load.");
            }
        }

        return directories;
    }

    /// <summary>Reads the target's framework references, preferring its <c>.runtimeconfig.json</c>.
    /// A class library does not get one, so the restore graph is used as a fallback - without it an
    /// ASP.NET Core-referencing library would appear to need no shared framework at all.</summary>
    private static IEnumerable<(string Name, string Version)> ReadFrameworkReferences(
        string mainAssemblyPath,
        List<string> diagnostics)
    {
        var fromRuntimeConfig = ReadRuntimeConfigFrameworkReferences(mainAssemblyPath, diagnostics).ToList();
        return fromRuntimeConfig.Count > 0
            ? fromRuntimeConfig
            : ReadAssetsFileFrameworkReferences(Path.GetDirectoryName(mainAssemblyPath)!, diagnostics);
    }

    private static IEnumerable<(string Name, string Version)> ReadRuntimeConfigFrameworkReferences(
        string mainAssemblyPath,
        List<string> diagnostics)
    {
        var runtimeConfigPath = Path.ChangeExtension(mainAssemblyPath, ".runtimeconfig.json");
        if (!File.Exists(runtimeConfigPath))
        {
            yield break;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add($"Could not read '{Path.GetFileName(runtimeConfigPath)}': {ex.Message}");
            yield break;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions))
            {
                yield break;
            }

            foreach (var reference in EnumerateFrameworkReferences(runtimeOptions))
            {
                yield return reference;
            }
        }
    }

    /// <summary>Reads <c>project.frameworks.&lt;tfm&gt;.frameworkReferences</c> from the restore
    /// assets file. The assets file records no version for a framework reference, so the target
    /// framework moniker it sits under supplies one for roll-forward.</summary>
    private static IEnumerable<(string Name, string Version)> ReadAssetsFileFrameworkReferences(
        string appDirectory,
        List<string> diagnostics)
    {
        var assetsPath = FindAssetsFile(appDirectory);
        if (assetsPath is null)
        {
            yield break;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(assetsPath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add($"Could not read '{Path.GetFileName(assetsPath)}': {ex.Message}");
            yield break;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("project", out var project)
                || !project.TryGetProperty("frameworks", out var frameworks)
                || frameworks.ValueKind != JsonValueKind.Object)
            {
                yield break;
            }

            foreach (var framework in frameworks.EnumerateObject())
            {
                if (!framework.Value.TryGetProperty("frameworkReferences", out var references)
                    || references.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var version = ParseTargetFrameworkVersion(framework.Name);
                foreach (var reference in references.EnumerateObject())
                {
                    yield return (reference.Name, version);
                }
            }
        }
    }

    /// <summary>Turns a target framework moniker such as <c>net10.0</c> into the <c>10.0.0</c>
    /// baseline version used for shared-framework roll-forward.</summary>
    private static string ParseTargetFrameworkVersion(string targetFrameworkMoniker)
    {
        var digits = targetFrameworkMoniker.AsSpan().TrimStart("net");
        var end = 0;
        while (end < digits.Length && (char.IsAsciiDigit(digits[end]) || digits[end] == '.'))
        {
            end++;
        }

        return ParseVersion(digits[..end].ToString()) is { } parsed
            ? parsed.ToString()
            : "0.0.0";
    }

    private static IEnumerable<(string Name, string Version)> EnumerateFrameworkReferences(JsonElement runtimeOptions)
    {
        if (runtimeOptions.TryGetProperty("framework", out var single) && single.ValueKind == JsonValueKind.Object)
        {
            if (TryReadFrameworkReference(single) is { } reference)
            {
                yield return reference;
            }
        }

        if (runtimeOptions.TryGetProperty("frameworks", out var many) && many.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in many.EnumerateArray())
            {
                if (TryReadFrameworkReference(element) is { } reference)
                {
                    yield return reference;
                }
            }
        }
    }

    private static (string Name, string Version)? TryReadFrameworkReference(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("name", out var nameElement)
            || nameElement.GetString() is not { Length: > 0 } name)
        {
            return null;
        }

        var version = element.TryGetProperty("version", out var versionElement)
            ? versionElement.GetString() ?? "0.0.0"
            : "0.0.0";
        return (name, version);
    }

    private static List<string> GetSharedFrameworkRoots()
    {
        var roots = new List<string>();

        // System.Private.CoreLib lives in <dotnet-root>/shared/Microsoft.NETCore.App/<version>, so
        // two levels up from it is the shared-framework root actually hosting this process - more
        // reliable than any environment variable.
        var coreLibDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        Add(Path.GetDirectoryName(Path.GetDirectoryName(coreLibDirectory)));

        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } dotnetRoot)
        {
            Add(Path.Combine(dotnetRoot, "shared"));
        }

        return roots;

        void Add(string? path)
        {
            if (path is null || !Directory.Exists(path))
            {
                return;
            }

            var full = Path.GetFullPath(path);
            if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(full);
            }
        }
    }

    /// <summary>Picks the best installed version directory for a shared framework, applying
    /// roll-forward semantics: a target built against 10.0.0 must still bind to an installed 10.0.11
    /// patch, because exact-match would find nothing.</summary>
    private static string? SelectFrameworkVersionDirectory(string frameworkDirectory, string requestedVersion)
    {
        if (!Directory.Exists(frameworkDirectory))
        {
            return null;
        }

        var installed = SafeEnumerateDirectories(frameworkDirectory)
            .Select(directory => (Directory: directory, Version: ParseVersion(Path.GetFileName(directory))))
            .Where(entry => entry.Version is not null)
            .Select(entry => (entry.Directory, Version: entry.Version!))
            .OrderBy(entry => entry.Version)
            .ToList();

        if (installed.Count == 0)
        {
            return null;
        }

        var requested = ParseVersion(requestedVersion) ?? new Version(0, 0, 0);

        return (installed.FirstOrDefault(entry => entry.Version.Major == requested.Major && entry.Version >= requested)
            .Directory
            ?? installed.FirstOrDefault(entry => entry.Version >= requested).Directory)
            ?? installed[^1].Directory;
    }

    private static Version? ParseVersion(string? value)
    {
        if (value is null)
        {
            return null;
        }

        // Strip any prerelease/build suffix ("10.0.0-preview.1.25080.5") that Version cannot parse.
        var dashIndex = value.IndexOf('-');
        var numeric = dashIndex >= 0 ? value[..dashIndex] : value;
        return Version.TryParse(numeric, out var parsed) ? parsed : null;
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
