using System.Reflection;
using System.Runtime.Loader;

namespace DotnetEfCoreMcp.Server.AssemblyLoading;

/// <summary>Resolves a <see cref="SharedFrameworkAssemblyNames"/> entry to the copy already
/// available in the default <see cref="AssemblyLoadContext"/>, deliberately ignoring the version
/// the target was compiled against.
///
/// <para>Both isolated load contexts in this server want the same thing for these assemblies:
/// <em>one</em> copy, so that <c>typeof(DbContext).IsAssignableFrom(...)</c> and
/// <c>DbContextOptions&lt;TContext&gt;</c> keep working across the isolation boundary. The obvious
/// way to express that from <c>AssemblyLoadContext.Load</c> is to return <c>null</c>, which hands
/// resolution back to the default context. That is subtly wrong: the CLR binder satisfies a
/// reference with an equal-or-<em>higher</em> version but never with a lower one, so as soon as the
/// target project references a newer EF Core than the server restored - even by a single patch,
/// which is the normal state of affairs for any target that updated first - the bind fails with
/// <see cref="FileNotFoundException"/>. Every type whose base chain reaches <c>DbContext</c> then
/// disappears from <c>Assembly.GetTypes()</c> and discovery reports "no DbContext found" for an
/// assembly that plainly has one.</para>
///
/// <para>Resolving by <em>simple name</em> sidesteps patch and minor-version checks, returning
/// the server's copy when the target requests the same major version. A patch/minor gap within the
/// same major version is binary-compatible by EF Core's own versioning policy, so the shared copy
/// is a safe stand-in; the gap is still reported as a load warning so a genuine incompatibility is
/// attributable instead of mysterious.</para>
///
/// <para>A <em>major</em>-version gap is where that reasoning stops: EF Core guarantees binary
/// compatibility only within a major version, so neither copy can safely stand in for the other.
/// That case throws <see cref="AssemblyLoadFailedException"/> naming both versions and the remedy.
/// Declining <em>silently</em> would be worse than throwing - the caller would fall back to the
/// target's own copy, splitting <c>DbContext</c>'s type identity and reproducing the very "no
/// DbContext found" symptom this type exists to fix.</para></summary>
internal static class SharedAssemblyResolver
{
    // Resolution happens on the assembly-loading hot path - once per reference per load context,
    // and a query's compiled assembly gets its own context per request - so the lookup is memoized
    // by simple name. Caching is safe because the default context never unloads an assembly, so a
    // simple name that resolved once resolves to the same instance forever. A null entry (a name
    // the server does not ship) is cached too, since that is the expensive case: it costs a failed
    // bind attempt every time. ConcurrentDictionary rejects null *keys*, not null values, so a
    // reference-typed value can legitimately be null.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Assembly?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Attempts to resolve <paramref name="requestedName"/> from the default load context
    /// by simple name.</summary>
    /// <param name="requestedName">The assembly reference recorded in the target's metadata,
    /// including the version it was compiled against.</param>
    /// <param name="assembly">The default context's copy, when one exists.</param>
    /// <param name="versionGapWarning">A human-readable warning when the resolved copy is
    /// <em>older</em> than the version the target requested, otherwise <c>null</c>. The assembly is
    /// still returned in that case - sharing a slightly older copy is what keeps type identity
    /// intact, and is far better than failing the bind outright.</param>
    /// <returns><c>true</c> when the default context can supply the assembly; <c>false</c> when the
    /// server ships no copy of that name at all, which lets callers fall back to the target's own
    /// copy.</returns>
    /// <exception cref="AssemblyLoadFailedException">The server's copy differs from the requested
    /// one by major version, which is outside the compatibility this unification can honestly
    /// provide.</exception>
    public static bool TryResolveFromDefaultContext(
        AssemblyName requestedName,
        out Assembly? assembly,
        out string? versionGapWarning)
    {
        assembly = null;
        versionGapWarning = null;

        if (requestedName.Name is not { } simpleName)
        {
            return false;
        }

        var resolved = Cache.GetOrAdd(
            simpleName,
            static name => FindAlreadyLoaded(name) ?? TryLoadBySimpleName(name));
        if (resolved is null)
        {
            return false;
        }

        var resolvedVersion = resolved.GetName().Version;
        if (requestedName.Version is { } requestedVersion && resolvedVersion is not null)
        {
            // A major-version gap is outside what simple-name unification can honestly paper over:
            // EF Core only guarantees binary compatibility within a major, so sharing across one
            // would trade a clear failure for a later MissingMethodException/TypeLoadException from
            // deep inside EF Core. Declining *silently* would be worse still - the caller would
            // load the target's own copy, splitting DbContext's type identity and reproducing the
            // very "no DbContext found" symptom this type exists to fix (#115). So fail loudly,
            // naming both versions and the remedy.
            if (requestedVersion.Major != resolvedVersion.Major)
            {
                throw new AssemblyLoadFailedException(
                    $"The target was compiled against '{simpleName}' {requestedVersion}, but this MCP server ships " +
                    $"{resolvedVersion} - a different major version. These assemblies must share a single copy for " +
                    "DbContext and entity types to be comparable across the load-context boundary, and EF Core only " +
                    "guarantees binary compatibility within one major version, so neither copy can safely stand in " +
                    "for the other. Use a server build that targets the same major version as the target project, or " +
                    "align the target with this server's.");
            }

            if (resolvedVersion < requestedVersion)
            {
                versionGapWarning =
                    $"The target was compiled against '{simpleName}' {requestedVersion} but this MCP server ships " +
                    $"{resolvedVersion}. The server's copy is being shared anyway, because a single copy is what keeps " +
                    "DbContext and entity types comparable across the load-context boundary. This is normally harmless " +
                    "within one major version; if a query or model fails with a MissingMethodException or " +
                    "TypeLoadException naming this assembly, the version gap is the likely cause - upgrade the MCP " +
                    "server, or pin the target to the server's version.";
            }
        }

        assembly = resolved;
        return true;
    }

    private static Assembly? FindAlreadyLoaded(string simpleName) =>
        AssemblyLoadContext.Default.Assemblies.FirstOrDefault(candidate =>
            string.Equals(candidate.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));

    private static Assembly? TryLoadBySimpleName(string simpleName)
    {
        try
        {
            // A name carrying no version/public key is matched on simple name alone, so this
            // cannot fail over a version gap the way forwarding the target's own reference does.
            return AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(simpleName));
        }
        catch (Exception exception) when (exception
            is FileNotFoundException
            or FileLoadException
            or BadImageFormatException)
        {
            // The server genuinely has no copy - e.g. an ASP.NET Core shared-framework assembly
            // that this console host does not reference. Callers fall back to the target's own
            // copy rather than letting the whole type graph fail to load.
            return null;
        }
    }
}
