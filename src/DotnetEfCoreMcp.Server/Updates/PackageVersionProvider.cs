using System.Reflection;

namespace DotnetEfCoreMcp.Server.Updates;

/// <summary>
/// Reads the NuGet package version this server was built as.
/// </summary>
/// <remarks>
/// Two consumers need this for different reasons, which is why it lives here rather than inside
/// either of them: <c>hooks install</c> pins installed hooks to an exact version, and the update
/// check compares it against the feed. Both need the real <c>$(PackageVersion)</c> rather than the
/// assembly version.
/// </remarks>
public static class PackageVersionProvider
{
    /// <summary>
    /// Assembly metadata key holding the build's <c>$(PackageVersion)</c>, written by the
    /// <c>EmbedPackageVersionMetadata</c> target in the server's project file.
    /// </summary>
    internal const string MetadataKey = "NuGetPackageVersion";

    /// <summary>
    /// Returns the NuGet package version of the running build.
    /// </summary>
    /// <returns>
    /// The package version, or <see langword="null"/> when the build carries no packaging metadata
    /// (a local F5, for instance). Callers must treat null as "unknown" and degrade rather than
    /// guess: an unpinned hook command, or a skipped update check.
    /// </returns>
    public static string? GetPackageVersion() => GetPackageVersion(typeof(PackageVersionProvider).Assembly);

    /// <summary>
    /// Returns the NuGet package version recorded in <paramref name="assembly"/>.
    /// </summary>
    /// <param name="assembly">The assembly to read version metadata from.</param>
    internal static string? GetPackageVersion(Assembly assembly)
    {
        // The build embeds the real $(PackageVersion); prefer it, because the informational version
        // does not reproduce Nerdbank.GitVersioning's package-version suffix verbatim.
        var packageVersion = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == MetadataKey)?.Value;

        if (!string.IsNullOrWhiteSpace(packageVersion))
        {
            return packageVersion;
        }

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return null;
        }

        // Strip the "+<commit>" build metadata, which is not part of a NuGet package version.
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? informational[..plus] : informational;
    }
}
