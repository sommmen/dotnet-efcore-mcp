using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.Compilation;

public sealed class UserQuerySourceGeneratorTests
{
    private static Type SampleAppDbContextType
    {
        get
        {
            var service = new AssemblyLoaderService();
            var handle = service.Load(FixturePaths.SampleAppDllPath);
            return DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors
                .Single(d => d.Name == "SampleAppDbContext").ClrType;
        }
    }

    [Theory]
    [InlineData("Orders.Where(o => o.Total > 10)")]
    [InlineData("Orders.Select(o => new { o.Id })")]
    [InlineData("Orders.OrderBy(o => o.Id).Take(5)")]
    public void Generate_ExpressionBodiesWithoutTrailingSemicolon_AreClassifiedAsExpressionMode(string query)
    {
        var result = UserQuerySourceGenerator.Generate(SampleAppDbContextType, query, "abc123");

        Assert.False(result.IsStatementMode);
        Assert.Contains($"return {query};", result.Source, StringComparison.Ordinal);
    }

    // Per docs/development/roslyn-user-query.md, a trailing ';' or a top-level '{' opts the query
    // into statement mode even for what looks like a single expression - this is the documented
    // trigger for statement-block authoring, not a bug. Callers who want expression-mode "auto
    // return" semantics must omit the trailing semicolon.
    [Theory]
    [InlineData("Orders.Where(o => o.Total > 10);")]
    [InlineData("Orders.Select(o => new { o.Id });")]
    [InlineData("var recent = Orders.Where(o => o.Total > 10); return recent;")]
    [InlineData("var x = Orders.Count(); return x;")]
    [InlineData("{ var x = 1; return x; }")]
    public void Generate_QueriesWithTrailingSemicolonOrBraces_AreClassifiedAsStatementMode(string query)
    {
        var result = UserQuerySourceGenerator.Generate(SampleAppDbContextType, query, "abc123");

        Assert.True(result.IsStatementMode);
        Assert.Contains(query.Trim('{', '}', ' '), result.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_ExpressionMode_QueryHeaderLineCountPointsAtQueryLine()
    {
        var result = UserQuerySourceGenerator.Generate(SampleAppDbContextType, "Orders.Count()", "abc123");

        var lines = result.Source.Split('\n');
        var queryLine = lines[result.QueryHeaderLineCount];

        Assert.Contains("Orders.Count()", queryLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_GeneratesDerivedClassWithExpectedNamespaceAndBaseType()
    {
        var result = UserQuerySourceGenerator.Generate(SampleAppDbContextType, "Orders.Count()", "abc123");

        Assert.Equal("DotnetEfCoreMcp.Server.CompiledQueries.UserQuery_abc123", result.TypeName);
        Assert.Contains("namespace DotnetEfCoreMcp.Server.CompiledQueries;", result.Source, StringComparison.Ordinal);
        Assert.Contains("public sealed class UserQuery_abc123 : global::SampleApp.SampleAppDbContext", result.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_NullContextType_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => UserQuerySourceGenerator.Generate(null!, "Orders.Count()", "abc123"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Generate_EmptyOrWhitespaceQuery_Throws(string query)
    {
        Assert.Throws<ArgumentException>(() => UserQuerySourceGenerator.Generate(SampleAppDbContextType, query, "abc123"));
    }

    [Fact]
    public void Generate_AnyConstructorShape_OverridesOnConfiguringWithNoTracking()
    {
        var result = UserQuerySourceGenerator.Generate(SampleAppDbContextType, "Orders.Count()", "abc123");

        Assert.Contains(
            "optionsBuilder.UseQueryTrackingBehavior(global::Microsoft.EntityFrameworkCore.QueryTrackingBehavior.NoTracking);",
            result.Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_DesignTimeFactoryOnlyContext_ThrowsQueryExecutionException()
    {
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);
        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors
            .Single(d => d.Name == "FactoryOnlyDbContext");

        Assert.Throws<QueryExecutionException>(
            () => UserQuerySourceGenerator.Generate(descriptor.ClrType, "Customers.Count()", "abc123"));
    }
}
