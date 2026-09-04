using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.Schema;

namespace DotnetEfCoreMcp.Server.Tests.Schema;

/// <summary>Exercises <see cref="ConnectionSchemaAccessPolicy"/> (P0 #9) against hand-built
/// <see cref="SchemaDto"/> instances: entity-level filtering, non-mutation of the shared cached
/// instance, and stripping of foreign keys/navigations/base-entity references that would otherwise
/// disclose an excluded entity's existence.</summary>
public sealed class ConnectionSchemaAccessPolicyTests
{
    private const string ContextFullName = "SampleApp.SampleAppDbContext";

    private static SchemaDto CreateSampleSchema()
    {
        var customer = new EntityTypeSchema(
            Name: "Customer",
            ClrFullName: "SampleApp.Customer",
            TableName: "Customers",
            Properties:
            [
                new PropertySchema("Id", "Int32", "Id", "INTEGER", false, true, false, false),
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

        var priorityOrder = new EntityTypeSchema(
            Name: "PriorityOrder",
            ClrFullName: "SampleApp.PriorityOrder",
            TableName: "Orders",
            Properties: [],
            PrimaryKeyProperties: ["Id"],
            ForeignKeys: [],
            Navigations: [],
            IsOwned: false,
            BaseEntityName: "Order",
            DiscriminatorProperty: "Discriminator");

        return new SchemaDto("SampleAppDbContext", [customer, order, priorityOrder]);
    }

    private static ConnectionAccessPolicy CreatePolicy(
        IReadOnlyList<string>? allowContexts = null,
        IReadOnlyList<EntitySelector>? allowEntities = null) =>
        new()
        {
            AllowContexts = allowContexts ?? [],
            DenyContexts = [],
            AllowEntities = allowEntities ?? [],
            DenyEntities = [],
        };

    [Fact]
    public void Apply_WithBlanketContextAllow_ReturnsEveryEntityUnchanged()
    {
        var schema = CreateSampleSchema();
        var policy = new ConnectionSchemaAccessPolicy(CreatePolicy(allowContexts: [ContextFullName]), ContextFullName);

        var filtered = policy.Apply(schema);

        Assert.Equal(3, filtered.Entities.Count);
        Assert.Contains(filtered.Entities, e => e.Name == "Order" && e.ForeignKeys.Count == 1);
    }

    [Fact]
    public void Apply_WithOnlyOneEntityAllowed_ExcludesEveryOtherEntity()
    {
        var schema = CreateSampleSchema();
        var policy = new ConnectionSchemaAccessPolicy(
            CreatePolicy(allowEntities: [new EntitySelector(ContextFullName, "Customer")]),
            ContextFullName);

        var filtered = policy.Apply(schema);

        var entity = Assert.Single(filtered.Entities);
        Assert.Equal("Customer", entity.Name);
    }

    [Fact]
    public void Apply_ExcludedRelatedEntity_IsStrippedFromForeignKeysAndNavigations()
    {
        // Order is permitted but Customer (its FK principal / nav target) is not: Order's schema
        // must not disclose that a "Customer" entity exists via a dangling FK/navigation.
        var schema = CreateSampleSchema();
        var policy = new ConnectionSchemaAccessPolicy(
            CreatePolicy(allowEntities: [new EntitySelector(ContextFullName, "Order")]),
            ContextFullName);

        var filtered = policy.Apply(schema);

        var order = Assert.Single(filtered.Entities, e => e.Name == "Order");
        Assert.Empty(order.ForeignKeys);
        Assert.Empty(order.Navigations);
    }

    [Fact]
    public void Apply_ExcludedBaseEntity_IsStrippedFromBaseEntityName()
    {
        // PriorityOrder is permitted but its base entity "Order" is not: the filtered view must not
        // disclose the excluded base type via BaseEntityName.
        var schema = CreateSampleSchema();
        var policy = new ConnectionSchemaAccessPolicy(
            CreatePolicy(allowEntities: [new EntitySelector(ContextFullName, "PriorityOrder")]),
            ContextFullName);

        var filtered = policy.Apply(schema);

        var priorityOrder = Assert.Single(filtered.Entities);
        Assert.Equal("PriorityOrder", priorityOrder.Name);
        Assert.Null(priorityOrder.BaseEntityName);
    }

    [Fact]
    public void Apply_WithNoAllowedEntities_ReturnsEmptySchema()
    {
        var schema = CreateSampleSchema();
        var policy = new ConnectionSchemaAccessPolicy(CreatePolicy(), ContextFullName);

        var filtered = policy.Apply(schema);

        Assert.Empty(filtered.Entities);
    }

    [Fact]
    public void Apply_DoesNotMutateSharedCachedSchemaInstance()
    {
        var schema = CreateSampleSchema();
        var originalEntityCount = schema.Entities.Count;
        var originalOrderForeignKeyCount = schema.Entities.Single(e => e.Name == "Order").ForeignKeys.Count;
        var policy = new ConnectionSchemaAccessPolicy(
            CreatePolicy(allowEntities: [new EntitySelector(ContextFullName, "Order")]),
            ContextFullName);

        _ = policy.Apply(schema);

        Assert.Equal(originalEntityCount, schema.Entities.Count);
        Assert.Equal(originalOrderForeignKeyCount, schema.Entities.Single(e => e.Name == "Order").ForeignKeys.Count);
    }

    [Fact]
    public void Apply_WithNullContextFullName_DeniesEveryEntity()
    {
        // A null contextFullName (e.g. an unresolvable CLR type) must never fall back to permissive
        // behavior; every entity is denied, matching ConnectionAccessPolicy's fail-closed semantics.
        var schema = CreateSampleSchema();
        var policy = new ConnectionSchemaAccessPolicy(CreatePolicy(allowContexts: [ContextFullName]), contextFullName: null);

        var filtered = policy.Apply(schema);

        Assert.Empty(filtered.Entities);
    }
}
