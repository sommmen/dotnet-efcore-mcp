using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetEfCoreMcp.Server.AssemblyLoading;

namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Produces the <c>--runtimeconfig</c> file <c>dotnet exec</c> needs to launch the query
/// host under the target application's runtime.
///
/// <para>The obvious source is the target's own adjacent <c>&lt;target&gt;.runtimeconfig.json</c>,
/// but plenty of perfectly valid targets do not have one: the SDK only emits a runtime config for
/// projects that are built to run, and a <c>DbContext</c> frequently lives in a class library, in a
/// project-reference copy inside some host app's output folder, or - worst case - in a NuGet package
/// assembly copied into a consuming app's <c>bin</c>. Requiring an adjacent runtime config forced
/// those targets onto <see cref="QueryExecutionMode.InProcess"/>, which is exactly the mode the
/// out-of-process host exists to avoid.</para>
///
/// <para>This resolver keeps the adjacent file as the preferred answer and otherwise synthesizes an
/// equivalent one from the frameworks the target was actually built against (its restore graph, or
/// the runtime configs of the application that owns the output folder it was copied into). The
/// synthesized file is cached in the temp folder under a content hash so repeated queries - and the
/// long-lived pooled workers - reuse the same file instead of churning new ones.</para></summary>
internal static class TargetRuntimeConfigResolver
{
    private const string DefaultFrameworkName = "Microsoft.NETCore.App";

    /// <summary>Returns the path to a runtime configuration file describing the framework the
    /// <paramref name="targetAssemblyPath"/> target runs on, synthesizing one when the target does
    /// not ship its own.</summary>
    /// <exception cref="QueryExecutionException">A runtime config could neither be found nor
    /// written.</exception>
    public static string Resolve(string targetAssemblyPath)
    {
        var adjacent = Path.ChangeExtension(targetAssemblyPath, ".runtimeconfig.json");
        if (File.Exists(adjacent) && DeclaresSharedFramework(adjacent))
        {
            return adjacent;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(targetAssemblyPath))!;
        var frameworks = TargetDependencyProbe.GetFrameworkReferences(targetAssemblyPath);
        if (frameworks.Count == 0)
        {
            frameworks = ReadNeighborAppFrameworks(directory, adjacent);
        }

        var targetFrameworkMoniker = ResolveTargetFrameworkMoniker(targetAssemblyPath, directory, adjacent);
        if (frameworks.Count == 0)
        {
            frameworks = [(DefaultFrameworkName, ToFrameworkVersion(targetFrameworkMoniker))];
        }

        return WriteSynthesizedRuntimeConfig(targetAssemblyPath, targetFrameworkMoniker, frameworks);
    }

    /// <summary>Whether a runtime config selects a shared framework the way <c>dotnet exec</c>
    /// requires. A self-contained app's config instead records <c>includedFrameworks</c> and
    /// carries no usable framework reference, so it is rejected in favour of synthesis.</summary>
    private static bool DeclaresSharedFramework(string runtimeConfigPath) =>
        TryReadRuntimeOptions(runtimeConfigPath, out var runtimeOptions)
        && (runtimeOptions.TryGetProperty("framework", out _) || runtimeOptions.TryGetProperty("frameworks", out _));

    /// <summary>Unions the frameworks declared by the application runtime configs sitting in the
    /// target's own output folder. This is the backstop for a target that is a dependency copy -
    /// a project reference or NuGet package assembly copied into some app's output - where the
    /// owning app's runtime config is the only remaining description of the runtime the folder was
    /// built for.</summary>
    private static List<(string Name, string Version)> ReadNeighborAppFrameworks(string directory, string excludedPath)
    {
        var frameworks = new List<(string Name, string Version)>();

        foreach (var candidate in SafeEnumerateRuntimeConfigs(directory))
        {
            if (string.Equals(candidate, excludedPath, StringComparison.OrdinalIgnoreCase)
                || candidate.EndsWith(".runtimeconfig.dev.json", StringComparison.OrdinalIgnoreCase)
                || !TryReadRuntimeOptions(candidate, out var runtimeOptions))
            {
                continue;
            }

            foreach (var reference in EnumerateFrameworkReferences(runtimeOptions))
            {
                AddFramework(frameworks, reference);
            }
        }

        return frameworks;
    }

