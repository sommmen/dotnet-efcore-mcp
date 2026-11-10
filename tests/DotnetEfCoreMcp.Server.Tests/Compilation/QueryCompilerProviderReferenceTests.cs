using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.Compilation;

/// <summary>Regression coverage for issue #122: a query against a SQL Server connection failed to
/// compile with "the type 'NpgsqlTsQuery' is defined in an assembly that is not referenced",
/// naming a PostgreSQL type neither the target nor the connection has anything to do with.
///
/// <para>The reference set used to consist of whichever shared assemblies the server happened to
/// have loaded at that moment. Npgsql.EntityFrameworkCore.PostgreSQL (loaded eagerly by the
/// provider-configuration switch's <c>UseNpgsql</c> call site) could therefore be referenced
/// without Npgsql (loaded only when a PostgreSQL connection is actually opened), which puts
/// <c>NpgsqlDbFunctionsExtensions</c> overloads into overload resolution with parameter types the
/// compiler cannot bind. Because this depended on server state rather than query text, the same
/// query could succeed and then fail within one session.</para></summary>
public sealed class QueryCompilerProviderReferenceTests
{
    private static LoadedAssemblyHandle LoadSampleApp() => new AssemblyLoaderService().Load(FixturePaths.SampleAppDllPath);

    private static Type SampleAppContextType(LoadedAssemblyHandle handle) =>
        DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors.Single(d => d.Name == "SampleAppDbContext").ClrType;

    [Theory]
    [InlineData(DatabaseProvider.SqlServer)]
    [InlineData(DatabaseProvider.Sqlite)]
    [InlineData(DatabaseProvider.PostgreSql)]
    [InlineData(null)]
    public void GetReferences_ForAnyProvider_NeverReferencesAProviderSurfaceInPart(DatabaseProvider? provider)
    {
        var handle = LoadSampleApp();
        var compiler = new QueryCompiler(new QueryCompilationOptions());

        var referenced = compiler.GetReferenceAssemblyNamesForTesting(handle, provider);

        // Npgsql.EntityFrameworkCore.PostgreSQL's public extension methods take Npgsql types, so
        // referencing one without the other is what produced the CS0012 in #122.
        Assert.Equal(
            referenced.Contains("Npgsql.EntityFrameworkCore.PostgreSQL"),
            referenced.Contains("Npgsql"));
        Assert.Equal(
            referenced.Contains("Microsoft.EntityFrameworkCore.SqlServer"),
            referenced.Contains("Microsoft.Data.SqlClient"));
        Assert.Equal(
            referenced.Contains("Microsoft.EntityFrameworkCore.Sqlite"),
            referenced.Contains("Microsoft.Data.Sqlite"));
    }

    [Fact]
    public void GetReferences_ForASqlServerConnection_ExcludesThePostgreSqlProviderSurface()
    {
        // The target has no PostgreSQL dependency, so nothing should bring Npgsql extension
        // methods into overload resolution for a SQL Server query at all.
        var handle = LoadSampleApp();
        var compiler = new QueryCompiler(new QueryCompilationOptions());

        var referenced = compiler.GetReferenceAssemblyNamesForTesting(handle, DatabaseProvider.SqlServer);

        Assert.DoesNotContain("Npgsql", referenced);
        Assert.DoesNotContain("Npgsql.EntityFrameworkCore.PostgreSQL", referenced);
        Assert.Contains("Microsoft.EntityFrameworkCore.SqlServer", referenced);
    }

    [Fact]
    public void GetReferences_ForAPostgreSqlConnection_ReferencesThePostgreSqlProviderSurfaceWhole()
    {
        var handle = LoadSampleApp();
        var compiler = new QueryCompiler(new QueryCompilationOptions());

        var referenced = compiler.GetReferenceAssemblyNamesForTesting(handle, DatabaseProvider.PostgreSql);

        Assert.Contains("Npgsql", referenced);
        Assert.Contains("Npgsql.EntityFrameworkCore.PostgreSQL", referenced);
    }

