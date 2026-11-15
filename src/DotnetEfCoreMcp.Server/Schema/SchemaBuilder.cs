using System.Globalization;
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

        foreach (var entityType in model.GetEntityTypes().OrderBy(EntityName, StringComparer.OrdinalIgnoreCase).ThenBy(EntityName, StringComparer.Ordinal))
        {
            var primaryKey = entityType.FindPrimaryKey();

            var properties = entityType.GetProperties()
                .Select(p => new PropertySchema(
                    Name: p.Name,
                    ClrTypeName: FormatClrTypeName(p.ClrType),
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
                    DefaultValue: isRelational ? FormatDefaultValue(p.GetDefaultValue()) : null,
                    Comment: isRelational ? p.GetComment() : null))
                .ToList();

            var primaryKeyProperties = primaryKey?.Properties
                .Select(p => p.Name)
                .ToList() ?? [];

            var foreignKeys = entityType.GetForeignKeys()
                .Select(fk => new ForeignKeySchema(
                    Properties: fk.Properties.Select(p => p.Name).ToList(),
                    PrincipalEntity: EntityName(fk.PrincipalEntityType),
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
                    TargetEntity: EntityName(n.TargetEntityType),
                    IsCollection: n.IsCollection,
                    IsOnDependent: n.IsOnDependent,
                    IsEagerLoaded: n.IsEagerLoaded,
                    DeleteBehavior: n.ForeignKey.DeleteBehavior.ToString(),
                    ForeignKeyProperties: n.ForeignKey.Properties.Select(p => p.Name).ToList()))
                .ToList();

            var indexList = entityType.GetIndexes()
                .Select(i => new IndexSchema(
                    Properties: i.Properties.Select(p => p.Name).ToList(),
                    Name: isRelational ? i.GetDatabaseName() : null,
                    IsUnique: i.IsUnique,
                    Filter: isRelational ? i.GetFilter() : null))
                .ToList();
            var indexes = indexList.Count > 0 ? indexList : null;

            entities.Add(new EntityTypeSchema(
                Name: EntityName(entityType),
                ClrFullName: FormatClrFullName(entityType.ClrType),
                TableName: TryGetTableName(entityType),
                Properties: properties,
                PrimaryKeyProperties: primaryKeyProperties,
                ForeignKeys: foreignKeys,
                Navigations: navigations,
                IsOwned: entityType.IsOwned(),
                BaseEntityName: entityType.BaseType is { } baseType ? EntityName(baseType) : null,
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

    /// <summary>The name callers use for an entity: the EF entity name for a shared-type entity (many
    /// share one CLR type such as <c>Dictionary&lt;string, object&gt;</c>), a readable generic name for
    /// a generic CLR type, and the CLR type name otherwise.</summary>
    public static string EntityName(Microsoft.EntityFrameworkCore.Metadata.IReadOnlyTypeBase entityType) =>
        entityType.HasSharedClrType ? entityType.Name
        : entityType.ClrType.IsGenericType ? FormatClrTypeName(entityType.ClrType)
        : entityType.ClrType.Name;

    // Type.FullName of a constructed generic embeds every argument assembly-qualified, which runs to hundreds of characters.
    private static string? FormatClrFullName(Type type)
    {
        if (!type.IsGenericType)
            return type.FullName;

        var definitionName = type.GetGenericTypeDefinition().FullName;
        if (definitionName is null)
            return type.FullName;

        var genericArguments = string.Join(", ", type.GetGenericArguments().Select(a => FormatClrFullName(a) ?? a.Name));
        // Arity markers are stripped everywhere so a nested type keeps its "+Inner" suffix.
        return $"{System.Text.RegularExpressions.Regex.Replace(definitionName, @"`\d+", string.Empty)}<{genericArguments}>";
    }

    // Table/column/store-type metadata comes from the relational annotation extensions
    // (Microsoft.EntityFrameworkCore.Relational). These return null for non-relational providers
    // (e.g. Cosmos), which is fine - the fields are simply omitted from the response.
    private static string FormatClrTypeName(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } nullableUnderlyingType)
            return $"{FormatClrTypeName(nullableUnderlyingType)}?";

        if (!type.IsGenericType)
            return type.Name;

        var genericTypeName = type.Name[..type.Name.IndexOf('`')];
        var genericArguments = string.Join(", ", type.GetGenericArguments().Select(FormatClrTypeName));
        return $"{genericTypeName}<{genericArguments}>";
    }

    private static string? TryGetTableName(IEntityType entityType) => entityType.GetTableName();

    private static string? TryGetColumnName(IProperty property) => property.GetColumnName();

    // Default values can be numbers, dates, or other IFormattable types whose ToString() output
    // is culture-dependent (e.g. decimal separators, date formats). Formatting with
    // InvariantCulture keeps the serialized value stable across machines/locales. byte[] (e.g.
    // rowversion/timestamp defaults) is handled explicitly since it is neither IFormattable nor
    // meaningfully convertible via Convert.ToString, which can throw for arbitrary CLR types.
    private static string? FormatDefaultValue(object? value) => value switch
    {
        null => null,
        string s => s,
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
