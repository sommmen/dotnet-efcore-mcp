using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Schema;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace DotnetEfCoreMcp.Server.Tests.AssemblyLoading;

/// <summary>Regression coverage for a bug where a target DbContext referencing a type from an
/// assembly missing from <see cref="TargetAssemblyLoadContext"/>'s shared assembly list (here,
/// Microsoft.Extensions.Logging.Abstractions, via <c>RelationalEventId.CommandExecuting</c> in a
/// <c>ConfigureWarnings</c> call) blew up with <see cref="MissingFieldException"/>. This happened
/// because the target's isolated <see cref="System.Runtime.Loader.AssemblyLoadContext"/> loaded a
/// second, type-incompatible copy of that assembly instead of reusing the server's own already-
/// loaded copy, so the two ALCs disagreed about the identity of the <c>EventId</c> type carried by
/// the field. See the real-world repro against OPG.Platform.Commerce.Core.DAL that originally
/// surfaced this.</summary>
public sealed class TargetAssemblyLoadContextSharedTypesTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void GetSchema_ContextConfiguringRelationalEventIdWarnings_DoesNotThrowMissingFieldException()
    {
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);
        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors
            .Single(d => d.Name == "WarningsConfiguredDbContext");

        using var context = DbContextActivator.CreateInstance(descriptor.ClrType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite);

        // Building the schema forces EF Core to build its internal service provider and touch
        // the model, which is what originally triggered the MissingFieldException deep inside
        // EF Core's logging infrastructure when RelationalEventId.CommandExecuting (an EventId
        // field from Microsoft.Extensions.Logging.Abstractions) resolved to a type loaded twice
        // across two different AssemblyLoadContexts.
        var schema = SchemaBuilder.Build(context);

        Assert.Contains(schema.Entities, e => e.Name == "Customer");
    }

    [Fact]
    public void CreateInstance_ContextConfiguringRelationalEventIdWarnings_CanEnsureCreatedAndQuery()
    {
        var service = new AssemblyLoaderService();
        var handle = service.Load(FixturePaths.SampleAppDllPath);
        var descriptor = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors
            .Single(d => d.Name == "WarningsConfiguredDbContext");

        using var context = DbContextActivator.CreateInstance(descriptor.ClrType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite);
        context.Database.EnsureCreated();

        // Actually issuing a command is what pushes EF Core through the ConfigureWarnings/logging
        // pipeline that reads the RelationalEventId.CommandExecuting field; before the fix this
        // threw MissingFieldException instead of returning an empty result set.
        var customerType = EntitySeeding.GetEntityClrType(context, "Customer");
        var customer = EntitySeeding.CreateEntity(customerType, new Dictionary<string, object?> { ["Name"] = "Dana", ["Age"] = 40 });
        context.Add(customer);
        context.SaveChanges();

        var setMethod = typeof(DbContext).GetMethods().Single(m => m.Name == nameof(DbContext.Set) && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        var dbSet = (IQueryable)setMethod.MakeGenericMethod(customerType).Invoke(context, null)!;
        var savedCount = dbSet.Cast<object>().Count();

        Assert.Equal(1, savedCount);
    }
}
