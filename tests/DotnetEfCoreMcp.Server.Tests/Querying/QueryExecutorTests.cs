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

    public QueryExecutorTests()
    {
        var handle = new AssemblyLoaderService().Load(FixturePaths.SampleAppDllPath);
        _contextType = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors.Single(d => d.Name == "SampleAppDbContext").ClrType;
        using var context = NewContext();
        context.Database.EnsureCreated();
        _customerType = EntitySeeding.GetEntityClrType(context, "Customer");
        _orderType = EntitySeeding.GetEntityClrType(context, "Order");
        var alice = EntitySeeding.CreateEntity(_customerType, new Dictionary<string, object?> { ["Name"] = "Alice", ["Age"] = 30 });
        context.Add(alice);
        context.Add(EntitySeeding.CreateEntity(_customerType, new Dictionary<string, object?> { ["Name"] = "Bob", ["Age"] = 15 }));
        context.Add(EntitySeeding.CreateEntity(_customerType, new Dictionary<string, object?> { ["Name"] = "Carol", ["Age"] = 30 }));
        context.SaveChanges();
        context.Add(EntitySeeding.CreateEntity(_orderType, new Dictionary<string, object?> { ["CustomerId"] = EntitySeeding.GetPropertyValue(alice, "Id"), ["Amount"] = 19.99m, ["CreatedAtUtc"] = DateTime.UtcNow }));
        context.SaveChanges();
    }

    public void Dispose() => _db.Dispose();
    private DbContext NewContext() => DbContextActivator.CreateInstance(_contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite);
    private static QueryExecutor CreateExecutor(int maxTake = 200, int defaultTake = 50, int maxQueryOperators = 20) => new(new QueryExecutionOptions { MaxTake = maxTake, DefaultTake = defaultTake, MaxQueryOperators = maxQueryOperators }, NullLogger<QueryExecutor>.Instance);

    [Fact]
    public async Task ExecuteAsync_ResolvesDbSetAndFiltersProjection()
    {
        using var context = NewContext();
        var result = await CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = "Customers.Where(c => c.Age == 30).Select(c => c.Name)" }, 30, CancellationToken.None);
        Assert.Equal(2, result.RowCount);
        Assert.All(result.Rows, row => Assert.Contains(row["Value"], new[] { "Alice", "Carol" }));
    }

    [Fact]
    public async Task ExecuteAsync_AllowsNavigationPredicate()
    {
        using var context = NewContext();
        var result = await CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = "Orders.Where(o => o.Customer.Name == \"Alice\")" }, 30, CancellationToken.None);
        Assert.Single(result.Rows);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsTerminalAggregateWithoutPaging()
    {
        using var context = NewContext();
        var result = await CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = "Customers.Count(c => c.Age >= 30)" }, 30, CancellationToken.None);
        Assert.True(result.IsScalar);
        Assert.Equal(2, result.Scalar);
        Assert.Null(result.EffectiveTake);
    }

    [Fact]
    public async Task ExecuteAsync_CapsSequenceResults()
    {
        using var context = NewContext();
        var result = await CreateExecutor(1).ExecuteAsync(context, new QueryRequest { Query = "Customers.OrderBy(c => c.Name)" }, 30, CancellationToken.None);
        Assert.Equal(1, result.RowCount);
        Assert.Equal(1, result.EffectiveTake);
        Assert.True(result.HasMoreRows);
    }

    [Fact]
    public async Task ExecuteAsync_UsesDefaultPageWhenNoTakeIsSpecified()
    {
        using var context = NewContext();
        var result = await CreateExecutor(maxTake: 2, defaultTake: 1).ExecuteAsync(context, new QueryRequest { Query = "Customers.OrderBy(c => c.Name)" }, 30, CancellationToken.None);
        Assert.Equal(1, result.RowCount);
        Assert.Equal(1, result.EffectiveTake);
        Assert.Equal("Alice", result.Rows[0]["Name"]);
        Assert.True(result.HasMoreRows);
    }

    [Fact]
    public async Task ExecuteAsync_ClampsCallerTakeToMaximum()
    {
        using var context = NewContext();
        var result = await CreateExecutor(maxTake: 1).ExecuteAsync(context, new QueryRequest { Query = "Customers.OrderBy(c => c.Name).Take(100)" }, 30, CancellationToken.None);
        Assert.Single(result.Rows);
        Assert.Equal(1, result.EffectiveTake);
        Assert.True(result.HasMoreRows);
    }

    [Fact]
    public async Task ExecuteAsync_HasMoreRows_FalseWhenNoRowsMatch()
    {
        using var context = NewContext();
        var result = await CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = "Customers.Where(c => c.Age > 999)" }, 30, CancellationToken.None);
        Assert.Equal(0, result.RowCount);
        Assert.False(result.HasMoreRows);
    }

    [Fact]
    public async Task ExecuteAsync_HasMoreRows_FalseWhenRowCountExactlyMatchesEffectiveTake()
    {
        using var context = NewContext();
        var result = await CreateExecutor(maxTake: 3, defaultTake: 3).ExecuteAsync(context, new QueryRequest { Query = "Customers.OrderBy(c => c.Name)" }, 30, CancellationToken.None);
        Assert.Equal(3, result.RowCount);
        Assert.Equal(3, result.EffectiveTake);
        Assert.False(result.HasMoreRows);
    }

    [Fact]
    public async Task ExecuteAsync_HasMoreRows_TrueWhenExtraRowExistsBeyondTake()
    {
        using var context = NewContext();
        var result = await CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = "Customers.OrderBy(c => c.Name).Take(2)" }, 30, CancellationToken.None);
        Assert.Equal(2, result.RowCount);
        Assert.Equal(2, result.EffectiveTake);
        Assert.True(result.HasMoreRows);
        Assert.Equal(new[] { "Alice", "Bob" }, result.Rows.Select(row => row["Name"]));
    }

    [Fact]
    public async Task ExecuteAsync_HasMoreRows_ReflectsRemainderAfterSkippedWindow()
    {
        using var context = NewContext();
        var result = await CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = "Customers.OrderBy(c => c.Name).Skip(1).Take(1)" }, 30, CancellationToken.None);
        Assert.Single(result.Rows);
        Assert.Equal(1, result.EffectiveTake);
        Assert.Equal("Bob", result.Rows[0]["Name"]);
        Assert.True(result.HasMoreRows);

        var lastPage = await CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = "Customers.OrderBy(c => c.Name).Skip(2).Take(1)" }, 30, CancellationToken.None);
        Assert.Single(lastPage.Rows);
        Assert.Equal("Carol", lastPage.Rows[0]["Name"]);
        Assert.False(lastPage.HasMoreRows);
    }

    [Fact]
    public async Task ExecuteAsync_HasMoreRows_FalseForZeroTakeWithoutMaterializing()
    {
        using var context = NewContext();
        var result = await CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = "Customers.OrderBy(c => c.Name).Take(0)" }, 30, CancellationToken.None);
        Assert.Equal(0, result.RowCount);
        Assert.Equal(0, result.EffectiveTake);
        Assert.Empty(result.Rows);
        Assert.False(result.HasMoreRows);
    }

    [Fact]
    public async Task ExecuteAsync_HasMoreRows_FalseForScalarResults()
    {
        using var context = NewContext();
        var result = await CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = "Customers.Count(c => c.Age >= 30)" }, 30, CancellationToken.None);
        Assert.True(result.IsScalar);
        Assert.False(result.HasMoreRows);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsEnumerableOperations()
    {
        using var context = NewContext();
        await Assert.ThrowsAsync<QueryExecutionException>(() => CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = "Customers.Where(c => c.Name.ToCharArray().Contains('A'))" }, 30, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_RejectsExcessiveOperators()
    {
        using var context = NewContext();
        await Assert.ThrowsAsync<QueryExecutionException>(() => CreateExecutor(maxQueryOperators: 1).ExecuteAsync(context, new QueryRequest { Query = "Customers.Where(c => c.Age > 0).Select(c => c.Name)" }, 30, CancellationToken.None));
    }

    [Theory]
    [InlineData("Customers.SelectMany(c => c.Orders)")]
    [InlineData("Orders.Join(Customers, o => o.CustomerId, c => c.Id, (o, c) => c.Name)")]
    [InlineData("Orders.GroupJoin(Customers, o => o.CustomerId, c => c.Id, (o, cs) => cs)")]
    public async Task ExecuteAsync_RejectsUnsupportedDynamicLinqOperators(string query)
    {
        using var context = NewContext();
        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = query }, 30, CancellationToken.None));
        Assert.Contains("unsupported", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_FirstOrDefaultReturnsScalarRow()
    {
        using var context = NewContext();
        var result = await CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = "Customers.OrderBy(c => c.Name).FirstOrDefault()" }, 30, CancellationToken.None);
        Assert.True(result.IsScalar);
    }

    [Fact]
    public async Task ExecuteAsync_SingleOrDefaultReturnsScalarRow()
    {
        using var context = NewContext();
        var result = await CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = "Customers.SingleOrDefault(c => c.Name == \"Alice\")" }, 30, CancellationToken.None);
        Assert.True(result.IsScalar);
    }

    [Fact]
    public async Task ExecuteAsync_AnyReturnsScalarBoolean()
    {
        using var context = NewContext();
        var result = await CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = "Customers.Any(c => c.Age > 18)" }, 30, CancellationToken.None);
        Assert.True(result.IsScalar);
        Assert.Equal(true, result.Scalar);
    }

    [Fact]
    public async Task ExecuteAsync_AllReturnsScalarBoolean()
    {
        using var context = NewContext();
        var result = await CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = "Customers.All(c => c.Age > 0)" }, 30, CancellationToken.None);
        Assert.True(result.IsScalar);
        Assert.Equal(true, result.Scalar);
    }

    [Fact]
    public async Task ExecuteAsync_DistinctReturnsSequence()
    {
        using var context = NewContext();
        var result = await CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = "Customers.Select(c => c.Age).Distinct()" }, 30, CancellationToken.None);
        Assert.False(result.IsScalar);
        Assert.Equal(2, result.RowCount);
    }

    [Fact]
    public async Task ExecuteAsync_UnionCombinesTwoDbSetsByName()
    {
        using var context = NewContext();
        var result = await CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = "Customers.Select(c => c.Name).Union(Orders.Select(o => \"Alice\"))" }, 30, CancellationToken.None);
        Assert.False(result.IsScalar);
    }

    [Theory]
    [InlineData("customers.Where(c => c.Age > 0)")]
    [InlineData("Unknown.Where(c => true)")]
    [InlineData("Customers.AsEnumerable()")]
    [InlineData("Customers.Select(c => new DateTime())")]
    [InlineData("Customers; Orders")]
    public async Task ExecuteAsync_RejectsInvalidOrUnsafeExpressions(string query)
    {
        using var context = NewContext();
        await Assert.ThrowsAsync<QueryExecutionException>(() => CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = query }, 30, CancellationToken.None));
    }
}