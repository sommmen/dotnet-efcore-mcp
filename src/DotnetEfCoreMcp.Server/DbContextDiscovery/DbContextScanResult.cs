namespace DotnetEfCoreMcp.Server.DbContextDiscovery;

/// <summary>Result of scanning an assembly for <see cref="Microsoft.EntityFrameworkCore.DbContext"/>-derived
/// types via <see cref="DbContextScanner.FindDbContextTypes"/>. Carries not just the descriptors that were
/// successfully discovered, but also any type-load diagnostics produced along the way, so that a scan which
/// silently finds zero contexts because of a missing dependency (rather than because the assembly genuinely
/// has none) can be told apart and reported to the caller instead of being swallowed.</summary>
public sealed record DbContextScanResult(
    IReadOnlyList<DbContextDescriptor> Descriptors,
    IReadOnlyList<string> TypeLoadWarnings);
