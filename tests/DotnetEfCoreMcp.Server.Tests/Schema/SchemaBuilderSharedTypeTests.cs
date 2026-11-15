using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Schema;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.Schema;

public sealed class SchemaBuilderSharedTypeTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private SchemaDto BuildSchema()
    {
        var handle = new AssemblyLoaderService().Load(FixturePaths.ModelShapesAppDllPath);
        var contextType = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors.Single().ClrType;
        using var context = DbContextActivator.CreateInstance(contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite);
        return SchemaBuilder.Build(context);
    }

    [Fact]
    public void Build_SharedTypeEntities_AreNamedByTheirEfEntityNameAndAreDistinct()
    {
        var schema = BuildSchema();

        var names = schema.Entities.Select(e => e.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(names, name => name.Contains('`'));
        Assert.Contains("CategoryPost", names);
        Assert.Contains("PostTag", names);
    }

    [Fact]
    public void Build_SharedTypeEntities_AreReachableThroughGetEntitySchemaLookup()
    {
        var schema = BuildSchema();

        var join = SchemaSlicer.FindEntity(schema, "PostTag", NoOpSchemaAccessPolicy.Instance);

        Assert.NotNull(join);
        Assert.Equal("PostTag", join.TableName);
        Assert.True(join.ClrFullName!.Length < 100, join.ClrFullName);
    }

    [Fact]
    public void Build_GenericEntity_UsesAReadableName()
    {
        var schema = BuildSchema();

        var audit = Assert.Single(schema.Entities, e => e.TableName == "Audits");

        Assert.Equal("Audit<Int32>", audit.Name);
        Assert.Equal("ModelShapesApp.Audit<System.Int32>", audit.ClrFullName);
    }

    [Fact]
    public void Build_EntitiesAreSortedAlphabeticallyAndReferencesUseTheSameNames()
    {
        var schema = BuildSchema();

        var names = schema.Entities.Select(e => e.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ThenBy(n => n, StringComparer.Ordinal), names);

        var known = names.ToHashSet(StringComparer.Ordinal);
        Assert.All(schema.Entities.SelectMany(e => e.ForeignKeys), fk => Assert.Contains(fk.PrincipalEntity, known));
        Assert.All(schema.Entities.SelectMany(e => e.Navigations), nav => Assert.Contains(nav.TargetEntity, known));
    }
}
