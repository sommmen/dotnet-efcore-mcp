using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Migrations;
using DotnetEfCoreMcp.Server.Mutations;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Schema;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using DotnetEfCoreMcp.Server.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace DotnetEfCoreMcp.Server.Tests.Tools;

/// <summary>Covers <c>run_query</c>'s structured error formatting (<c>FormatQueryError</c>) for
/// Roslyn compile/configuration failures, ensuring the caller gets an actionable "Next step" hint
/// tailored to the actual failure instead of the generic LINQ-flavored hint, which is misleading
/// for e.g. a C# compile error or a misconfigured out-of-process query host.</summary>
public sealed class EfCoreMcpToolsQueryErrorFormattingTests
{
    [Fact]
    public async Task RunQuery_WithRoslynEngineAndInvalidCSharp_ReportsACompileErrorHint()
    {
        var tools = CreateTools(new QueryExecutionOptions
        {
            Mode = QueryExecutionMode.InProcess,
        });
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.RunQuery("SampleAppDbContext", "Customers.ThisMethodDoesNotExist()"));

        Assert.Contains("could not be compiled", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Next step: Fix the reported compile error(s)", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewQuerySql_WithScalarResult_ReportsAPreviewNotAvailableHint()
    {
        // Use InProcess mode, which is required for preview_query_sql to work.
        var tools = CreateTools(new QueryExecutionOptions
        {
            Mode = QueryExecutionMode.InProcess,
        });
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        // Zip has no SQL translation and returns a lazily-evaluated IEnumerable<T>, not an
        // IQueryable (unlike Count(), which would execute immediately against the schema-less
        // in-memory database and fail with an unrelated evaluation error instead), so this
        // exercises the "not an IQueryable" rejection itself without touching the database.
        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.PreviewQuerySql(
                "SampleAppDbContext",
                "Customers.OrderBy(c => c.Id).AsEnumerable().Zip(Orders.OrderBy(o => o.Id).AsEnumerable(), (c, o) => new { c.Name, o.Amount })"));

        Assert.Contains("is not an IQueryable and has no SQL to preview", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Next step: Rewrite the query", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("server-side configuration problem", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewQuerySql_WithOutOfProcessModeAndNoHostConfigured_ReportsAServerConfigurationHint()
    {
        var tools = CreateTools(new QueryExecutionOptions
        {
            Mode = QueryExecutionMode.OutOfProcess,
            OutOfProcessHostPath = null,
        });
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.PreviewQuerySql(
                "SampleAppDbContext",
                "Customers.Where(c => c.Age >= 18)"));

        Assert.Contains("QueryExecution:OutOfProcessHostPath", exception.Message, StringComparison.Ordinal);
        Assert.Contains("server-side configuration problem", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunQuery_WithOutOfProcessModeAndNoHostConfigured_ReportsAServerConfigurationHint()
    {
        var tools = CreateTools(new QueryExecutionOptions
        {
            Mode = QueryExecutionMode.OutOfProcess,
            OutOfProcessHostPath = null,
        });
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.RunQuery("SampleAppDbContext", "Customers.Select(c => c.Name)"));

        Assert.Contains("QueryExecution:OutOfProcessHostPath", exception.Message, StringComparison.Ordinal);
        Assert.Contains("server-side configuration problem", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("validate Dynamic LINQ syntax", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunQuery_WithUntranslatableExpression_SurfacesTheRealEfTranslationMessage()
    {
        // Issue #85: the provider's own diagnostic is the actionable part of a translation
        // failure - without it, a genuinely broken query is indistinguishable from a healthy
        // tool refusing to work. String.Normalize() has no SQL translation, so EF Core throws
        // its "could not be translated" diagnostic naming the offending expression.
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(new QueryExecutionOptions { Mode = QueryExecutionMode.InProcess }, db);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        EnsureSchema(db);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.RunQuery("SampleAppDbContext", "Customers.Where(c => c.Name.Normalize() == \"x\").Take(1)"));

        Assert.Contains("could not be translated", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Normalize", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunQuery_InDefaultAutoMode_SurfacesTheRealEfTranslationMessage()
    {
        // The reporter hit this in Auto (the default), where the diagnostic has to survive the
        // isolated host's single-string wire format as well as the server's formatting.
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(
            new QueryExecutionOptions { Mode = QueryExecutionMode.Auto, OutOfProcessHostPath = FixturePaths.QueryHostDllPath },
            db);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        EnsureSchema(db);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.RunQuery("SampleAppDbContext", "Customers.Where(c => c.Name.Normalize() == \"x\").Take(1)"));

        Assert.Contains("could not be translated", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Normalize", exception.Message, StringComparison.Ordinal);
        // The cause must appear exactly once: the host already flattened it into its Error string,
        // so the server must not wrap it behind a second "Cause:".
        Assert.Equal(1, CountOccurrences(exception.Message, "Cause:"));
    }

    [Fact]
    public async Task RunQuery_CollectionProjectionConflict_SurfacesTheProviderDiagnosticAndACollectionHint()
    {
        // The family of failure from issue #85: EF Core refusing to combine a collection in a
        // projection with another operator. Previously this returned the same opaque text as a
        // perfectly valid query, so the agent could not tell the two apart.
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(new QueryExecutionOptions { Mode = QueryExecutionMode.InProcess }, db);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        EnsureSchema(db);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.RunQuery(
                "SampleAppDbContext",
                "Customers.Select(c => new { c.Name, Amounts = c.Orders.Select(o => o.Amount).ToList() }).Distinct()"));

        Assert.Contains("Cause: InvalidOperationException:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("projection containing a collection", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("collection", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"Next step: {GenericHintFragment}", exception.Message, StringComparison.Ordinal);
    }

    private const string GenericHintFragment = "verify entity and property names with get_schema";

    [Fact]
    public async Task RunQuery_ValidQuery_ProducesNoErrorResemblingATranslationFailure()
    {
        // The core complaint of issue #85 was that valid and broken queries were indistinguishable.
        // This pins the other half of that table: a valid query must succeed outright.
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(new QueryExecutionOptions { Mode = QueryExecutionMode.InProcess }, db);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        EnsureSchema(db);

        var json = await tools.RunQuery("SampleAppDbContext", "Customers.Take(3).Select(c => new { c.Id, c.Name })");

        Assert.DoesNotContain("could not be translated", json, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
            i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static void EnsureSchema(SqliteTestDatabase db)
    {
        var handle = new AssemblyLoaderService().Load(FixturePaths.SampleAppDllPath);
        var contextType = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors
            .Single(d => d.Name == "SampleAppDbContext").ClrType;
        using var context = DbContextActivator.CreateInstance(contextType, db.ToRegistryEntry(), DatabaseProvider.Sqlite);
        context.Database.EnsureCreated();
    }

    private static EfCoreMcpTools CreateTools(QueryExecutionOptions queryExecutionOptions, SqliteTestDatabase? db = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Connections:Primary:ConnectionString"] = db?.ConnectionString ?? "Data Source=:memory:",
                ["Connections:Primary:Provider"] = "Sqlite",
                ["Connections:Primary:Environment"] = "Development",
                ["Connections:Primary:AccessPolicy:AllowContexts:0"] = "SampleApp.SampleAppDbContext",
            })
            .Build();
        var rawSqlOptions = new RawSqlExecutionOptions();
        var migrationsOptions = new MigrationsOptions();

        return new EfCoreMcpTools(
            new AssemblyLoaderService(),
            new AssemblyDiscoveryService(),
            new ConnectionRegistry(configuration),
            new SchemaCache(),
            new RoslynQueryExecutor(queryExecutionOptions, new QueryCompiler(new QueryCompilationOptions())),
            new OutOfProcessRoslynQueryExecutor(queryExecutionOptions),
            queryExecutionOptions,
            rawSqlOptions,
            new SqlQueryExecutor(rawSqlOptions, NullLogger<SqlQueryExecutor>.Instance),
            migrationsOptions,
            new MigrationInspector(migrationsOptions, NullLogger<MigrationInspector>.Instance),
            new JsonToolResultFormatter(),
            new ToolDiagnosticsOptions(),
            NullLogger<EfCoreMcpTools>.Instance,
            new EntityMutationsOptions(),
            new EntityMutationExecutor(NullLogger<EntityMutationExecutor>.Instance));
    }
}
