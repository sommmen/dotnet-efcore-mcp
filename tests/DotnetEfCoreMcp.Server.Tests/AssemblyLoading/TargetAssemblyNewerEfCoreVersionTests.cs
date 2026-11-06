using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.AssemblyLoading;

/// <summary>Regression coverage for targets that reference a NEWER EF Core patch than the server.
///
/// <para><see cref="SharedFrameworkAssemblyNames"/> forces EF Core and
/// Microsoft.AspNetCore.Identity.EntityFrameworkCore to resolve to a single shared copy, so that
/// <c>typeof(DbContext).IsAssignableFrom</c> keeps working across the isolation boundary.
/// <c>TargetAssemblyLoadContext.Load</c> used to implement that by returning <c>null</c> for those
/// simple names, which delegates the bind to the default context.</para>
///
/// <para>Delegating forwards the target's own version-bearing reference, and the CLR binder
/// satisfies a reference only with an equal-or-HIGHER version, never a lower one. So when the
/// target was compiled against a newer EF Core than the server restored, the bind failed with
/// <c>FileNotFoundException</c>, every type whose base chain reaches <c>DbContext</c> was dropped
/// from <c>Assembly.GetTypes()</c>, and discovery reported "no DbContext found" for an assembly
/// that plainly had some. <see cref="SharedAssemblyResolver"/> resolves by simple name instead,
/// which ignores the requested version while still yielding one shared copy.</para>
///
/// <para>Every other fixture inherits the server's EF Core version through Central Package
/// Management, so host and target always agree and this path is never exercised.
/// <c>NewerEfCoreApp</c> pins one patch above the server deliberately.</para></summary>
public sealed class TargetAssemblyNewerEfCoreVersionTests
{
    [Fact]
    public void NewerEfCoreAppFixture_StillReferencesANewerEfCoreThanTheServer()
    {
        var handle = new AssemblyLoaderService().Load(FixturePaths.NewerEfCoreAppDllPath);

        var targetEfCoreVersion = handle.Assembly.GetReferencedAssemblies()
            .Single(reference => reference.Name == "Microsoft.EntityFrameworkCore")
            .Version;
        var serverEfCoreVersion = typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly.GetName().Version;

        // Guards the whole class: once the server's own pin catches up to the fixture's, host and
        // target agree again and every test below starts passing for the wrong reason. Renovate
        // bumps the server's EF Core routinely, so this has to fail loudly rather than quietly
        // stop covering anything. Fix by bumping tests/Fixtures/NewerEfCoreApp/NewerEfCoreApp.csproj
        // above the root Directory.Packages.props version.
        Assert.True(
            targetEfCoreVersion > serverEfCoreVersion,
            $"NewerEfCoreApp references EF Core {targetEfCoreVersion} but the server now ships " +
            $"{serverEfCoreVersion}. Bump the fixture's pinned EF Core version above the server's, " +
            "or this fixture no longer reproduces a target-ahead-of-server mismatch.");
    }

    [Fact]
    public void Load_TargetReferencingNewerEfCore_DiscoversItsDbContexts()
    {
        var service = new AssemblyLoaderService();

        var handle = service.Load(FixturePaths.NewerEfCoreAppDllPath);
        var result = DbContextScanner.FindDbContextTypes(handle.Assembly);

        // A plain DbContext subclass. Nothing about it is unusual except the version of the EF Core
        // assembly its base class lives in.
        Assert.Contains(result.Descriptors, d => d.Name == "CatalogDbContext");
    }

    [Fact]
    public void Load_TargetReferencingNewerEfCore_ProducesNoTypeLoadWarnings()
    {
        var service = new AssemblyLoaderService();

        var handle = service.Load(FixturePaths.NewerEfCoreAppDllPath);
        var result = DbContextScanner.FindDbContextTypes(handle.Assembly);

        // Asserted separately from discovery because a version gap must not cost *any* type: a
        // partially-loaded type graph is how this bug used to manifest, and silently dropping an
        // unrelated type would still break tools downstream of load_assembly.
        Assert.Empty(result.TypeLoadWarnings);
    }

    [Fact]
    public void Load_TargetReferencingNewerEfCoreWithIdentityBaseClass_DiscoversItsDbContext()
    {
        var service = new AssemblyLoaderService();

        var handle = service.Load(FixturePaths.NewerEfCoreAppDllPath);
        var result = DbContextScanner.FindDbContextTypes(handle.Assembly);

        // IdentityDbContext<T> additionally reaches Microsoft.AspNetCore.Identity.EntityFrameworkCore,
        // which also ships in the ASP.NET Core shared framework. Kept separate from the plain
        // context above because the two failed for independent reasons, so a partial fix shows up
        // as one passing and one failing rather than as a single opaque result.
        Assert.Contains(result.Descriptors, d => d.Name == "AccountDbContext");
    }

    [Fact]
    public void Load_TargetReferencingNewerEfCore_ReportsTheVersionGapAsADependencyDiagnostic()
    {
        var service = new AssemblyLoaderService();

        var handle = service.Load(FixturePaths.NewerEfCoreAppDllPath);

        // Discovery has to run first: diagnostics accumulate as the runtime resolves references
        // while types are loaded, not at Load time.
        _ = DbContextScanner.FindDbContextTypes(handle.Assembly);

        // Discovery succeeding silently would hide a real, if usually harmless, version gap. The
        // diagnostic names both versions so a later MissingMethodException is attributable.
        var diagnostic = Assert.Single(
            handle.DependencyDiagnostics,
            d => d.Contains("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.Contains("10.0.14", diagnostic, StringComparison.Ordinal);
    }
}
