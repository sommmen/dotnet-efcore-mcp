using System.Text.Json;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Mutations;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetEfCoreMcp.Server.Tests.Mutations;

public sealed class EntityMutationExecutorTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();
    private readonly Type _contextType;
    private readonly EntityMutationExecutor _executor = new(NullLogger<EntityMutationExecutor>.Instance);

    public EntityMutationExecutorTests()
    {
        var handle = new AssemblyLoaderService().Load(FixturePaths.SampleAppDllPath);
        _contextType = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors
            .Single(descriptor => descriptor.Name == "SampleAppDbContext").ClrType;
        using var context = NewContext();
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task InsertUpdateAndDelete_ExecuteOneEntityMutationEach()
    {
        using var context = NewContext();
        var insert = await _executor.ExecuteAsync(context, new EntityMutationRequest(
            EntityMutationOperation.Insert, "Customer", Values: Values("Name", "Ada", "Age", 37, "Version", 1)), default);

        Assert.Equal("insert", insert.Operation);
        Assert.Equal(1, insert.AffectedRows);
        var id = Assert.IsType<int>(insert.Values!["Id"]);

        var update = await _executor.ExecuteAsync(context, new EntityMutationRequest(
            EntityMutationOperation.Update, "Customer", Values("Id", id), Values("Age", 38), Values("Version", 1)), default);

        Assert.Equal("update", update.Operation);
        Assert.Equal(1, update.AffectedRows);

        var delete = await _executor.ExecuteAsync(context, new EntityMutationRequest(
            EntityMutationOperation.Delete, "Customer", Values("Id", id), Concurrency: Values("Version", 1)), default);

        Assert.Equal("delete", delete.Operation);
        Assert.Equal(1, delete.AffectedRows);
    }

    [Fact]
    public async Task Update_WithMissingConcurrencyToken_RejectsBeforeWriting()
    {
        using var context = NewContext();
        await Assert.ThrowsAsync<MutationExecutionException>(() => _executor.ExecuteAsync(context,
            new EntityMutationRequest(EntityMutationOperation.Update, "Customer", Values("Id", 1), Values("Age", 38)), default));
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Update_WithUnknownOrPrimaryKeyValue_RejectsBeforeWriting()
    {
        using var context = NewContext();
        await Assert.ThrowsAsync<MutationExecutionException>(() => _executor.ExecuteAsync(context,
            new EntityMutationRequest(EntityMutationOperation.Update, "Customer", Values("Id", 1), Values("Id", 2), Values("Version", 1)), default));
        await Assert.ThrowsAsync<MutationExecutionException>(() => _executor.ExecuteAsync(context,
            new EntityMutationRequest(EntityMutationOperation.Update, "Customer", Values("Id", 1), Values("Orders", 2), Values("Version", 1)), default));
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Delete_WhenEntityDoesNotExist_ReturnsConflict()
    {
        using var context = NewContext();
        var result = await _executor.ExecuteAsync(context, new EntityMutationRequest(
            EntityMutationOperation.Delete, "Customer", Values("Id", -1), Concurrency: Values("Version", 1)), default);

        Assert.True(result.IsConflict);
        Assert.Equal(0, result.AffectedRows);
    }

    private DbContext NewContext()
        => DbContextActivator.CreateInstance(_contextType, _db.ToRegistryEntry(accessMode: ConnectionAccessMode.ReadWrite), DatabaseProvider.Sqlite);

    private static Dictionary<string, JsonElement> Values(params object[] pairs)
    {
        var values = new Dictionary<string, JsonElement>();
        for (var index = 0; index < pairs.Length; index += 2)
        {
            values.Add((string)pairs[index], JsonSerializer.SerializeToElement(pairs[index + 1]));
        }
        return values;
    }

    public void Dispose() => _db.Dispose();
}
