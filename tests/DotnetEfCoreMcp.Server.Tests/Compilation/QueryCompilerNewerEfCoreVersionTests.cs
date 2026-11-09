using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.Compilation;

/// <summary>Regression coverage for query COMPILATION against a target that references a newer
/// EF Core patch than the server (issue #118).
///
/// <para>#116 fixed the runtime bind for this situation: <see cref="SharedAssemblyResolver"/>
/// resolves shared assemblies by simple name, which ignores the requested version while still
/// yielding one shared copy, so <c>load_assembly</c> discovers contexts again. The compile path got
/// no equivalent treatment - <c>QueryCompiler.GetReferences</c> built its reference set from the
/// server's OWN loaded assemblies, so Roslyn compiled the generated <c>UserQuery</c> against the
/// server's older EF Core while the target assembly it must bind declared a dependency on the
/// newer one. Roslyn applies the same exact-or-higher version rule as the CLR binder and rejects
/// the reference outright, so every query failed with "uses ... which has a higher version than
/// referenced assembly".</para>
///
/// <para><c>QueryCompilerTests</c> only loads fixtures that inherit the server's EF Core version
/// through Central Package Management, so host and target always agree there and no existing
/// compilation test can observe a version gap - which is why #116 could land green while leaving
/// this path broken. <c>NewerEfCoreApp</c> already pins one patch above the server deliberately; it
/// was simply never used by a compilation test.</para></summary>
public sealed class QueryCompilerNewerEfCoreVersionTests
{
    /// <summary><c>CatalogDbContext</c> derives from plain <c>DbContext</c>, so this isolates the
    /// EF Core version gap on its own, with no ASP.NET Core shared-framework involvement.</summary>
    [Fact]
    public async Task CompileAsync_TargetReferencesNewerEfCore_Compiles()
    {
        var handle = new AssemblyLoaderService().Load(FixturePaths.NewerEfCoreAppDllPath);
        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors
            .Single(d => d.Name == "CatalogDbContext");
        var source = UserQuerySourceGenerator.Generate(descriptor.ClrType, "Products.Take(1)", "newerefcore1");
        var compiler = new QueryCompiler(new QueryCompilationOptions());

        var compiled = await compiler.CompileAsync(source, handle, CancellationToken.None);

        Assert.NotEmpty(compiled.Pe);
    }

    /// <summary><c>AccountDbContext</c> derives from <c>IdentityDbContext&lt;IdentityUser&gt;</c>,
    /// so its inherited member surface additionally reaches
    /// Microsoft.AspNetCore.Identity.EntityFrameworkCore - a shared assembly the server resolves
    /// from the ASP.NET Core shared framework rather than from its own package references. Kept
    /// separate from the plain context above so a fix covering only one of the two hard-shared
    /// assemblies shows up as one passing and one failing rather than a single opaque
    /// result.</summary>
    [Fact]
    public async Task CompileAsync_IdentityTargetReferencesNewerEfCore_Compiles()
    {
        var handle = new AssemblyLoaderService().Load(FixturePaths.NewerEfCoreAppDllPath);
        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors
            .Single(d => d.Name == "AccountDbContext");
        var source = UserQuerySourceGenerator.Generate(descriptor.ClrType, "Products.Take(1)", "newerefcore2");
        var compiler = new QueryCompiler(new QueryCompilationOptions());

        var compiled = await compiler.CompileAsync(source, handle, CancellationToken.None);

        Assert.NotEmpty(compiled.Pe);
    }
}
