using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace DotnetEfCoreMcp.Server.Tests.Querying;

/// <summary>End-to-end coverage for <c>run_query</c> / <c>preview_query_sql</c> against a target
/// that references a newer EF Core patch than the server (issue #118).
///
/// <para>Compiling is only half the story: the emitted assembly is compiled against the target's
/// newer EF Core reference but binds at runtime to the server's single shared copy (see
/// <see cref="SharedAssemblyResolver"/>), so these tests assert the two views actually agree well
/// enough to run a query and translate it to SQL - which is what the user observes, and what
/// stayed broken after #116 fixed discovery.</para></summary>
public sealed class RoslynQueryExecutorNewerEfCoreVersionTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();
    private readonly LoadedAssemblyHandle _handle;
    private readonly Type _contextType;

    public RoslynQueryExecutorNewerEfCoreVersionTests()
    {
        _handle = new AssemblyLoaderService().Load(FixturePaths.NewerEfCoreAppDllPath);
        _contextType = DbContextScanner.FindDbContextTypes(_handle.Assembly).Descriptors
            .Single(d => d.Name == "CatalogDbContext").ClrType;

        using var context = DbContextActivator.CreateInstance(_contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite);
        context.Database.EnsureCreated();
        var productType = EntitySeeding.GetEntityClrType(context, "Product");
        context.Add(EntitySeeding.CreateEntity(productType, new Dictionary<string, object?> { ["Name"] = "Widget", ["Price"] = 9.99m }));
        context.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ExecuteAsync_TargetReferencesNewerEfCore_ReturnsRows()
    {
        var result = await CreateExecutor().ExecuteAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Products.Select(p => p.Name)", RootEntityName = "Products" },
            CancellationToken.None);

        Assert.Equal(1, result.RowCount);
        Assert.Equal("Widget", result.Rows[0]["Value"]);
    }

    [Fact]
    public async Task PreviewSqlAsync_TargetReferencesNewerEfCore_ReturnsTranslatedSql()
    {
        var result = await CreateExecutor().PreviewSqlAsync(
            _handle, _contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite,
            new QueryRequest { Query = "Products.Where(p => p.Price > 1m)", RootEntityName = "Products" },
            CancellationToken.None);

        Assert.Contains("SELECT", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static RoslynQueryExecutor CreateExecutor() => new(
        new QueryExecutionOptions(),
        new QueryCompiler(new QueryCompilationOptions()));
}
