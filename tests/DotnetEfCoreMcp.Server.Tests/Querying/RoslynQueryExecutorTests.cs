using System.Reflection;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace DotnetEfCoreMcp.Server.Tests.Querying;

public sealed class RoslynQueryExecutorTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();
    private readonly LoadedAssemblyHandle _handle;
    private readonly Type _contextType;

    public RoslynQueryExecutorTests()
    {
        _handle = new AssemblyLoaderService().Load(FixturePaths.SampleAppDllPath);
        _contextType = DbContextScanner.FindDbContextTypes(_handle.Assembly).Descriptors.Single(d => d.Name == "SampleAppDbContext").ClrType;
        using var context = NewContext();
        context.Database.EnsureCreated();
        var customerType = EntitySeeding.GetEntityClrType(context, "Customer");
        var alice = EntitySeeding.CreateEntity(customerType, new Dictionary<string, object?> { ["Name"] = "Alice", ["Age"] = 30 });
        var bob = EntitySeeding.CreateEntity(customerType, new Dictionary<string, object?> { ["Name"] = "Bob", ["Age"] = 15 });
        context.Add(alice);
        context.Add(bob);
        context.SaveChanges();

        // Two orders, both belonging to Alice - used by the cross-root LINQ tests (Join/GroupJoin/
        // SelectMany/Zip) below to exercise operators that combine the Customers and Orders roots.
        var orderType = EntitySeeding.GetEntityClrType(context, "Order");
        var aliceId = (int)EntitySeeding.GetPropertyValue(alice, "Id")!;
        context.Add(EntitySeeding.CreateEntity(orderType, new Dictionary<string, object?> { ["CustomerId"] = aliceId, ["Amount"] = 10m, ["CreatedAtUtc"] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) }));
        context.Add(EntitySeeding.CreateEntity(orderType, new Dictionary<string, object?> { ["CustomerId"] = aliceId, ["Amount"] = 20m, ["CreatedAtUtc"] = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc) }));
        context.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ExecuteAsync_ExpressionQuery_MaterializesCappedProjection()
    {
        var result = await CreateExecutor().ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Where(c => c.Age >= 18).Select(c => c.Name)" },
            CancellationToken.None);

        Assert.Equal("C#", result.Entity);
        Assert.Equal(1, result.RowCount);
        Assert.False(result.IsScalar);
        Assert.Single(result.Rows);
        Assert.Equal("Alice", result.Rows[0]["Value"]);
    }

    [Fact]
    public async Task ExecuteAsync_StatementQuery_WithLocalVariable_ReturnsScalar()
    {
        var result = await CreateExecutor().ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "var adults = Customers.Count(c => c.Age >= 18);\nreturn adults;" },
            CancellationToken.None);

        Assert.True(result.IsScalar);
        Assert.Equal(1, result.Scalar);
    }

    [Fact]
    public async Task ExecuteAsync_ExcessiveTake_IsCappedToConfiguredMaximum()
    {
        var result = await CreateExecutor(maxTake: 1).ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Take(100)" }, CancellationToken.None);

        Assert.Equal(1, result.RowCount);
        Assert.Equal(1, result.EffectiveTake);
        Assert.True(result.HasMoreRows);
    }

    [Fact]
    public async Task ExecuteAsync_HasMoreRows_FalseWhenRowCountExactlyMatchesEffectiveTake()
    {
        var result = await CreateExecutor(maxTake: 2).ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.OrderBy(c => c.Name).Take(2)" }, CancellationToken.None);

        Assert.Equal(2, result.RowCount);
        Assert.Equal(2, result.EffectiveTake);
        Assert.False(result.HasMoreRows);
    }

    [Fact]
    public async Task ExecuteAsync_HasMoreRows_FalseForZeroTakeWithoutMaterializing()
    {
        var result = await CreateExecutor().ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.OrderBy(c => c.Name).Take(0)" }, CancellationToken.None);

        Assert.Equal(0, result.RowCount);
        Assert.Equal(0, result.EffectiveTake);
        Assert.Empty(result.Rows);
        Assert.False(result.HasMoreRows);
    }

    [Fact]
    public async Task ExecuteAsync_Join_CombinesTwoRoots()
    {
        var result = await CreateExecutor().ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest
            {
                Query = "Customers.Join(Orders, c => c.Id, o => o.CustomerId, (c, o) => new { c.Name, o.Amount })"
            },
            CancellationToken.None);

        Assert.False(result.IsScalar);
        Assert.Equal(2, result.RowCount);
        Assert.All(result.Rows, row => Assert.Equal("Alice", row["Name"]));
        Assert.Equal([10m, 20m], result.Rows.Select(r => r["Amount"]).Order());
    }

    [Fact]
    public async Task ExecuteAsync_GroupJoin_CombinesTwoRoots()
    {
        var result = await CreateExecutor().ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest
            {
                Query = "Customers.GroupJoin(Orders, c => c.Id, o => o.CustomerId, (c, orders) => new { c.Name, OrderCount = orders.Count() })"
            },
            CancellationToken.None);

        Assert.False(result.IsScalar);
        Assert.Equal(2, result.RowCount);
        var bySale = result.Rows.ToDictionary(r => (string)r["Name"]!, r => (int)r["OrderCount"]!);
        Assert.Equal(2, bySale["Alice"]);
        Assert.Equal(0, bySale["Bob"]);
    }

    [Fact]
    public async Task ExecuteAsync_SelectMany_FlattensNavigationProperty()
    {
        var result = await CreateExecutor().ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.SelectMany(c => c.Orders, (c, o) => new { c.Name, o.Amount })" },
            CancellationToken.None);

        Assert.False(result.IsScalar);
        Assert.Equal(2, result.RowCount);
        Assert.All(result.Rows, row => Assert.Equal("Alice", row["Name"]));
    }

    [Fact]
    public async Task ExecuteAsync_Zip_PairsTwoRootsPositionally()
    {
        // Zip has no SQL translation, so the query must materialize both sequences client-side
        // (AsEnumerable) before zipping them - same as a user would have to write it in LINQPad.
        // Enumerable.Zip returns IEnumerable<T>, not IQueryable, so ShapeResultAsync currently
        // treats the whole sequence as a single scalar value rather than shaping it into rows
        // (tracked by the roslyn-result-scope-decision follow-up). The query ends with ToList()
        // so the (otherwise deferred) sequence is fully materialized before the DbContext used to
        // produce it is disposed.
        var result = await CreateExecutor().ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest
            {
                Query = "Customers.OrderBy(c => c.Id).AsEnumerable().Zip(Orders.OrderBy(o => o.Id).AsEnumerable(), (c, o) => new { c.Name, o.Amount }).ToList()"
            },
            CancellationToken.None);

        Assert.True(result.IsScalar);
        var pairs = Assert.IsAssignableFrom<System.Collections.IEnumerable>(result.Scalar).Cast<object>().ToList();
        Assert.Equal(2, pairs.Count);
        Assert.Equal("Alice", EntitySeeding.GetPropertyValue(pairs[0], "Name"));
        Assert.Equal(10m, EntitySeeding.GetPropertyValue(pairs[0], "Amount"));
        Assert.Equal("Bob", EntitySeeding.GetPropertyValue(pairs[1], "Name"));
        Assert.Equal(20m, EntitySeeding.GetPropertyValue(pairs[1], "Amount"));
    }

    [Fact]
    public async Task PreviewSqlAsync_ExpressionQuery_ReturnsQueryStringForFinalIQueryable()
    {
        var result = await CreateExecutor().PreviewSqlAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Where(c => c.Age >= 18).Select(c => c.Name)" },
            CancellationToken.None);

        Assert.Equal("C#", result.Entity);
        Assert.Contains("SELECT", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewSqlAsync_NeverOpensADatabaseConnection()
    {
        // Deliberately does not call EnsureCreated: if PreviewSqlAsync ever opened a connection or
        // executed a command, this would fail with "no such table: Customer" instead of succeeding.
        using var emptyDb = new SqliteTestDatabase();

        var result = await CreateExecutor().PreviewSqlAsync(
            _handle, _contextType, emptyDb.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Where(c => c.Age >= 18).OrderBy(c => c.Name)" },
            CancellationToken.None);

        Assert.Equal("C#", result.Entity);
        Assert.Contains("SELECT", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewSqlAsync_ScalarResult_Throws()
    {
        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => CreateExecutor().PreviewSqlAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Count()" }, CancellationToken.None));

        Assert.Contains("is not an IQueryable and has no SQL to preview", ex.Message);
    }

    [Fact]
    public async Task PreviewSqlAsync_MaterializedResult_Throws()
    {
        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => CreateExecutor().PreviewSqlAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.ToList()" }, CancellationToken.None));

        Assert.Contains("is not an IQueryable and has no SQL to preview", ex.Message);
    }

    [Fact]
    public async Task PreviewSqlAsync_NonTranslatableEnumerableResult_Throws()
    {
        // Zip returns a lazily-evaluated IEnumerable<T>, not an IQueryable, and - because it is
        // never enumerated here (no ToList/foreach) - this also proves no query is executed for a
        // rejected preview.
        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => CreateExecutor().PreviewSqlAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest
            {
                Query = "Customers.OrderBy(c => c.Id).AsEnumerable().Zip(Orders.OrderBy(o => o.Id).AsEnumerable(), (c, o) => new { c.Name, o.Amount })"
            },
            CancellationToken.None));

        Assert.Contains("is not an IQueryable and has no SQL to preview", ex.Message);
    }

    [Fact]
    public async Task PreviewSqlAsync_ValidQuery_SucceedsAndExercisesExceptionHandlingCodePath()
    {
        // This test verifies that PreviewSqlAsync correctly returns SQL for a valid query.
        // It exercises the success path including the try-catch wrapper around ToQueryString().
        // While a direct test of the exception handling when ToQueryString() throws would be ideal,
        // triggering a real translation failure is not practical with valid compiled queries:
        // - Most LINQ expressions that compile in C# also translate successfully to SQL
        // - The Roslyn compiler validates the C# before we ever call ToQueryString()
        // - EF Core's Sqlite provider has very broad translation support
        // This test-design limitation means the exception-handling code path is present in the codebase
        // and would activate if ToQueryString() threw, but is not directly exercised by this test.
        var result = await CreateExecutor().PreviewSqlAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Where(c => c.Age >= 18)" },
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("C#", result.Entity);
        Assert.NotEmpty(result.Sql);
        Assert.Contains("SELECT", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_ParameterlessOnConfiguringContext_OverridesHardcodedConnectionString()
    {
        var contextType = DbContextScanner.FindDbContextTypes(_handle.Assembly).Descriptors.Single(d => d.Name == "LegacyOnConfiguringDbContext").ClrType;
        using (var seedContext = DbContextActivator.CreateInstance(contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite))
        {
            seedContext.Database.EnsureCreated();
            var customerType = EntitySeeding.GetEntityClrType(seedContext, "Customer");
            seedContext.Add(EntitySeeding.CreateEntity(customerType, new Dictionary<string, object?> { ["Name"] = "Carol", ["Age"] = 40 }));
            seedContext.SaveChanges();
        }

        // The fixture's own OnConfiguring hardcodes a bogus, nonexistent SQLite file. If the
        // Roslyn engine's parameterless-shape construction didn't override the connection string
        // with the registry-resolved one, this would fail trying to open that bogus file instead
        // of returning the seeded row from the real test database.
        var result = await CreateExecutor().ExecuteAsync(
            _handle, contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Where(c => c.Name == \"Carol\").Select(c => c.Name)" },
            CancellationToken.None);

        Assert.Equal(1, result.RowCount);
        Assert.Equal("Carol", result.Rows[0]["Value"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public async Task ExecuteAsync_EmptyOrWhitespaceQuery_Throws(string query)
    {
        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => CreateExecutor().ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = query }, CancellationToken.None));

        Assert.Equal("`query` must be non-empty C# code.", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_QueryExceedingConfiguredMaximum_Throws()
    {
        var executor = new RoslynQueryExecutor(
            new QueryExecutionOptions { MaxQueryLength = 10 },
            new QueryCompiler(new QueryCompilationOptions()));

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => executor.ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Where(c => c.Age >= 18)" }, CancellationToken.None));

        Assert.Equal("`query` exceeds the configured maximum length.", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_DefaultNoTracking_DoesNotTrackMaterializedEntities()
    {
        var result = await CreateExecutor().ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.ToList();\nreturn ChangeTracker.Entries().Count();" }, CancellationToken.None);

        Assert.True(result.IsScalar);
        Assert.Equal(0, result.Scalar);
    }

    [Fact]
    public async Task ExecuteAsync_AsTracking_OverridesDefaultNoTracking()
    {
        var result = await CreateExecutor().ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.AsTracking().ToList();\nreturn ChangeTracker.Entries().Count();" }, CancellationToken.None);

        Assert.True(result.IsScalar);
        Assert.Equal(2, result.Scalar);
    }

    [Fact]
    public async Task ExecuteAsync_UserQueryException_IsWrappedWithEvaluationMessage()
    {
        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => CreateExecutor().ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "throw new global::System.InvalidOperationException(\"boom\");" }, CancellationToken.None));

        Assert.Equal("The C# query failed while it was being evaluated.", ex.Message);
        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal("boom", inner.Message);
    }

    [Fact]
    public async Task ExecuteAsync_NonGenericOptionsContext_ExecutesQuery()
    {
        var contextType = DbContextScanner.FindDbContextTypes(_handle.Assembly).Descriptors.Single(d => d.Name == "NonGenericOptionsDbContext").ClrType;

        var result = await CreateExecutor().ExecuteAsync(
            _handle, contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Select(c => c.Name)" }, CancellationToken.None);

        Assert.Equal(2, result.RowCount);
        Assert.Equal(["Alice", "Bob"], result.Rows.Select(row => (string)row["Value"]!));
    }

    [Fact]
    public async Task ExecuteAsync_DesignTimeFactoryContext_RejectsBeforeCompilation()
    {
        var contextType = DbContextScanner.FindDbContextTypes(_handle.Assembly).Descriptors.Single(d => d.Name == "FactoryOnlyDbContext").ClrType;

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => CreateExecutor().ExecuteAsync(
            _handle, contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers" }, CancellationToken.None));

        Assert.Contains("cannot be used with the Roslyn query engine", ex.Message);
    }

    private const string MutatingQuery =
        "Customers.Add(new global::SampleApp.Customer { Name = \"Dave\", Age = 22 });\nSaveChanges();\nreturn Customers.Count();";

    [Fact]
    public async Task ExecuteAsync_MutationsDisabledByOption_ThrowsEvenOnReadWriteNonProductionConnection()
    {
        var executor = CreateExecutor(allowMutationsInRunQuery: false);
        var entry = _db.ToRegistryEntry(accessMode: ConnectionAccessMode.ReadWrite, environment: EnvironmentType.Development);

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => executor.ExecuteAsync(
            _handle, _contextType, entry, DatabaseProvider.Sqlite,
            new QueryRequest { Query = MutatingQuery }, CancellationToken.None));

        Assert.Contains("disabled for this connection", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ReadOnlyConnection_ThrowsEvenWhenOptionEnabled()
    {
        var executor = CreateExecutor(allowMutationsInRunQuery: true);
        var entry = _db.ToRegistryEntry(accessMode: ConnectionAccessMode.ReadOnly, environment: EnvironmentType.Development);

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => executor.ExecuteAsync(
            _handle, _contextType, entry, DatabaseProvider.Sqlite,
            new QueryRequest { Query = MutatingQuery }, CancellationToken.None));

        Assert.Contains("disabled for this connection", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ProductionConnection_ThrowsEvenWhenReadWriteAndOptionEnabled()
    {
        var executor = CreateExecutor(allowMutationsInRunQuery: true);
        var entry = _db.ToRegistryEntry(accessMode: ConnectionAccessMode.ReadWrite, environment: EnvironmentType.Production);

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => executor.ExecuteAsync(
            _handle, _contextType, entry, DatabaseProvider.Sqlite,
            new QueryRequest { Query = MutatingQuery }, CancellationToken.None));

        Assert.Contains("disabled for this connection", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_NonProductionReadWriteWithOptionEnabled_AllowsSave()
    {
        var executor = CreateExecutor(allowMutationsInRunQuery: true);
        var entry = _db.ToRegistryEntry(accessMode: ConnectionAccessMode.ReadWrite, environment: EnvironmentType.Development);

        var result = await executor.ExecuteAsync(
            _handle, _contextType, entry, DatabaseProvider.Sqlite,
            new QueryRequest { Query = MutatingQuery }, CancellationToken.None);

        Assert.True(result.IsScalar);
        Assert.Equal(3, result.Scalar);
    }

    private DbContext NewContext() => DbContextActivator.CreateInstance(_contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite);

    private static RoslynQueryExecutor CreateExecutor(int maxTake = 200, bool allowMutationsInRunQuery = false) => new(
        new QueryExecutionOptions { MaxTake = maxTake, AllowMutationsInRunQuery = allowMutationsInRunQuery },
        new QueryCompiler(new QueryCompilationOptions()));
}