    /// <summary>Records a framework reference, keeping the highest version when the same framework
    /// is declared more than once across the sources consulted.</summary>
    private static void AddFramework(List<(string Name, string Version)> frameworks, (string Name, string Version) reference)
    {
        var existing = frameworks.FindIndex(entry => string.Equals(entry.Name, reference.Name, StringComparison.OrdinalIgnoreCase));
        if (existing < 0)
        {
            frameworks.Add(reference);
            return;
        }

        if (CompareVersions(reference.Version, frameworks[existing].Version) > 0)
        {
            frameworks[existing] = reference;
        }
    }

    private static int CompareVersions(string left, string right) =>
        (Version.TryParse(StripSuffix(left), out var parsedLeft) ? parsedLeft : new Version(0, 0, 0))
            .CompareTo(Version.TryParse(StripSuffix(right), out var parsedRight) ? parsedRight : new Version(0, 0, 0));

    private static string StripSuffix(string version)
    {
        var dashIndex = version.IndexOf('-');
        return dashIndex >= 0 ? version[..dashIndex] : version;
    }

    /// <summary>Determines the target framework moniker the target was built for, preferring the
    /// <c>runtimeTarget</c> its own <c>.deps.json</c> records, then any neighbouring app runtime
    /// config, and finally the runtime this server is itself running on.</summary>
    private static string ResolveTargetFrameworkMoniker(string targetAssemblyPath, string directory, string excludedPath)
    {
        if (ReadRuntimeTargetMoniker(Path.ChangeExtension(targetAssemblyPath, ".deps.json")) is { } fromOwnDeps)
        {
            return fromOwnDeps;
        }

        foreach (var candidate in SafeEnumerateRuntimeConfigs(directory))
        {
            if (string.Equals(candidate, excludedPath, StringComparison.OrdinalIgnoreCase)
                || !TryReadRuntimeOptions(candidate, out var runtimeOptions))
            {
                continue;
            }

            if (runtimeOptions.TryGetProperty("tfm", out var tfm) && tfm.GetString() is { Length: > 0 } moniker)
            {
                return moniker;
            }
        }

        var runtime = Environment.Version;
        return string.Create(CultureInfo.InvariantCulture, $"net{runtime.Major}.{runtime.Minor}");
    }

    /// <summary>Turns a <c>.deps.json</c> <c>runtimeTarget.name</c> such as
    /// <c>.NETCoreApp,Version=v10.0</c> into the <c>net10.0</c> moniker form.</summary>
    private static string? ReadRuntimeTargetMoniker(string depsPath)
    {
        if (!File.Exists(depsPath) || !TryParseJson(depsPath, out var document))
        {
            return null;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("runtimeTarget", out var runtimeTarget)
                || !runtimeTarget.TryGetProperty("name", out var nameElement)
                || nameElement.GetString() is not { Length: > 0 } name)
            {
                return null;
            }

            const string versionMarker = "Version=v";
            var versionIndex = name.IndexOf(versionMarker, StringComparison.OrdinalIgnoreCase);
            if (!name.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase) || versionIndex < 0)
            {
                return null;
            }

            var version = name[(versionIndex + versionMarker.Length)..];
            var end = 0;
            while (end < version.Length && (char.IsAsciiDigit(version[end]) || version[end] == '.'))
            {
                end++;
            }

