using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.Compilation;

public sealed class QueryCompilerTests
{
    private static (Type ContextType, LoadedAssemblyHandle Handle) LoadSampleAppDbContext()
    {
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);
        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors
            .Single(d => d.Name == "SampleAppDbContext");
        return (descriptor.ClrType, handle);
    }

    [Fact]
    public async Task CompileAsync_ValidExpressionQuery_ProducesLoadablePeAndPdbBytes()
    {
        var (contextType, handle) = LoadSampleAppDbContext();
        var source = UserQuerySourceGenerator.Generate(contextType, "Orders.Where(o => o.Amount > 10)", "compileok1");
        var compiler = new QueryCompiler(new QueryCompilationOptions());

        var compiled = await compiler.CompileAsync(source, handle, CancellationToken.None);

        Assert.NotEmpty(compiled.Pe);
        Assert.NotEmpty(compiled.Pdb);
    }

    [Fact]
    public async Task CompileAsync_SyntaxError_ThrowsQueryExecutionExceptionWithSourceRelativeLineNumber()
    {
        var (contextType, handle) = LoadSampleAppDbContext();
        // Missing closing paren - guaranteed syntax error on the query line.
        var source = UserQuerySourceGenerator.Generate(contextType, "Orders.Where(o => o.Total > 10", "compilebad1");
        var compiler = new QueryCompiler(new QueryCompilationOptions());

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(
            () => compiler.CompileAsync(source, handle, CancellationToken.None));

        Assert.Contains("Line 1:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompileAsync_BindingError_ThrowsQueryExecutionException()
    {
        var (contextType, handle) = LoadSampleAppDbContext();
        var source = UserQuerySourceGenerator.Generate(contextType, "Orders.ThisMemberDoesNotExist()", "compilebad2");
        var compiler = new QueryCompiler(new QueryCompilationOptions());

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(
            () => compiler.CompileAsync(source, handle, CancellationToken.None));

        Assert.Contains("could not be compiled", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompileAsync_NonPositiveTimeout_ThrowsInvalidOperationException()
    {
        var (contextType, handle) = LoadSampleAppDbContext();
        var source = UserQuerySourceGenerator.Generate(contextType, "Orders.Count()", "compiletimeout1");
        var compiler = new QueryCompiler(new QueryCompilationOptions { CompileTimeoutSeconds = 0 });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => compiler.CompileAsync(source, handle, CancellationToken.None));
    }

    [Fact]
    public async Task CompileAsync_AlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        var (contextType, handle) = LoadSampleAppDbContext();
        var source = UserQuerySourceGenerator.Generate(contextType, "Orders.Count()", "compilecancel1");
        var compiler = new QueryCompiler(new QueryCompilationOptions());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => compiler.CompileAsync(source, handle, cts.Token));
    }

    [Fact]
    public async Task CompileAsync_UnknownAdditionalReferenceAssemblyName_ThrowsInvalidOperationException()
    {
        var (contextType, handle) = LoadSampleAppDbContext();
        var source = UserQuerySourceGenerator.Generate(contextType, "Orders.Count()", "compilerefbad1");
        var compiler = new QueryCompiler(new QueryCompilationOptions
        {
            AdditionalReferenceAssemblyNames = ["ThisAssemblyDoesNotExistAnywhere"]
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => compiler.CompileAsync(source, handle, CancellationToken.None));
    }

    [Fact]
    public async Task CompileAsync_TargetDbContextDerivesFromIdentityDbContext_CompilesSuccessfully()
    {
        // Regression test for a bug found while live-testing run_query's Roslyn engine against
        // OPG Platform's CommerceDbContext (which derives from IdentityDbContext<TUser, ...>).
        // GetReferences previously only added MetadataReferences for the target's own
        // LoadedAssemblyPaths, which deliberately excludes SharedFrameworkAssemblyNames entries
        // like Microsoft.AspNetCore.Identity.EntityFrameworkCore (see that type's doc comment).
        // Every single query against such a context failed to compile - not just ones that
        // reference Identity types directly - because the base class itself couldn't bind.
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.IdentityAppDllPath);
        var contextType = handle.Assembly.GetType("IdentityApp.IdentityAppDbContext", throwOnError: true)!;
        var source = UserQuerySourceGenerator.Generate(contextType, "Orders.Count()", "compileidentity1");
        var compiler = new QueryCompiler(new QueryCompilationOptions());

        var compiled = await compiler.CompileAsync(source, handle, CancellationToken.None);

        Assert.NotEmpty(compiled.Pe);
    }

    [Theory]
    [InlineData("System.IO.File.ReadAllText(\"C:/secrets.txt\")", "compileboundaryio1")]
    [InlineData("new System.Net.Http.HttpClient()", "compileboundarynet1")]
    [InlineData("System.Diagnostics.Process.Start(\"cmd.exe\")", "compileboundaryproc1")]
    [InlineData("System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new System.Reflection.AssemblyName(\"x\"), System.Reflection.Emit.AssemblyBuilderAccess.Run)", "compileboundaryemit1")]
    public async Task CompileAsync_QueryReferencingSandboxedNamespace_ThrowsQueryExecutionException(string query, string token)
    {
        // These namespaces are deliberately excluded from QueryCompiler's curated reference
        // allowlist (see BuiltInReferenceAssemblyNames), so a query referencing any type from
        // them must fail to bind at compile time rather than execute.
        var (contextType, handle) = LoadSampleAppDbContext();
        var source = UserQuerySourceGenerator.Generate(contextType, query, token);
        var compiler = new QueryCompiler(new QueryCompilationOptions());

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(
            () => compiler.CompileAsync(source, handle, CancellationToken.None));

        Assert.Contains("could not be compiled", ex.Message, StringComparison.Ordinal);
    }
}
