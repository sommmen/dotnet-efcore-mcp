namespace DotnetEfCoreMcp.Server.DbContextDiscovery;

/// <summary>Result of scanning an assembly for <see cref="Microsoft.EntityFrameworkCore.DbContext"/>-derived
/// types via <see cref="DbContextScanner.FindDbContextTypes"/>. Carries not just the descriptors that were
/// successfully discovered, but also any type-load diagnostics produced along the way, so that a scan which
/// silently finds zero contexts because of a missing dependency (rather than because the assembly genuinely
/// has none) can be told apart and reported to the caller instead of being swallowed.</summary>
public sealed record DbContextScanResult(
    IReadOnlyList<DbContextDescriptor> Descriptors,
    IReadOnlyList<string> TypeLoadWarnings)
{
    /// <summary>Distinct messages of the <see cref="AssemblyLoading.AssemblyLoadFailedException"/>s the
    /// load context threw deliberately (for example an EF Core major-version gap) while resolving the
    /// assembly's types. The runtime buries them under a generic <c>FileLoadException</c>, so they are
    /// recorded separately for callers that report why no DbContext was found.</summary>
    public IReadOnlyList<string> AssemblyLoadFailureReasons { get; init; } = [];

    /// <summary>A sentence fragment naming why discovery came up empty when a deliberate assembly load
    /// failure is on record, otherwise an empty string; meant to be appended to a "no DbContexts"
    /// message.</summary>
    public string DescribeLoadFailures()
    {
        if (AssemblyLoadFailureReasons.Count == 0)
        {
            return string.Empty;
        }

        var more = AssemblyLoadFailureReasons.Count > 1
            ? $" ({AssemblyLoadFailureReasons.Count - 1} more load failure(s) are listed in the load_assembly warnings.)"
            : string.Empty;
        return $" Cause: {AssemblyLoadFailureReasons[0]}{more}";
    }
}
