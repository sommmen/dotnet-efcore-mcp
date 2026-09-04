using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetEfCoreMcp.Server.Tests.Querying;

/// <summary>Regression coverage for a context with a very large number of public DbSets (like OPG
/// Platform's CommerceDbContext, which exposes 136): once the total exceeds Dynamic LINQ's
/// built-in Func<>/Action<> delegate arities (16 parameters), naively registering every DbSet as
/// an extra lambda parameter forces System.Linq.Dynamic.Core to emit a custom delegate type via
/// Reflection.Emit into a non-collectible dynamic assembly, which cannot reference entity types
/// loaded into this context's collectible AssemblyLoadContext and fails with a
/// NotSupportedException. QueryExecutor instead only registers DbSets actually mentioned in the
/// query text (see the comment above the otherDbSets filter in
/// QueryExecutor.ExecuteAsyncCore); these tests pin that behavior against the
/// <see cref="ManyDbSetsApp.ManyDbSetsAppDbContext"/> fixture, which exposes 22 public DbSets.</summary>
public sealed class QueryExecutorManyDbSetsTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();
    private readonly Type _widgetType;
    private readonly Type _gadgetType;
    private readonly Type _contextType;

    public QueryExecutorManyDbSetsTests()
    {
        var handle = new AssemblyLoaderService().Load(FixturePaths.ManyDbSetsAppDllPath);
        _contextType = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors.Single(d => d.Name == "ManyDbSetsAppDbContext").ClrType;
        using var context = NewContext();
        context.Database.EnsureCreated();
        _widgetType = EntitySeeding.GetEntityClrType(context, "Widget");
        _gadgetType = EntitySeeding.GetEntityClrType(context, "Gadget");
        context.Add(EntitySeeding.CreateEntity(_widgetType, new Dictionary<string, object?> { ["Name"] = "Alpha" }));
        context.Add(EntitySeeding.CreateEntity(_widgetType, new Dictionary<string, object?> { ["Name"] = "Beta" }));
        context.SaveChanges();
        context.Add(EntitySeeding.CreateEntity(_gadgetType, new Dictionary<string, object?> { ["Label"] = "Gamma" }));
        context.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private DbContext NewContext() => DbContextActivator.CreateInstance(_contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite);

    private static QueryExecutor CreateExecutor() => new(new QueryExecutionOptions { MaxTake = 200, DefaultTake = 50, MaxQueryOperators = 20 }, NullLogger<QueryExecutor>.Instance);

    [Fact]
    public void Fixture_ExposesMoreThanSixteenPublicDbSets()
    {
        // Guards the premise of these tests: if the fixture ever regresses below Dynamic LINQ's
        // 16-parameter Func<>/Action<> ceiling, the tests below would no longer exercise the
        // Reflection.Emit failure path they exist to pin.
        var dbSetPropertyCount = _contextType.GetProperties()
            .Count(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>));
        Assert.True(dbSetPropertyCount > 16, $"Expected more than 16 public DbSets, found {dbSetPropertyCount}.");
    }

    [Fact]
    public async Task ExecuteAsync_QueryingSingleDbSet_SucceedsDespiteManyOtherDbSetsOnContext()
    {
        using var context = NewContext();
        var result = await CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = "Widgets.Where(w => w.Name == \"Alpha\")" }, 30, CancellationToken.None);
        Assert.Single(result.Rows);
    }

    [Fact]
    public async Task ExecuteAsync_UnionOfTwoNamedDbSets_SucceedsDespiteManyOtherDbSetsOnContext()
    {
        // Exercises the otherDbSets registration path itself (not just the zero-extra-parameter
        // case above): the query text mentions exactly one other DbSet by name, so exactly one
        // extra lambda parameter should be registered, never all 21 of the others.
        using var context = NewContext();
        var result = await CreateExecutor().ExecuteAsync(context, new QueryRequest { Query = "Widgets.Select(w => w.Name).Union(Gadgets.Select(g => g.Label))" }, 30, CancellationToken.None);
        Assert.Equal(3, result.RowCount);
    }
}
