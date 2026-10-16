using NuGet.Versioning;

namespace DotnetEfCoreMcp.Server.Updates;

/// <summary>
/// Decides which published version counts as "newer" than the running one.
/// </summary>
/// <remarks>
/// <para>
/// This is the highest-risk logic in the update check, because this package publishes preview
/// builds only. A naive "compare against the latest stable" implementation fails two ways for a
/// preview user, and both are worse than having no update check at all:
/// </para>
/// <list type="number">
/// <item>
/// They are told they are up to date forever, because no stable release exists to compare against.
/// </item>
/// <item>
/// They are nagged to "upgrade" to something older than what they have, because SemVer sorts
/// <c>0.2.0-preview</c> <em>below</em> <c>0.2.0</c> - so a stable 0.1.0 outranks a preview 0.2.0
/// under a stable-only comparison that forgets to re-check the ordering.
/// </item>
/// </list>
/// <para>
/// The fix, following Vue CLI's <c>includePrerelease = !!semver.prerelease(local)</c>, is to let the
/// installed version pick its own channel: a preview user is compared against previews, a stable
/// user against stable releases only.
/// </para>
/// </remarks>
public static class VersionChannel
{
    /// <summary>How often to re-check for updates on a prerelease build.</summary>
    /// <remarks>
    /// Matches npm's prerelease cadence. Previews ship far more often than stable releases, so a
    /// weekly check would usually report stale information.
    /// </remarks>
    public static readonly TimeSpan PrereleaseInterval = TimeSpan.FromHours(24);

    /// <summary>How often to re-check for updates on a stable build.</summary>
    public static readonly TimeSpan StableInterval = TimeSpan.FromDays(7);

    /// <summary>
    /// Reports whether <paramref name="version"/> is a prerelease.
    /// </summary>
    /// <param name="version">A NuGet version string.</param>
    /// <returns>
    /// <see langword="true"/> for a prerelease; <see langword="false"/> for a stable version or an
    /// unparseable string (which is never treated as a prerelease, so it cannot widen the search).
    /// </returns>
    public static bool IsPrerelease(string? version) =>
        NuGetVersion.TryParse(version, out var parsed) && parsed.IsPrerelease;

    /// <summary>
    /// Returns how long to wait between checks for a build on <paramref name="version"/>'s channel.
    /// </summary>
    public static TimeSpan GetCheckInterval(string? version) =>
        IsPrerelease(version) ? PrereleaseInterval : StableInterval;

    /// <summary>
    /// Picks the newest version worth upgrading to, from everything a feed reports.
    /// </summary>
    /// <param name="installedVersion">The running version.</param>
    /// <param name="availableVersions">Every version the feed lists, in any order.</param>
    /// <returns>
    /// The version to recommend, or <see langword="null"/> when the installed version is already
    /// current, is unparseable, or nothing newer exists on its channel.
    /// </returns>
    public static NuGetVersion? SelectUpdate(string? installedVersion, IEnumerable<string> availableVersions)
    {
        ArgumentNullException.ThrowIfNull(availableVersions);

        if (!NuGetVersion.TryParse(installedVersion, out var installed))
        {
            // No parseable version means nothing to compare against. A local F5 build lands here,
            // and it must not produce a notice.
            return null;
        }

        // A preview user is offered previews; a stable user is never offered one. Pushing a
        // prerelease at someone who deliberately installed a stable build would be a downgrade in
        // stability even when it is an upgrade in version number.
        var allowPrerelease = installed.IsPrerelease;

        NuGetVersion? best = null;

        foreach (var candidate in availableVersions)
        {
            if (!NuGetVersion.TryParse(candidate, out var parsed))
            {
                // Feeds can list versions this client does not understand. Skipping beats throwing
                // for a check that is meant to be invisible when it fails.
                continue;
            }

            if (parsed.IsPrerelease && !allowPrerelease)
            {
                continue;
            }

            // NuGetVersion's comparison implements SemVer precedence, which is the whole reason
            // this type is used instead of System.Version: it knows 0.2.0-preview precedes 0.2.0.
            if (parsed > installed && (best is null || parsed > best))
            {
                best = parsed;
            }
        }

        return best;
    }
}