    [Fact]
    public void GetReferences_ForAnyProvider_StillReferencesTheNonProviderSharedAssemblies()
    {
        // Provider scoping must not narrow the shared names a DbContext's own base class reaches
        // (the #118/#120 regression surface).
        var handle = LoadSampleApp();
        var compiler = new QueryCompiler(new QueryCompilationOptions());

        var referenced = compiler.GetReferenceAssemblyNamesForTesting(handle, DatabaseProvider.SqlServer);

        Assert.Contains("Microsoft.EntityFrameworkCore", referenced);
        Assert.Contains("Microsoft.EntityFrameworkCore.Relational", referenced);
        Assert.Contains("Microsoft.Extensions.Logging.Abstractions", referenced);
    }

    [Theory]
    [InlineData(DatabaseProvider.SqlServer)]
    [InlineData(DatabaseProvider.Sqlite)]
    [InlineData(DatabaseProvider.PostgreSql)]
    [InlineData(null)]
    public async Task CompileAsync_InlineArrayContains_CompilesForEveryProvider(DatabaseProvider? provider)
    {
        // The exact shape from #122: an inline array literal plus Contains, which is where the
        // unbindable Npgsql extension overload entered overload resolution.
        var handle = LoadSampleApp();
        var source = UserQuerySourceGenerator.Generate(
            SampleAppContextType(handle),
            "Customers.Where(c => new[] {\"A\",\"B\"}.Contains(c.Name)).Select(c => new { c.Name })",
            $"providerscope{provider?.ToString() ?? "none"}");
        var compiler = new QueryCompiler(new QueryCompilationOptions());

        var compiled = await compiler.CompileAsync(source, handle, provider, CancellationToken.None);

        Assert.NotEmpty(compiled.Pe);
    }

    [Fact]
    public async Task CompileAsync_QueryUsingAProviderExtensionFromAnotherProvider_FailsToBind()
    {
        // The flip side of scoping: with PostgreSQL excluded, EF.Functions.ILike is simply not
        // there for a SQL Server query, which is an ordinary binding error about the query rather
        // than a reference-set failure.
        var handle = LoadSampleApp();
        var source = UserQuerySourceGenerator.Generate(
            SampleAppContextType(handle),
            "Customers.Where(c => EF.Functions.ILike(c.Name, \"a%\"))",
            "providerscopecross1");
        var compiler = new QueryCompiler(new QueryCompilationOptions());

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(
            () => compiler.CompileAsync(source, handle, DatabaseProvider.SqlServer, CancellationToken.None));

        Assert.Contains("could not be compiled", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(QueryCompiler.ReferenceSetFailureMarker, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderReferenceGroups_CoverEveryProviderSpecificSharedAssembly()
    {
        // Adding a provider assembly to SharedFrameworkAssemblyNames without also adding it to a
        // group would restore exactly the #122 failure mode for that assembly: it would be
        // referenced on "the server loaded it" grounds alone, independently of its siblings.
        var ungrouped = SharedFrameworkAssemblyNames.Value
            .Where(LooksProviderSpecific)
            .Where(name => !ProviderReferenceGroups.AllNames.Contains(name))
            .ToArray();

        Assert.Empty(ungrouped);
    }

    private static bool LooksProviderSpecific(string name) =>
        name.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("SQLitePCLRaw", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.Data.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.Identity", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.EntityFrameworkCore.Sql", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.EntityFrameworkCore.Postgre", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task CompileAsync_OrdinaryBindingError_IsNotAttributedToTheServersReferenceSet()
    {
        var handle = LoadSampleApp();
        var source = UserQuerySourceGenerator.Generate(
            SampleAppContextType(handle), "Customers.ThisMemberDoesNotExist()", "providerscopeblame1");
        var compiler = new QueryCompiler(new QueryCompilationOptions());

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(
            () => compiler.CompileAsync(source, handle, DatabaseProvider.SqlServer, CancellationToken.None));

        Assert.DoesNotContain(QueryCompiler.ReferenceSetFailureMarker, ex.Message, StringComparison.Ordinal);
    }
}
