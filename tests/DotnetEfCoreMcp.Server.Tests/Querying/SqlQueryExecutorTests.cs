using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetEfCoreMcp.Server.Tests.Querying;

public sealed class SqlQueryExecutorTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();
    private readonly Type _contextType;

    public SqlQueryExecutorTests()
    {
        var handle = new AssemblyLoaderService().Load(FixturePaths.SampleAppDllPath);
        _contextType = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors.Single(d => d.Name == "SampleAppDbContext").ClrType;

        using var context = NewContext();
        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw("INSERT INTO Customers (Name, Age) VALUES ('Alice', 30), ('Bob', 15), ('Carol', 42)");
    }

    public void Dispose() => _db.Dispose();

    private DbContext NewContext() => DbContextActivator.CreateInstance(_contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite);

    [Fact]
    public async Task ExecuteAsync_ParameterizedSelect_ReturnsMatchedRow()
    {
        using var context = NewContext();
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(
            context,
            new SqlQueryRequest { Sql = "SELECT Name, Age FROM Customers WHERE Name = @p0", Parameters = ["Alice"] },
            30,
            CancellationToken.None);

        var row = Assert.Single(result.Rows);
        Assert.Equal("Alice", row["Name"]);
        Assert.Equal(30L, row["Age"]);
        Assert.Null(result.AffectedRows);
    }

    [Fact]
    public async Task ExecuteAsync_SelectCapsReturnedRowsAndIndicatesMore()
    {
        using var context = NewContext();
        var executor = CreateExecutor(maxRows: 2);

        var result = await executor.ExecuteAsync(context, new SqlQueryRequest { Sql = "SELECT Name FROM Customers ORDER BY Name" }, 30, CancellationToken.None);

        Assert.Equal(2, result.ReturnedRowCount);
        Assert.True(result.HasMoreRows);
        Assert.Equal(2, result.MaxRows);
    }

    [Fact]
    public async Task ExecuteAsync_NonQuery_ReturnsAffectedRowCount()
    {
        using var context = NewContext();
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(context, new SqlQueryRequest { Sql = "UPDATE Customers SET Age = 31 WHERE Name = @p0", Parameters = ["Alice"] }, 30, CancellationToken.None);

        Assert.Empty(result.Rows);
        Assert.Equal(1, result.AffectedRows);
    }

    private static SqlQueryExecutor CreateExecutor(int maxRows = 200) =>
        new(new RawSqlExecutionOptions { Enabled = true, MaxRows = maxRows }, NullLogger<SqlQueryExecutor>.Instance);
}
