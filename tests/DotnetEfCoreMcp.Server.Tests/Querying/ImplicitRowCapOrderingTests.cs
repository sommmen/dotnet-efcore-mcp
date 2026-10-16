using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace DotnetEfCoreMcp.Server.Tests.Querying;

/// <summary>Regression coverage for issue #97: the server's implicit row cap is itself a row-limiting
/// operator, so against a target context that escalates EF Core's
/// <c>RowLimitingOperationWithoutOrderByWarning</c> to an error every unordered query failed - even
/// ones that succeed under plain EF Core, because plain EF Core never adds the cap. The cap must
/// therefore carry its own deterministic primary-key ordering.</summary>
public sealed class ImplicitRowCapOrderingTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();
    private readonly LoadedAssemblyHandle _handle;
    private readonly Type _throwingContextType;

    public ImplicitRowCapOrderingTests()
    {
        _handle = new AssemblyLoaderService().Load(FixturePaths.SampleAppDllPath);
        var descriptors = DbContextScanner.FindDbContextTypes(_handle.Assembly).Descriptors;
        _throwingContextType = descriptors.Single(d => d.Name == "ThrowOnUnorderedRowLimitDbContext").ClrType;
        var seedContextType = descriptors.Single(d => d.Name == "SampleAppDbContext").ClrType;

        using var context = DbContextActivator.CreateInstance(seedContextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite);
        context.Database.EnsureCreated();
        var customerType = EntitySeeding.GetEntityClrType(context, "Customer");
        foreach (var (name, age) in new[] { ("Alice", 30), ("Bob", 15), ("Carol", 42) })
        {
            context.Add(EntitySeeding.CreateEntity(customerType, new Dictionary<string, object?> { ["Name"] = name, ["Age"] = age }));
        }

        context.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ExecuteAsync_UnorderedQuery_SucceedsAgainstContextThatThrowsOnUnorderedRowLimit()
    {
        var result = await CreateExecutor().ExecuteAsync(
            _handle, _throwingContextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers" }, CancellationToken.None);

        Assert.Equal(3, result.RowCount);
        Assert.Equal(["Alice", "Bob", "Carol"], result.Rows.Select(row => row["Name"]));
    }

    [Fact]
    public async Task ExecuteAsync_UnorderedQuery_IssuesCappedSqlOrderedByPrimaryKey()
    {
        var commands = new List<string>();
        using var listener = new SqlCommandDiagnosticListener(commands);

        await CreateExecutor().ExecuteAsync(
            _handle, _throwingContextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Where(c => c.Age > 10)" }, CancellationToken.None);

        var command = Assert.Single(commands, text => text.Contains("FROM \"Customers\"", StringComparison.OrdinalIgnoreCase));
        Assert.Matches(@"(?is)ORDER\s+BY\s+.*?\bId\b", command);
        Assert.Contains("LIMIT", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_ExplicitOrdering_IsNotReplacedByTheImplicitKeyOrdering()
    {
        var result = await CreateExecutor().ExecuteAsync(
            _handle, _throwingContextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.OrderByDescending(c => c.Age)" }, CancellationToken.None);

        Assert.Equal(["Carol", "Alice", "Bob"], result.Rows.Select(row => row["Name"]));
    }

    [Fact]
    public async Task ExecuteAsync_UnorderedProjection_SucceedsAgainstContextThatThrowsOnUnorderedRowLimit()
    {
        var result = await CreateExecutor().ExecuteAsync(
            _handle, _throwingContextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Where(c => c.Age > 10).Select(c => c.Name)" }, CancellationToken.None);

        Assert.Equal(["Alice", "Bob", "Carol"], result.Rows.Select(row => row["Value"]));
    }

    [Fact]
    public async Task ExecuteAsync_QueryWithNoOrderableKey_StillReturnsItsRows()
    {
        var result = await CreateExecutor().ExecuteAsync(
            _handle, _throwingContextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Select(c => c.Age).Distinct()" }, CancellationToken.None);

        Assert.Equal([15, 30, 42], result.Rows.Select(row => row["Value"]).OrderBy(value => value));
    }

    /// <summary>The query shapes issue #97 reports as failing which carry no row-limiting operator of
    /// their own, so the server's implicit cap is the only one - the shapes the issue's own control run
    /// confirms succeed under plain EF Core precisely because plain EF Core never adds that cap.</summary>
    [Theory]
    [InlineData("Customers")]
    [InlineData("Customers.Select(c => c.Name)")]
    [InlineData("Customers.Where(c => c.Name == \"Alice\")")]
    [InlineData("Customers.Where(c => c.Age > 10)")]
    [InlineData("Customers.Select(c => new { c.Name, c.Age })")]
    [InlineData("Customers.Where(c => c.Age > 10).Select(c => c.Id).Distinct()")]
    [InlineData("Customers.Select(c => c.Name).Select(name => name.Trim())")]
    public async Task ExecuteAsync_QueryShapesReportedInIssue97_AllSucceed(string query)
    {
        var result = await CreateExecutor().ExecuteAsync(
            _handle, _throwingContextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = query }, CancellationToken.None);

        Assert.NotEmpty(result.Rows);
    }

    /// <summary>A row limit the caller wrote themselves stays their own: the server neither reorders it
    /// nor hides the context's configured failure, which plain EF Core raises identically for the same
    /// query. The surfaced error names the escalated warning so the cause is diagnosable.</summary>
    [Fact]
    public async Task ExecuteAsync_CallerWrittenUnorderedTake_SurfacesTheContextsOwnWarningAsTheCause()
    {
        var exception = await Assert.ThrowsAsync<QueryExecutionException>(() => CreateExecutor().ExecuteAsync(
            _handle, _throwingContextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Take(1)" }, CancellationToken.None));

        Assert.Contains("RowLimitingOperationWithoutOrderByWarning", QueryExceptionDetail.Describe(exception.InnerException));
    }

    /// <summary><c>preview_query_sql</c> never applies the implicit cap, so an unordered query previews
    /// the SQL it would really issue - without the key ordering <c>run_query</c> would add on top.</summary>
    [Fact]
    public async Task PreviewSqlAsync_UnorderedQuery_PreviewsTheUncappedUnorderedSql()
    {
        var result = await CreateExecutor().PreviewSqlAsync(
            _handle, _throwingContextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Where(c => c.Age > 10)" }, CancellationToken.None);

        Assert.Contains("FROM \"Customers\"", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LIMIT", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static RoslynQueryExecutor CreateExecutor(int maxTake = 200) => new(
        new QueryExecutionOptions { MaxTake = maxTake },
        new QueryCompiler(new QueryCompilationOptions()));
}
