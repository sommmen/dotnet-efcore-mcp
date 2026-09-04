using DotnetEfCoreMcp.Server.Schema;

namespace DotnetEfCoreMcp.Server.Tests.Schema;

/// <summary>Exercises <see cref="SchemaSlicer"/> against hand-built <see cref="SchemaDto"/> instances
/// rather than a real <see cref="Microsoft.EntityFrameworkCore.DbContext"/>. Deliberately never touches
/// <see cref="SchemaBuilder"/>, a <c>DbContext</c>, or a database connection: this proves the slicer
/// itself is pure, cache-only metadata access, independent of how the schema was originally
/// built/cached.</summary>
public sealed class SchemaSlicerTests
{
    private static SchemaDto CreateSampleSchema()
    {
        var customer = new EntityTypeSchema(
            Name: "Customer",
            ClrFullName: "SampleApp.Customer",
            TableName: "Customers",
            Properties:
            [
                new PropertySchema("Id", "Int32", "Id", "INTEGER", false, true, false, false),
                new PropertySchema("Name", "String", "Name", "TEXT", false, false, false, false),
                new PropertySchema("Age", "Int32", "Age", "INTEGER", false, false, false, false),
            ],
            PrimaryKeyProperties: ["Id"],
            ForeignKeys: [],
            Navigations: [new NavigationSchema("Orders", "Order", true)],
            IsOwned: false,
            BaseEntityName: null,
            DiscriminatorProperty: null);

        var order = new EntityTypeSchema(
            Name: "Order",
            ClrFullName: "SampleApp.Order",
            TableName: "Orders",
            Properties:
            [
                new PropertySchema("Id", "Int32", "Id", "INTEGER", false, true, false, false),
                new PropertySchema("CustomerId", "Int32", "CustomerId", "INTEGER", false, false, true, false),
                new PropertySchema("Amount", "Decimal", "Amount", "TEXT", false, false, false, false),
            ],
            PrimaryKeyProperties: ["Id"],
            ForeignKeys:
            [
                new ForeignKeySchema(["CustomerId"], "Customer", ["Id"], "Customer", "Orders", true),
            ],
            Navigations: [new NavigationSchema("Customer", "Customer", false)],
            IsOwned: false,
            BaseEntityName: null,
            DiscriminatorProperty: null);

        return new SchemaDto("SampleAppDbContext", [customer, order]);
    }

    [Fact]
    public void FindEntity_WithExactName_ReturnsCompleteSlice()
    {
        var schema = CreateSampleSchema();

        var entity = SchemaSlicer.FindEntity(schema, "Order", NoOpSchemaAccessPolicy.Instance);

        Assert.NotNull(entity);
        Assert.Equal("Order", entity!.Name);
        Assert.Equal("SampleApp.Order", entity.ClrFullName);
        Assert.Equal(3, entity.Properties.Count);
        Assert.Single(entity.ForeignKeys);
        Assert.Single(entity.Navigations);
    }

    [Fact]
    public void FindEntity_IsCaseSensitive()
    {
        var schema = CreateSampleSchema();

        var entity = SchemaSlicer.FindEntity(schema, "order", NoOpSchemaAccessPolicy.Instance);

        Assert.Null(entity);
    }

    [Fact]
    public void FindEntity_WithUnknownName_ReturnsNull()
    {
        var schema = CreateSampleSchema();

        var entity = SchemaSlicer.FindEntity(schema, "DoesNotExist", NoOpSchemaAccessPolicy.Instance);

        Assert.Null(entity);
    }

    [Fact]
    public void Search_MatchesEntityNameCaseInsensitively()
    {
        var schema = CreateSampleSchema();

        // "custome" (without the trailing "r") only matches the "Customer" entity name itself -
        // "CustomerId" also contains it, but as a property on "Order", not as an entity name match.
        var result = SchemaSlicer.Search(schema, "custome", 10, NoOpSchemaAccessPolicy.Instance);

        var match = Assert.Single(result.Matches, m => m.EntityNameMatched);
        Assert.Equal("Customer", match.EntityName);
    }

    [Fact]
    public void Search_MatchesPropertyAndRelationshipNames()
    {
        var schema = CreateSampleSchema();

        var result = SchemaSlicer.Search(schema, "customerid", 10, NoOpSchemaAccessPolicy.Instance);

        var match = Assert.Single(result.Matches);
        Assert.Equal("Order", match.EntityName);
        Assert.False(match.EntityNameMatched);
        Assert.Contains("CustomerId", match.MatchingProperties);
    }

