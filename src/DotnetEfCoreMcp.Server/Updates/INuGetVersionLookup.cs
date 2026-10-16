namespace DotnetEfCoreMcp.Server.Updates;

/// <summary>
/// Looks up the versions a NuGet feed publishes for a package.
/// </summary>
/// <remarks>
/// An interface so the update logic can be tested without network access. Every implementation must
/// honor the same contract: never throw for an ordinary failure, return an empty list instead. The
/// update check is a cosmetic feature and must not be able to surface an error to a caller.
/// </remarks>
public interface INuGetVersionLookup
{
    /// <summary>
    /// Returns every version the feed lists for <paramref name="packageId"/>.
    /// </summary>
    /// <param name="packageId">The NuGet package id.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>
    /// The published versions, or an empty list when the feed is unreachable, slow, or returns
    /// anything unexpected. Includes prereleases; filtering is the caller's job.
    /// </returns>
    Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken);
}
