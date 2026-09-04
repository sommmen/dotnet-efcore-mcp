using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DotnetEfCoreMcp.Server.Schema;

/// <summary>Builds an agent-friendly <see cref="SchemaDto"/> from an EF Core <see cref="IModel"/>,
/// including primary keys, foreign keys, navigation properties, owned types, and TPH inheritance
/// (via each entity's base type / discriminator property, if any). Building the schema does not
/// require an open database connection - it is purely metadata already known to the compiled
/// model.</summary>
public static class SchemaBuilder
{
    public static SchemaDto Build(DbContext context)
    {
        // context.Model is the "read-optimized" runtime model, which sheds design-time-only
        // metadata (comments, default value/SQL annotations, etc.) for faster startup. The
        // design-time model retains that metadata and is required to read it without throwing
        // InvalidOperationException - it is still purely compiled model metadata, not a database
        // round-trip.
        var model = context.GetService<IDesignTimeModel>().Model;
        var entities = new List<EntityTypeSchema>();

        // The relational annotation extensions (e.g. GetColumnType) throw InvalidCastException
        // when the provider isn't relational (e.g. Cosmos, InMemory), because they attempt to
        // cast the provider's type mapping to RelationalTypeMapping. Checking IsRelational() up
        // front lets us skip those calls entirely for non-relational providers, instead of using
        // a catch-all to paper over the expected failure (which would also hide real bugs).
        var isRelational = context.Database.IsRelational();

        foreach (var entityType in model.GetEntityTypes())
        {
            var primaryKey = entityType.FindPrimaryKey();

            var properties = entityType.GetProperties()
                .Select(p => new PropertySchema(
                    Name: p.Name,
                    ClrTypeName: p.ClrType.Name,
                    ColumnName: TryGetColumnName(p),
                    StoreType: isRelational ? p.GetColumnType() : null,
                    IsNullable: p.IsNullable,
                    IsPrimaryKey: p.IsPrimaryKey(),
                    IsForeignKey: p.IsForeignKey(),
                    IsConcurrencyToken: p.IsConcurrencyToken,
                    MaxLength: p.GetMaxLength(),
                    Precision: p.GetPrecision(),
                    Scale: p.GetScale(),
                    IsUnicode: p.IsUnicode(),
                    IsFixedLength: isRelational ? p.IsFixedLength() : null,
                    ValueGenerated: p.ValueGenerated.ToString(),
                    DefaultValueSql: isRelational ? p.GetDefaultValueSql() : null,
                    ComputedColumnSql: isRelational ? p.GetComputedColumnSql() : null,
                    DefaultValue: isRelational ? p.GetDefaultValue()?.ToString() : null,
                    Comment: isRelational ? p.GetComment() : null))
                .ToList();

            var primaryKeyProperties = primaryKey?.Properties
                .Select(p => p.Name)
                .ToList() ?? [];

            var foreignKeys = entityType.GetForeignKeys()
                .Select(fk => new ForeignKeySchema(
                    Properties: fk.Properties.Select(p => p.Name).ToList(),
                    PrincipalEntity: fk.PrincipalEntityType.ClrType.Name,
                    PrincipalProperties: fk.PrincipalKey.Properties.Select(p => p.Name).ToList(),
                    DependentToPrincipalNavigation: fk.DependentToPrincipal?.Name,
                    PrincipalToDependentNavigation: fk.PrincipalToDependent?.Name,
                    IsRequired: fk.IsRequired,
                    ConstraintName: isRelational ? fk.GetConstraintName() : null,
                    DeleteBehavior: fk.DeleteBehavior.ToString(),
                    IsUnique: fk.IsUnique))
                .ToList();

            var navigations = entityType.GetNavigations()
                .Select(n => new NavigationSchema(
                    Name: n.Name,
                    TargetEntity: n.TargetEntityType.ClrType.Name,
                    IsCollection: n.IsCollection,
                    IsOnDependent: n.IsOnDependent,
                    IsEagerLoaded: n.IsEagerLoaded,
                    DeleteBehavior: n.ForeignKey.DeleteBehavior.ToString(),
                    ForeignKeyProperties: n.ForeignKey.Properties.Select(p => p.Name).ToList()))
                .ToList();

            var indexes = entityType.GetIndexes()
                .Select(i => new IndexSchema(
                    Properties: i.Properties.Select(p => p.Name).ToList(),
                    Name: isRelational ? i.GetDatabaseName() : null,
                    IsUnique: i.IsUnique,
                    Filter: isRelational ? i.GetFilter() : null))
                .ToList();

            entities.Add(new EntityTypeSchema(
                Name: entityType.ClrType.Name,
                ClrFullName: entityType.ClrType.FullName,
                TableName: TryGetTableName(entityType),
                Properties: properties,
                PrimaryKeyProperties: primaryKeyProperties,
                ForeignKeys: foreignKeys,
                Navigations: navigations,
                IsOwned: entityType.IsOwned(),
                BaseEntityName: entityType.BaseType?.ClrType.Name,
                DiscriminatorProperty: entityType.FindDiscriminatorProperty()?.Name,
                Schema: isRelational ? entityType.GetSchema() : null,
                ViewName: isRelational ? entityType.GetViewName() : null,
                ViewSchema: isRelational ? entityType.GetViewSchema() : null,
                Comment: isRelational ? entityType.GetComment() : null,
                PrimaryKeyName: isRelational ? primaryKey?.GetName() : null,
                Indexes: indexes));
        }

        return new SchemaDto(context.GetType().Name, entities);
    }

    // Table/column/store-type metadata comes from the relational annotation extensions
    // (Microsoft.EntityFrameworkCore.Relational). These return null for non-relational providers
    // (e.g. Cosmos), which is fine - the fields are simply omitted from the response.
    private static string? TryGetTableName(IEntityType entityType) => entityType.GetTableName();

    private static string? TryGetColumnName(IProperty property) => property.GetColumnName();
}
