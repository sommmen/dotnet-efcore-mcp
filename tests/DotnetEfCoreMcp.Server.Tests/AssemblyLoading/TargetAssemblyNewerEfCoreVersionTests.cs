using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.AssemblyLoading;

/// <summary>Regression coverage for targets that reference a NEWER EF Core patch than the server.
///
/// <para><see cref="SharedFrameworkAssemblyNames"/> forces EF Core and
/// Microsoft.AspNetCore.Identity.EntityFrameworkCore to resolve from the default
/// <c>AssemblyLoadContext</c>, so that <c>typeof(DbContext).IsAssignableFrom</c> keeps working.
/// <c>TargetAssemblyLoadContext.Load</c> implements that by returning <c>null</c> for those simple
/// names, which hands resolution to the server's own PackageReference-restored copy.</para>
///
/// <para>The CLR's binder allows an assembly reference to be satisfied by a HIGHER version, never
/// by a lower one. So when the target was compiled against a newer EF Core than the server
/// restored, that <c>null</c> cannot be satisfied: the bind fails with
/// <c>FileNotFoundException</c>, every type whose base chain reaches <c>DbContext</c> is dropped
/// from <c>Assembly.GetTypes()</c>, and discovery reports "no DbContext found" for an assembly
/// that plainly has some.</para>
///
/// <para>Every other fixture inherits the server's EF Core version through Central Package
/// Management, so host and target always agree and this path is never exercised.
/// <c>NewerEfCoreApp</c> pins one patch above the server deliberately.</para></summary>
public sealed class TargetAssemblyNewerEfCoreVersionTests
{
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

        // Asserted separately from discovery because the warnings are the actionable signal: they
        // name the assembly and version the binder could not satisfy, which is what distinguishes
        // this failure from an assembly that genuinely has no DbContext.
        Assert.Empty(result.TypeLoadWarnings);
    }

    [Fact]
    public void Load_TargetReferencingNewerEfCoreWithIdentityBaseClass_DiscoversItsDbContext()
    {
        var service = new AssemblyLoaderService();

        var handle = service.Load(FixturePaths.NewerEfCoreAppDllPath);
        var result = DbContextScanner.FindDbContextTypes(handle.Assembly);

        // IdentityDbContext<T> additionally reaches Microsoft.AspNetCore.Identity.EntityFrameworkCore,
        // which lives in the ASP.NET Core shared framework rather than in a package the server
        // restores. The server's runtimeconfig.json declares only Microsoft.NETCore.App, so this
        // case can keep failing after the EF Core version issue is addressed.
        Assert.Contains(result.Descriptors, d => d.Name == "AccountDbContext");
    }
}