    [Fact]
    public void Search_MatchesNavigationName()
    {
        var schema = CreateSampleSchema();

        var result = SchemaSlicer.Search(schema, "orders", 10, NoOpSchemaAccessPolicy.Instance);

        Assert.Contains(result.Matches, m => m.EntityName == "Customer" && m.MatchingRelationships.Contains("Orders"));
    }

    [Fact]
    public void Search_ResultsAreOrderedDeterministicallyByEntityName()
    {
        var schema = CreateSampleSchema();

        // "o" matches both entity names ("Customer" and "Order").
        var result = SchemaSlicer.Search(schema, "o", 10, NoOpSchemaAccessPolicy.Instance);

        Assert.Equal(["Customer", "Order"], result.Matches.Select(m => m.EntityName).ToArray());
    }

    [Fact]
    public void Search_DoesNotReturnFullEntityDefinitions()
    {
        var schema = CreateSampleSchema();

        var result = SchemaSlicer.Search(schema, "Order", 10, NoOpSchemaAccessPolicy.Instance);

        // SchemaSearchMatch has no Properties/ForeignKeys/Navigations collections of the full
        // EntityTypeSchema shape - only name lists - so there is nothing to assert away besides
        // confirming the match type itself is the compact shape.
        Assert.All(result.Matches, m => Assert.IsType<SchemaSearchMatch>(m));
    }

    [Fact]
    public void Search_WithNoMatches_ReturnsEmptyResultAndZeroTotal()
    {
        var schema = CreateSampleSchema();

        var result = SchemaSlicer.Search(schema, "zzz-no-match-zzz", 10, NoOpSchemaAccessPolicy.Instance);

        Assert.Empty(result.Matches);
        Assert.Equal(0, result.TotalMatchCount);
    }

    [Fact]
    public void Search_CapsResultsAtMaxResultsAndReportsTotalMatchCount()
    {
        var entities = Enumerable.Range(0, 5)
            .Select(i => new EntityTypeSchema(
                Name: $"MatchingEntity{i}",
                ClrFullName: $"Sample.MatchingEntity{i}",
                TableName: null,
                Properties: [],
                PrimaryKeyProperties: [],
                ForeignKeys: [],
                Navigations: [],
                IsOwned: false,
                BaseEntityName: null,
                DiscriminatorProperty: null))
            .ToList();
        var schema = new SchemaDto("ManyMatchesContext", entities);

        var result = SchemaSlicer.Search(schema, "MatchingEntity", 3, NoOpSchemaAccessPolicy.Instance);

        Assert.Equal(3, result.Matches.Count);
        Assert.Equal(5, result.TotalMatchCount);
    }

    [Fact]
    public void Search_AndFindEntity_RouteThroughTheSuppliedPolicy()
    {
        var schema = CreateSampleSchema();
        var policy = new RecordingPolicy();

        SchemaSlicer.FindEntity(schema, "Customer", policy);
        SchemaSlicer.Search(schema, "Customer", 10, policy);

        Assert.Equal(2, policy.ApplyCallCount);
    }

    [Fact]
    public void Search_HonorsAPolicyThatFiltersOutEntities()
    {
        var schema = CreateSampleSchema();
        var policy = new DenyEntityPolicy("Order");

        var result = SchemaSlicer.Search(schema, "o", 10, policy);
        var entity = SchemaSlicer.FindEntity(schema, "Order", policy);

        Assert.DoesNotContain(result.Matches, m => m.EntityName == "Order");
        Assert.Null(entity);
    }

    private sealed class RecordingPolicy : ISchemaAccessPolicy
    {
        public int ApplyCallCount { get; private set; }

        public SchemaDto Apply(SchemaDto schema)
        {
            ApplyCallCount++;
            return schema;
        }
    }

    /// <summary>A minimal stand-in for a future access-policy evaluator (P0 #9): proves the seam can
    /// filter entities out before matching/lookup without either public tool contract changing.</summary>
    private sealed class DenyEntityPolicy(string deniedEntityName) : ISchemaAccessPolicy
    {
        public SchemaDto Apply(SchemaDto schema)
            => schema with { Entities = schema.Entities.Where(e => e.Name != deniedEntityName).ToList() };
    }
}
