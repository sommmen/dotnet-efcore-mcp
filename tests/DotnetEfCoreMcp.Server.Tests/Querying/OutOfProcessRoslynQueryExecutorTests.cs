using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace DotnetEfCoreMcp.Server.Tests.Querying;

public sealed class OutOfProcessRoslynQueryExecutorTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();
    private readonly LoadedAssemblyHandle _handle;
    private readonly Type _contextType;

    public OutOfProcessRoslynQueryExecutorTests()
    {
        _handle = new AssemblyLoaderService().Load(FixturePaths.SampleAppDllPath);
        _contextType = DbContextScanner.FindDbContextTypes(_handle.Assembly).Descriptors.Single(d => d.Name == "SampleAppDbContext").ClrType;

        using var context = DbContextActivator.CreateInstance(_contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite);
        context.Database.EnsureCreated();
        var customerType = EntitySeeding.GetEntityClrType(context, "Customer");
        context.Add(EntitySeeding.CreateEntity(customerType, new Dictionary<string, object?> { ["Name"] = "Alice", ["Age"] = 30 }));
        context.Add(EntitySeeding.CreateEntity(customerType, new Dictionary<string, object?> { ["Name"] = "Bob", ["Age"] = 15 }));
        context.SaveChanges();
    }

    public void Dispose()
    {
        _handle.Unload();
        _db.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_SequenceQuery_MaterializesRowsInIsolatedHost()
    {
        var result = await CreateExecutor().ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers.Where(c => c.Age >= 18).Select(c => c.Name)" }, CancellationToken.None);

        Assert.False(result.IsScalar);
        Assert.Equal(1, result.RowCount);
        Assert.Equal("Alice", result.Rows.Single()["Value"]);
    }

    [Fact]
    public async Task ExecuteAsync_StatementQuery_ReturnsScalarFromIsolatedHost()
    {
        var result = await CreateExecutor().ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "var adults = Customers.Count(c => c.Age >= 18);\nreturn adults;" }, CancellationToken.None);

        Assert.True(result.IsScalar);
        Assert.Equal(1, result.Scalar);
    }

    [Fact]
    public async Task ExecuteAsync_MissingHost_ThrowsConfigurationError()
    {
        var executor = new OutOfProcessRoslynQueryExecutor(new QueryExecutionOptions
        {
            Engine = QueryEngine.Roslyn,
            Mode = QueryExecutionMode.OutOfProcess,
            OutOfProcessHostPath = Path.Combine(AppContext.BaseDirectory, "missing-query-host.dll"),
        });

        var exception = await Assert.ThrowsAsync<QueryExecutionException>(() => executor.ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Customers" }, CancellationToken.None));

        Assert.Equal("The configured out-of-process query host was not found.", exception.Message);
    }

    private static OutOfProcessRoslynQueryExecutor CreateExecutor() => new(new QueryExecutionOptions
    {
        Engine = QueryEngine.Roslyn,
        Mode = QueryExecutionMode.OutOfProcess,
        OutOfProcessHostPath = FixturePaths.QueryHostDllPath,
    });
}
