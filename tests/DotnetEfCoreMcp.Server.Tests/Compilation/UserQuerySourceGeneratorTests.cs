using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
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

    [Fact]
    public void Generate_DesignTimeFactoryContext_GeneratesStaticMethodWithDbSetAliases()
    {
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);
        var contextType = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors
            .Single(d => d.Name == "ApplicationFactoryDbContext").ClrType;

        var result = UserQuerySourceGenerator.Generate(contextType, "Customers.Select(c => c.Name)", "abc123");

        Assert.Contains("public static class UserQuery_abc123", result.Source, StringComparison.Ordinal);
        Assert.Contains("RunUserAuthoredQuery(global::SampleApp.ApplicationFactoryDbContext context)", result.Source, StringComparison.Ordinal);
        Assert.Contains("var Customers = context.Customers;", result.Source, StringComparison.Ordinal);
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
    public void Generate_EmitsUsingForDbContextAndEntityNamespaces_SoSiblingTypesResolveUnqualified()
    {
        // Regression test: previously only "System.Linq" and "Microsoft.EntityFrameworkCore" were
        // emitted as using directives, so a query referencing a sibling type in the same namespace
        // as the DbContext/entities (e.g. an enum like "PartnerType.Transport") required full
        // qualification while inherited DbSet properties (e.g. "Orders") resolved unqualified.
        var result = UserQuerySourceGenerator.Generate(SampleAppDbContextType, "Orders.Count()", "abc123");

        Assert.Contains("using SampleApp;", result.Source, StringComparison.Ordinal);
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
    public void Generate_DesignTimeFactoryOnlyContext_GeneratesStaticMethodWithDbSetAliases()
    {
        // FactoryOnlyDbContext has no options-constructor shape the activator recognizes, so it
        // is classified as DesignTimeFactory just like ApplicationFactoryDbContext. Source
        // generation must support it the same way: as a static query method, not a rejection.
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);
        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors
            .Single(d => d.Name == "FactoryOnlyDbContext");

        var result = UserQuerySourceGenerator.Generate(descriptor.ClrType, "Customers.Count()", "abc123");

        Assert.Contains("public static class UserQuery_abc123", result.Source, StringComparison.Ordinal);
        Assert.Contains("RunUserAuthoredQuery(global::SampleApp.FactoryOnlyDbContext context)", result.Source, StringComparison.Ordinal);
    }
}
