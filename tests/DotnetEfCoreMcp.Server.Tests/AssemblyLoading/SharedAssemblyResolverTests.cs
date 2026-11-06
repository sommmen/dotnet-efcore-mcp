using System.Reflection;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using Microsoft.EntityFrameworkCore;

namespace DotnetEfCoreMcp.Server.Tests.AssemblyLoading;

/// <summary>Unit tests for <see cref="SharedAssemblyResolver"/>, the piece that makes a target
/// referencing a newer EF Core patch than the server still unify on the server's single copy
/// instead of failing the bind and silently dropping every DbContext-derived type.</summary>
public sealed class SharedAssemblyResolverTests
{
    private static Version ServerEfCoreVersion =>
        typeof(DbContext).Assembly.GetName().Version!;

    [Fact]
    public void TryResolveFromDefaultContext_ExactVersion_ReturnsServerCopyWithoutWarning()
    {
        var requested = typeof(DbContext).Assembly.GetName();

        var resolved = SharedAssemblyResolver.TryResolveFromDefaultContext(requested, out var assembly, out var warning);

        Assert.True(resolved);
        Assert.Same(typeof(DbContext).Assembly, assembly);
        Assert.Null(warning);
    }

    [Fact]
    public void TryResolveFromDefaultContext_NewerRequestedVersion_StillReturnsServerCopy()
    {
        var requested = NewerThanServer();

        var resolved = SharedAssemblyResolver.TryResolveFromDefaultContext(requested, out var assembly, out _);

        // The whole point: the binder would refuse this reference, but simple-name resolution does
        // not consult the version at all, so type identity survives the patch gap.
        Assert.True(resolved);
        Assert.Same(typeof(DbContext).Assembly, assembly);
    }

    [Fact]
    public void TryResolveFromDefaultContext_NewerRequestedVersion_ReportsBothVersionsInWarning()
    {
        var requested = NewerThanServer();

        SharedAssemblyResolver.TryResolveFromDefaultContext(requested, out _, out var warning);

        // The warning is the actionable signal, so it has to name both versions - the old failure
        // mode reported "no DbContext found", which pointed at the wrong remedy entirely.
        Assert.NotNull(warning);
        Assert.Contains("Microsoft.EntityFrameworkCore", warning);
        Assert.Contains(requested.Version!.ToString(), warning);
        Assert.Contains(ServerEfCoreVersion.ToString(), warning);
    }

    [Fact]
    public void TryResolveFromDefaultContext_OlderRequestedVersion_ReturnsServerCopyWithoutWarning()
    {
        // A target behind the server binds fine on its own (equal-or-higher is satisfiable), so
        // there is nothing to warn about.
        var requested = new AssemblyName("Microsoft.EntityFrameworkCore")
        {
            Version = new Version(ServerEfCoreVersion.Major, ServerEfCoreVersion.Minor, 0, 0),
        };

        var resolved = SharedAssemblyResolver.TryResolveFromDefaultContext(requested, out var assembly, out var warning);

        Assert.True(resolved);
        Assert.Same(typeof(DbContext).Assembly, assembly);
        Assert.Null(warning);
    }

    [Fact]
    public void TryResolveFromDefaultContext_AssemblyTheServerDoesNotShip_ReturnsFalse()
    {
        var requested = new AssemblyName("Contoso.NotAShippedAssembly") { Version = new Version(1, 0, 0, 0) };

        var resolved = SharedAssemblyResolver.TryResolveFromDefaultContext(requested, out var assembly, out var warning);

        // Callers rely on false to mean "fall back to the target's own copy" rather than letting
        // the whole dependent type graph fail to load.
        Assert.False(resolved);
        Assert.Null(assembly);
        Assert.Null(warning);
    }

    [Fact]
    public void TryResolveFromDefaultContext_AssemblyTheServerDoesNotShip_KeepsReturningFalseWhenRepeated()
    {
        var requested = new AssemblyName("Contoso.AlsoNotShipped") { Version = new Version(1, 0, 0, 0) };

        // Misses are memoized, so the second call takes the cached path rather than re-attempting
        // the bind. It must still report false: a cached miss that reported true (or threw) would
        // break the fallback that keeps ASP.NET Core shared-framework assemblies loadable.
        Assert.False(SharedAssemblyResolver.TryResolveFromDefaultContext(requested, out _, out _));
        Assert.False(SharedAssemblyResolver.TryResolveFromDefaultContext(requested, out var assembly, out _));

        Assert.Null(assembly);
    }

    [Fact]
    public void TryResolveFromDefaultContext_DifferentMajorVersion_ThrowsNamingBothVersions()
    {
        var requested = new AssemblyName("Microsoft.EntityFrameworkCore")
        {
            Version = new Version(ServerEfCoreVersion.Major + 1, 0, 0, 0),
        };

        // EF Core guarantees binary compatibility only within a major version, so neither copy can
        // stand in for the other. Throwing beats returning false: false would make the caller load
        // the target's own copy, splitting DbContext's type identity and recreating the silent
        // "no DbContext found" failure this resolver exists to fix.
        var exception = Assert.Throws<AssemblyLoadFailedException>(
            () => SharedAssemblyResolver.TryResolveFromDefaultContext(requested, out _, out _));

        Assert.Contains(requested.Version!.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains(ServerEfCoreVersion.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolveFromDefaultContext_OlderMajorVersion_AlsoThrows()
    {
        var requested = new AssemblyName("Microsoft.EntityFrameworkCore")
        {
            Version = new Version(ServerEfCoreVersion.Major - 1, 0, 0, 0),
        };

        // A target on an older major is just as unsupported as a newer one, and previously got no
        // diagnostic at all because the binder happily satisfied it with the higher version.
        Assert.Throws<AssemblyLoadFailedException>(
            () => SharedAssemblyResolver.TryResolveFromDefaultContext(requested, out _, out _));
    }

    private static AssemblyName NewerThanServer() =>
        new("Microsoft.EntityFrameworkCore")
        {
            Version = new Version(
                ServerEfCoreVersion.Major,
                ServerEfCoreVersion.Minor,
                ServerEfCoreVersion.Build + 2,
                ServerEfCoreVersion.Revision),
        };
}
