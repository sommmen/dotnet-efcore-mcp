namespace DotnetEfCoreMcp.Server.Schema;

/// <summary>Agent-facing schema for a single resolved <c>DbContext</c>.</summary>
public sealed record SchemaDto(string ContextName, IReadOnlyList<EntityTypeSchema> Entities);

public sealed record EntityTypeSchema(
    string Name,
    string? ClrFullName,
    string? TableName,
    IReadOnlyList<PropertySchema> Properties,
    IReadOnlyList<string> PrimaryKeyProperties,
    IReadOnlyList<ForeignKeySchema> ForeignKeys,
    IReadOnlyList<NavigationSchema> Navigations,
    bool IsOwned,
    string? BaseEntityName,
    string? DiscriminatorProperty);

public sealed record PropertySchema(
    string Name,
    string ClrTypeName,
    string? ColumnName,
    string? StoreType,
    bool IsNullable,
    bool IsPrimaryKey,
    bool IsForeignKey,
    bool IsConcurrencyToken);

public sealed record ForeignKeySchema(
    IReadOnlyList<string> Properties,
    string PrincipalEntity,
    IReadOnlyList<string> PrincipalProperties,
    string? DependentToPrincipalNavigation,
    string? PrincipalToDependentNavigation,
    bool IsRequired);

public sealed record NavigationSchema(string Name, string TargetEntity, bool IsCollection);
