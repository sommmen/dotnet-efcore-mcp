using System.Diagnostics;
using System.Reflection;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
        var firstOrder = EntitySeeding.CreateEntity(orderType, new Dictionary<string, object?> { ["CustomerId"] = aliceId, ["Amount"] = 10m, ["CreatedAtUtc"] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        var secondOrder = EntitySeeding.CreateEntity(orderType, new Dictionary<string, object?> { ["CustomerId"] = aliceId, ["Amount"] = 20m, ["CreatedAtUtc"] = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc) });
        context.AddRange(firstOrder, secondOrder);
        context.SaveChanges();

        var orderLineType = EntitySeeding.GetEntityClrType(context, "OrderLine");
        var firstOrderId = (int)EntitySeeding.GetPropertyValue(firstOrder, "Id")!;
        var secondOrderId = (int)EntitySeeding.GetPropertyValue(secondOrder, "Id")!;
        context.AddRange(
            EntitySeeding.CreateEntity(orderLineType, new Dictionary<string, object?> { ["OrderId"] = firstOrderId, ["Product"] = "A" }),
            EntitySeeding.CreateEntity(orderLineType, new Dictionary<string, object?> { ["OrderId"] = firstOrderId, ["Product"] = "B" }),
            EntitySeeding.CreateEntity(orderLineType, new Dictionary<string, object?> { ["OrderId"] = firstOrderId, ["Product"] = "C" }),
            EntitySeeding.CreateEntity(orderLineType, new Dictionary<string, object?> { ["OrderId"] = secondOrderId, ["Product"] = "D" }),
            EntitySeeding.CreateEntity(orderLineType, new Dictionary<string, object?> { ["OrderId"] = secondOrderId, ["Product"] = "E" }));
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
    public async Task PreviewSqlAsync_ValidQuery_SucceedsWithExceptionWrapperPresent()
    {
        // This test verifies that PreviewSqlAsync correctly returns SQL for a valid query.
        // It exercises the success path including the try-catch wrapper around ToQueryString().
        // While a direct test of the exception handling when ToQueryString() throws would be ideal,
        // triggering a real translation failure is not practical with valid compiled queries:
        // - Most LINQ expressions that compile in C# also translate successfully to SQL
        // - The Roslyn compiler validates the C# before we ever call ToQueryString()
        // - EF Core's Sqlite provider has very broad translation support
        // This test-design limitation means the exception-handling code path is present in the codebase
        // (the try-catch wrapper around ToQueryString() would activate if it threw), but the catch
        // branch itself is not directly exercised by this test.
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
    public async Task ExecuteAsync_StructuredInclude_CollectionRowsAreBoundedInExecutedSqlBeforeMaterialization()
    {
        var commands = new List<string>();
        using var listener = new SqlCommandDiagnosticListener(commands);
        var result = await CreateExecutor(maxIncludedCollectionItems: 2).ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.OrderBy(c => c.Id)", Include = ["Orders"] },
            CancellationToken.None);

        Assert.Equal("C#", result.Entity);
        Assert.Contains(commands, command =>
            command.Contains("FROM \"Orders\"", StringComparison.OrdinalIgnoreCase)
            && command.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
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
    public async Task ExecuteAsync_QueryAtMaxExpressionNodesLimit_Succeeds()
    {
        const string query = "Customers.Where(c => c.Age >= 18)";
        var (nodeCount, _) = ComputeQueryComplexity(query);
        
        var executor = new RoslynQueryExecutor(
            new QueryExecutionOptions { MaxExpressionNodes = nodeCount },
            new QueryCompiler(new QueryCompilationOptions()));

        var result = await executor.ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = query }, CancellationToken.None);

        Assert.Equal(1, result.RowCount);
    }

    [Fact]
    public async Task ExecuteAsync_QueryExceedingMaxExpressionNodes_ThrowsWithoutDatabaseAccess()
    {
        const string query = "Customers.Where(c => c.Age >= 18)";
        var (nodeCount, _) = ComputeQueryComplexity(query);
        
        // Deliberately does not call EnsureCreated: if the check ran after provider work began, this
        // would fail with "no such table" instead of the sanitized complexity error.
        using var emptyDb = new SqliteTestDatabase();
        var executor = new RoslynQueryExecutor(
            new QueryExecutionOptions { MaxExpressionNodes = nodeCount - 1 },
            new QueryCompiler(new QueryCompilationOptions()));

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => executor.ExecuteAsync(
            _handle, _contextType, emptyDb.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = query }, CancellationToken.None));

        Assert.Contains($"exceeding the configured maximum of {nodeCount - 1} (MaxExpressionNodes)", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Age", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewSqlAsync_QueryExceedingMaxExpressionNodes_ThrowsWithoutDatabaseAccess()
    {
        using var emptyDb = new SqliteTestDatabase();
        var executor = new RoslynQueryExecutor(
            new QueryExecutionOptions { MaxExpressionNodes = 12 },
            new QueryCompiler(new QueryCompilationOptions()));

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => executor.PreviewSqlAsync(
            _handle, _contextType, emptyDb.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Where(c => c.Age >= 18)" }, CancellationToken.None));

        Assert.Contains("exceeding the configured maximum of 12 (MaxExpressionNodes)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_QueryAtMaxExpressionDepthLimit_Succeeds()
    {
        const string query = "Customers.Where(c => c.Age >= 18)";
        var (_, maxDepth) = ComputeQueryComplexity(query);
        
        var executor = new RoslynQueryExecutor(
            new QueryExecutionOptions { MaxExpressionDepth = maxDepth },
            new QueryCompiler(new QueryCompilationOptions()));

        var result = await executor.ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = query }, CancellationToken.None);

        Assert.Equal(1, result.RowCount);
    }

    [Fact]
    public async Task ExecuteAsync_QueryExceedingMaxExpressionDepth_ThrowsWithoutDatabaseAccess()
    {
        const string query = "Customers.Where(c => c.Age >= 18)";
        var (_, maxDepth) = ComputeQueryComplexity(query);
        
        using var emptyDb = new SqliteTestDatabase();
        var executor = new RoslynQueryExecutor(
            new QueryExecutionOptions { MaxExpressionDepth = maxDepth - 1 },
            new QueryCompiler(new QueryCompilationOptions()));

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => executor.ExecuteAsync(
            _handle, _contextType, emptyDb.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = query }, CancellationToken.None));

        Assert.Contains($"exceeding the configured maximum of {maxDepth - 1} (MaxExpressionDepth)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_QueryAtMaxQueryOperatorsLimit_Succeeds()
    {
        // Exactly two query operators: Where, Select.
        var executor = new RoslynQueryExecutor(
            new QueryExecutionOptions { MaxQueryOperators = 2 },
            new QueryCompiler(new QueryCompilationOptions()));

        var result = await executor.ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Where(c => c.Age >= 18).Select(c => c.Name)" }, CancellationToken.None);

        Assert.Equal(1, result.RowCount);
    }

    [Fact]
    public async Task ExecuteAsync_QueryExceedingMaxQueryOperators_ThrowsWithoutDatabaseAccess()
    {
        using var emptyDb = new SqliteTestDatabase();
        var executor = new RoslynQueryExecutor(
            new QueryExecutionOptions { MaxQueryOperators = 1 },
            new QueryCompiler(new QueryCompilationOptions()));

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => executor.ExecuteAsync(
            _handle, _contextType, emptyDb.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Where(c => c.Age >= 18).Select(c => c.Name)" }, CancellationToken.None));

        Assert.Contains("exceeding the configured maximum of 1 (MaxQueryOperators)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_QueryAtMaxIncludedCollectionItemsLimit_Succeeds()
    {
        var executor = new RoslynQueryExecutor(
            new QueryExecutionOptions { MaxIncludedCollectionItems = 1 },
            new QueryCompiler(new QueryCompilationOptions()));

        var result = await executor.ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Include(c => c.Orders)" }, CancellationToken.None);

        Assert.Equal(2, result.RowCount);
    }

    [Fact]
    public async Task ExecuteAsync_QueryExceedingMaxIncludedCollectionItems_ThrowsWithoutDatabaseAccess()
    {
        using var emptyDb = new SqliteTestDatabase();
        var executor = new RoslynQueryExecutor(
            new QueryExecutionOptions { MaxIncludeCount = 1 },
            new QueryCompiler(new QueryCompilationOptions()));

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => executor.ExecuteAsync(
            _handle, _contextType, emptyDb.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Include(c => c.Orders).ThenInclude(o => o.Customer)" }, CancellationToken.None));

        Assert.Contains("exceeding the configured maximum of 1 (MaxIncludeCount)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_QueryExceedingMultipleLimitsAtOnce_ThrowsNamingOnlyOneLimit()
    {
        // Exceeds MaxExpressionNodes, MaxExpressionDepth, and MaxQueryOperators simultaneously; the
        // validator must still fail closed and name exactly one limit rather than leaking anything
        // about the query itself.
        using var emptyDb = new SqliteTestDatabase();
        var executor = new RoslynQueryExecutor(
            new QueryExecutionOptions
            {
                MaxExpressionNodes = 5,
                MaxExpressionDepth = 3,
                MaxQueryOperators = 1,
            },
            new QueryCompiler(new QueryCompilationOptions()));

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => executor.ExecuteAsync(
            _handle, _contextType, emptyDb.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Where(c => c.Age >= 18).Select(c => c.Name)" }, CancellationToken.None));

        // Verify exactly one limit identifier appears (fail closed: name only one limit).
        var limitIdentifiers = new[] { "MaxExpressionNodes", "MaxExpressionDepth", "MaxQueryOperators", "MaxIncludedCollectionItems" };
        var matchCount = limitIdentifiers.Count(id => ex.Message.Contains(id, StringComparison.Ordinal));
        Assert.Equal(1, matchCount);

        // Verify query text is not leaked.
        Assert.DoesNotContain("Age", ex.Message, StringComparison.Ordinal);
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

    /// <summary>Computes the query complexity metrics (node count and max depth) for a query expression,
    /// using the same algorithm as <see cref="QueryComplexityValidator"/>. This is used to derive
    /// test boundary values dynamically rather than hard-coding them, ensuring tests remain correct
    /// even if the Roslyn parser's tree shape changes in future SDK updates.</summary>
    private static (int NodeCount, int MaxDepth) ComputeQueryComplexity(string query)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(query);
        var root = (CompilationUnitSyntax)syntaxTree.GetRoot();
        
        ExpressionSyntax? expression = null;
        
        // Try to extract the expression from the parsed tree
        if (root.Members.Count > 0 && root.Members[0] is GlobalStatementSyntax globalStmt)
        {
            if (globalStmt.Statement is ExpressionStatementSyntax exprStmt)
            {
                expression = exprStmt.Expression;
            }
        }

        if (expression is null)
            return (0, 0);

        var nodeCount = 0;
        var maxDepth = 0;
        var stack = new Stack<(SyntaxNode Node, int Depth)>();
        stack.Push((expression, 1));

        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            nodeCount++;
            if (depth > maxDepth) maxDepth = depth;

            foreach (var child in node.ChildNodes().Reverse())
            {
                stack.Push((child, depth + 1));
            }
        }

        return (nodeCount, maxDepth);
    }

    [Fact]
    public async Task ExecuteAsync_StructuredInclude_ProjectsRequestedNestedBranchesWithCollectionCaps()
    {
        var result = await CreateExecutor(maxIncludedCollectionItems: 2).ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.OrderBy(c => c.Id)", Include = ["Orders.OrderLines"] }, CancellationToken.None);

        Assert.Equal(2, result.RowCount);
        var alice = Assert.Single(result.Rows, row => (string)row["Name"]! == "Alice");
        var orders = Assert.IsType<List<Dictionary<string, object?>>>(alice["Orders"]);
        Assert.Equal(2, orders.Count);
        Assert.Equal([10m, 20m], orders.Select(order => (decimal)order["Amount"]!));
        Assert.All(orders, order => Assert.True(order.ContainsKey("OrderLines")));
        Assert.Equal(["A", "B"], Assert.IsType<List<Dictionary<string, object?>>>(orders[0]["OrderLines"]!).Select(line => (string)line["Product"]!));
        Assert.Equal(["D", "E"], Assert.IsType<List<Dictionary<string, object?>>>(orders[1]["OrderLines"]!).Select(line => (string)line["Product"]!));
        var bob = Assert.Single(result.Rows, row => (string)row["Name"]! == "Bob");
        Assert.Empty(Assert.IsType<List<Dictionary<string, object?>>>(bob["Orders"]));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    public async Task ExecuteAsync_StructuredInclude_AppliesCollectionCapBeforeProjection(int cap, int expectedCount)
    {
        var result = await CreateExecutor(maxIncludedCollectionItems: cap).ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Where(c => c.Name == \"Alice\")", Include = ["Orders"] }, CancellationToken.None);

        var orders = Assert.IsType<List<Dictionary<string, object?>>>(Assert.Single(result.Rows)["Orders"]);
        Assert.Equal(expectedCount, orders.Count);
        Assert.Equal(Enumerable.Range(1, expectedCount), orders.Select(order => (int)order["Id"]!));
    }

    [Theory]
    [InlineData("Orders.Unknown")]
    [InlineData("Orders.Amount")]
    [InlineData("Orders.Customer.Orders")]
    [InlineData("Orders..Customer")]
    public async Task ExecuteAsync_StructuredInclude_RejectsInvalidModelPathsBeforeExecution(string include)
    {
        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => CreateExecutor().ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers", Include = [include] }, CancellationToken.None));

        Assert.Contains("Include", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_StructuredInclude_EnforcesConfiguredDepthAndCount()
    {
        var depthException = await Assert.ThrowsAsync<QueryExecutionException>(() => CreateExecutor(maxIncludeDepth: 1).ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers", Include = ["Orders.OrderLines"] }, CancellationToken.None));
        Assert.Contains("MaxIncludeDepth", depthException.Message);

        var countException = await Assert.ThrowsAsync<QueryExecutionException>(() => CreateExecutor(maxIncludeCount: 1).ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers", Include = ["Orders", "Orders.OrderLines"] }, CancellationToken.None));
        Assert.Contains("MaxIncludeCount", countException.Message);
    }

    [Fact]
    public async Task ExecuteAsync_StructuredInclude_PreservesRootPaging()
    {
        var result = await CreateExecutor().ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.OrderBy(c => c.Id).Take(1)", Include = ["Orders"] }, CancellationToken.None);

        Assert.Single(result.Rows);
        Assert.Equal("Alice", result.Rows[0]["Name"]);
        Assert.Equal(2, Assert.IsType<List<Dictionary<string, object?>>>(result.Rows[0]["Orders"]).Count);
    }

    private DbContext NewContext() => DbContextActivator.CreateInstance(_contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite);

    private sealed class SqlCommandDiagnosticListener : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly List<string> _commands;
        private readonly IDisposable _allListenersSubscription;
        private IDisposable? _efCoreSubscription;

        public SqlCommandDiagnosticListener(List<string> commands)
        {
            _commands = commands;
            _allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Microsoft.EntityFrameworkCore")
            {
                _efCoreSubscription = listener.Subscribe(this);
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key.EndsWith("CommandExecuting", StringComparison.Ordinal)
                && value.Value is Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData command)
            {
                _commands.Add(command.Command.CommandText);
            }
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void Dispose()
        {
            _efCoreSubscription?.Dispose();
            _allListenersSubscription.Dispose();
        }
    }

    private static RoslynQueryExecutor CreateExecutor(int maxTake = 200, bool allowMutationsInRunQuery = false, int maxIncludeDepth = 3, int maxIncludeCount = 5, int maxIncludedCollectionItems = 5) => new(
        new QueryExecutionOptions { MaxTake = maxTake, AllowMutationsInRunQuery = allowMutationsInRunQuery, MaxIncludeDepth = maxIncludeDepth, MaxIncludeCount = maxIncludeCount, MaxIncludedCollectionItems = maxIncludedCollectionItems },
        new QueryCompiler(new QueryCompilationOptions()));
}