            return end > 0 ? $"net{version[..end]}" : null;
        }
    }

    /// <summary>Turns a target framework moniker such as <c>net10.0</c> into the <c>10.0.0</c>
    /// baseline version a framework reference needs.</summary>
    private static string ToFrameworkVersion(string targetFrameworkMoniker)
    {
        var digits = targetFrameworkMoniker.AsSpan().TrimStart("net");
        var end = 0;
        while (end < digits.Length && (char.IsAsciiDigit(digits[end]) || digits[end] == '.'))
        {
            end++;
        }

        return Version.TryParse(digits[..end].ToString(), out var parsed)
            ? new Version(parsed.Major, Math.Max(parsed.Minor, 0), Math.Max(parsed.Build, 0)).ToString()
            : "0.0.0";
    }

    /// <summary>Writes (or reuses) the synthesized runtime config. The file name is derived from a
    /// hash of its own contents plus the target path, so concurrent servers, pooled workers and
    /// repeated queries all converge on one stable file instead of racing over a shared name.</summary>
    private static string WriteSynthesizedRuntimeConfig(
        string targetAssemblyPath,
        string targetFrameworkMoniker,
        IReadOnlyList<(string Name, string Version)> frameworks)
    {
        var content = BuildRuntimeConfigJson(targetFrameworkMoniker, frameworks);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(targetAssemblyPath).ToUpperInvariant() + "\n" + content)))[..16].ToLowerInvariant();

        var directory = Path.Combine(Path.GetTempPath(), "dotnet-efcore-mcp", "runtimeconfig", hash);
        var path = Path.Combine(directory, Path.GetFileNameWithoutExtension(targetAssemblyPath) + ".runtimeconfig.json");

        try
        {
            if (File.Exists(path))
            {
                return path;
            }

            Directory.CreateDirectory(directory);

            // Write through a unique temp file so a concurrent writer never exposes a partial
            // config to a query host that is starting up at the same moment.
            var staging = Path.Combine(directory, Path.GetRandomFileName());
            File.WriteAllText(staging, content);
            File.Move(staging, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A racing writer that won the move already produced the file we need.
            if (File.Exists(path))
            {
                return path;
            }

            throw new QueryExecutionException(
                "The target application does not provide a runtime configuration file and one could not be " +
                $"synthesized for out-of-process query execution: {ex.Message}", ex);
        }
    }

    /// <summary>Pads a framework version to the three-part <c>major.minor.patch</c> form the host
    /// requires. The restore graph fallback supplies a two-part version derived from the target
    /// framework moniker (<c>net10.0</c> → <c>10.0</c>), which <c>hostfxr</c> rejects outright.</summary>
    private static string NormalizeFrameworkVersion(string version)
    {
        var suffixIndex = version.IndexOf('-');
        var numeric = suffixIndex >= 0 ? version[..suffixIndex] : version;
        var suffix = suffixIndex >= 0 ? version[suffixIndex..] : string.Empty;

        return Version.TryParse(numeric, out var parsed)
            ? new Version(parsed.Major, Math.Max(parsed.Minor, 0), Math.Max(parsed.Build, 0)) + suffix
            : version;
    }

    private static string BuildRuntimeConfigJson(string targetFrameworkMoniker, IReadOnlyList<(string Name, string Version)> frameworks)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("runtimeOptions");
            writer.WriteString("tfm", targetFrameworkMoniker);

            // The target was only ever built, never published against a pinned runtime, so roll
            // forward to whatever patch/minor release is actually installed rather than failing.
            writer.WriteString("rollForward", "latestMinor");
            writer.WriteStartArray("frameworks");
            foreach (var (name, version) in frameworks.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteString("version", NormalizeFrameworkVersion(version));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static IEnumerable<(string Name, string Version)> EnumerateFrameworkReferences(JsonElement runtimeOptions)
    {
        if (runtimeOptions.TryGetProperty("framework", out var single)
            && TryReadFrameworkReference(single) is { } singleReference)
        {
            yield return singleReference;
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

        return (name, element.TryGetProperty("version", out var versionElement)
            ? versionElement.GetString() ?? "0.0.0"
            : "0.0.0");
    }

    private static bool TryReadRuntimeOptions(string runtimeConfigPath, out JsonElement runtimeOptions)
    {
        runtimeOptions = default;
        if (!TryParseJson(runtimeConfigPath, out var document))
        {
            return false;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("runtimeOptions", out var options))
            {
                return false;
            }

            // The document is disposed with the pooled buffer it was parsed from, so hand back a
            // detached clone the caller can keep reading.
            runtimeOptions = options.Clone();
            return true;
        }
    }

    private static bool TryParseJson(string path, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            document = null!;
            return false;
        }
    }

    private static IEnumerable<string> SafeEnumerateRuntimeConfigs(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.runtimeconfig.json").Order(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }
}
