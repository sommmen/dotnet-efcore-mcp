using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetEfCoreMcp.Server.Tests.Querying;

public sealed class QueryExecutorTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();
    private readonly Type _customerType;
    private readonly Type _orderType;
    private readonly Type _contextType;
    private readonly int _aliceId;

    public QueryExecutorTests()
    {
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);
        _contextType = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors.Single(d => d.Name == "SampleAppDbContext").ClrType;

        using var seedContext = DbContextActivator.CreateInstance(_contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite);
        seedContext.Database.EnsureCreated();

        _customerType = EntitySeeding.GetEntityClrType(seedContext, "Customer");
        _orderType = EntitySeeding.GetEntityClrType(seedContext, "Order");

        var alice = EntitySeeding.CreateEntity(_customerType, new Dictionary<string, object?> { ["Name"] = "Alice", ["Age"] = 30 });
        var bob = EntitySeeding.CreateEntity(_customerType, new Dictionary<string, object?> { ["Name"] = "Bob", ["Age"] = 15 });
        var obrien = EntitySeeding.CreateEntity(_customerType, new Dictionary<string, object?> { ["Name"] = "O'Brien", ["Age"] = 45 });

        seedContext.Add(alice);
        seedContext.Add(bob);
        seedContext.Add(obrien);
        seedContext.SaveChanges();

        _aliceId = (int)EntitySeeding.GetPropertyValue(alice, "Id")!;
        var order = EntitySeeding.CreateEntity(_orderType, new Dictionary<string, object?>
        {
            ["CustomerId"] = _aliceId,
            ["Amount"] = 19.99m,
            ["CreatedAtUtc"] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        seedContext.Add(order);
        seedContext.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private DbContext NewContext() => DbContextActivator.CreateInstance(_contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite);

    [Fact]
    public async Task ExecuteAsync_NoFilters_ReturnsAllRowsUpToDefaultTake()
    {
        using var context = NewContext();
        var executor = new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance);

        var result = await executor.ExecuteAsync(context, new QueryRequest { Entity = "Customer" }, commandTimeoutSeconds: 30, CancellationToken.None);

        Assert.Equal(3, result.RowCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhereWithPositionalParameter_FiltersCorrectly_AndHandlesQuoteCharacterSafely()
    {
        using var context = NewContext();
        var executor = new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            context,
            new QueryRequest { Entity = "Customer", Where = "Name == @0", Parameters = ["O'Brien"] },
            commandTimeoutSeconds: 30,
            CancellationToken.None);

        Assert.Equal(1, result.RowCount);
        Assert.Equal("O'Brien", result.Rows[0]["Name"]);
    }

    [Fact]
    public async Task ExecuteAsync_WhereWithComparisonAndParameter_FiltersCorrectly()
    {
        using var context = NewContext();
        var executor = new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            context,
            new QueryRequest { Entity = "Customer", Where = "Age > @0", Parameters = [18] },
            commandTimeoutSeconds: 30,
            CancellationToken.None);

        Assert.Equal(2, result.RowCount);
        Assert.All(result.Rows, row => Assert.True((int)row["Age"]! > 18));
    }

    [Fact]
    public async Task ExecuteAsync_OrderByDescending_ReturnsRowsInDescendingOrder()
    {
        using var context = NewContext();
        var executor = new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            context,
            new QueryRequest { Entity = "Customer", OrderBy = "Age desc" },
            commandTimeoutSeconds: 30,
            CancellationToken.None);

        var ages = result.Rows.Select(r => (int)r["Age"]!).ToList();
        Assert.Equal(ages.OrderByDescending(a => a), ages);
    }

    [Fact]
    public async Task ExecuteAsync_SkipAndTake_PagesResults()
    {
        using var context = NewContext();
        var executor = new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            context,
            new QueryRequest { Entity = "Customer", OrderBy = "Age", Skip = 1, Take = 1 },
            commandTimeoutSeconds: 30,
            CancellationToken.None);

        Assert.Equal(1, result.RowCount);
        Assert.Equal(1, result.EffectiveSkip);
        Assert.Equal(1, result.EffectiveTake);
    }

    [Fact]
    public async Task ExecuteAsync_TakeExceedsServerMax_IsClampedToServerMax()
    {
        using var context = NewContext();
        var executor = new QueryExecutor(new QueryExecutionOptions { MaxTake = 2 }, NullLogger<QueryExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            context,
            new QueryRequest { Entity = "Customer", Take = 10_000 },
            commandTimeoutSeconds: 30,
            CancellationToken.None);

        Assert.Equal(2, result.EffectiveTake);
        Assert.True(result.RowCount <= 2);
    }

    [Fact]
    public async Task ExecuteAsync_TakeOmitted_UsesConfiguredDefaultTake()
    {
        using var context = NewContext();
        var executor = new QueryExecutor(new QueryExecutionOptions { DefaultTake = 1, MaxTake = 200 }, NullLogger<QueryExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            context,
            new QueryRequest { Entity = "Customer" },
            commandTimeoutSeconds: 30,
            CancellationToken.None);

        Assert.Equal(1, result.EffectiveTake);
        Assert.Equal(1, result.RowCount);
    }

    [Fact]
    public async Task ExecuteAsync_ValidInclude_ProjectsNestedScalarOnlyNavigation()
    {
        using var context = NewContext();
        var executor = new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            context,
            new QueryRequest { Entity = "Customer", Where = "Name == @0", Parameters = ["Alice"], Include = ["Orders"] },
            commandTimeoutSeconds: 30,
            CancellationToken.None);

        var row = Assert.Single(result.Rows);
        var orders = Assert.IsAssignableFrom<System.Collections.IEnumerable>(row["Orders"]).Cast<object>().ToList();
        var firstOrder = Assert.Single(orders);
        var orderDict = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(firstOrder);
        Assert.Equal(19.99m, orderDict["Amount"]);
        // Depth is bounded to one level - the nested Order must not itself expand its own
        // `Customer` back-reference navigation (which would otherwise be an unbounded cycle).
        Assert.False(orderDict.ContainsKey("Customer"));
    }

    [Fact]
    public async Task ExecuteAsync_InvalidInclude_ThrowsQueryExecutionException()
    {
        using var context = NewContext();
        var executor = new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance);

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => executor.ExecuteAsync(
            context,
            new QueryRequest { Entity = "Customer", Include = ["NotARealNavigation"] },
            commandTimeoutSeconds: 30,
            CancellationToken.None));

        Assert.Contains("NotARealNavigation", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownEntity_ThrowsQueryExecutionException()
    {
        using var context = NewContext();
        var executor = new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance);

        await Assert.ThrowsAsync<QueryExecutionException>(() => executor.ExecuteAsync(
            context,
            new QueryRequest { Entity = "DoesNotExist" },
            commandTimeoutSeconds: 30,
            CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_InvalidWhereExpression_ThrowsQueryExecutionException()
    {
        using var context = NewContext();
        var executor = new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance);

        await Assert.ThrowsAsync<QueryExecutionException>(() => executor.ExecuteAsync(
            context,
            new QueryRequest { Entity = "Customer", Where = "this is not a valid expression &&&" },
            commandTimeoutSeconds: 30,
            CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_ResultRows_ContainOnlyScalarTopLevelPropertiesByDefault()
    {
        using var context = NewContext();
        var executor = new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            context,
            new QueryRequest { Entity = "Customer", Where = "Name == @0", Parameters = ["Alice"] },
            commandTimeoutSeconds: 30,
            CancellationToken.None);

        var row = Assert.Single(result.Rows);
        Assert.False(row.ContainsKey("Orders"));
        Assert.Equal("Alice", row["Name"]);
        Assert.Equal(30, row["Age"]);

    }

    [Fact]
    public async Task ExecuteAsync_ResultRows_ExcludeNotMappedProperties()
    {
        using var context = NewContext();
        var executor = new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            context,
            new QueryRequest { Entity = "Customer", Where = "Name == @0", Parameters = ["Alice"] },
            commandTimeoutSeconds: 30,
            CancellationToken.None);

        var row = Assert.Single(result.Rows);
        // `Customer.DisplayLabel` is a `[NotMapped]` computed property - only EF-mapped scalar
        // properties should ever be reflected over and projected.
        Assert.False(row.ContainsKey("DisplayLabel"));
    }

    [Fact]
    public async Task ExecuteAsync_IncludedCollectionExceedingCap_IsTruncatedToMaxIncludedCollectionItems()
    {
        using var seedContext = NewContext();

        for (var i = 0; i < 10; i++)
        {
            var extraOrder = EntitySeeding.CreateEntity(_orderType, new Dictionary<string, object?>
            {
                ["CustomerId"] = _aliceId,
                ["Amount"] = 1.00m,
                ["CreatedAtUtc"] = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            });
            seedContext.Add(extraOrder);
        }
        seedContext.SaveChanges();

        using var context = NewContext();
        var executor = new QueryExecutor(new QueryExecutionOptions { MaxIncludedCollectionItems = 3 }, NullLogger<QueryExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            context,
            new QueryRequest { Entity = "Customer", Where = "Name == @0", Parameters = ["Alice"], Include = ["Orders"] },
            commandTimeoutSeconds: 30,
            CancellationToken.None);

        var row = Assert.Single(result.Rows);
        var orders = Assert.IsAssignableFrom<System.Collections.IEnumerable>(row["Orders"]).Cast<object>().ToList();
        // Alice has 11 orders total (1 seeded + 10 extra) but the cap is 3.
        Assert.Equal(3, orders.Count);
    }
}
