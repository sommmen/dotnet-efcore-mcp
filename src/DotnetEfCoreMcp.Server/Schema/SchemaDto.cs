namespace DotnetEfCoreMcp.Server.Schema;

/// <summary>Agent-facing schema for a single resolved <c>DbContext</c>.</summary>
public sealed record SchemaDto(string ContextName, IReadOnlyList<EntityTypeSchema> Entities);

/// <summary>An index on an entity type, sourced from relational metadata
/// (<see cref="Microsoft.EntityFrameworkCore.Metadata.IReadOnlyIndex"/>). <see cref="Name"/> and
/// <see cref="Filter"/> are relational-specific and stay <see langword="null"/> for providers that
/// don't support them (e.g. non-relational providers, or an index without an explicit filter).</summary>
public sealed record IndexSchema(
    IReadOnlyList<string> Properties,
    string? Name,
    bool IsUnique,
    string? Filter);

/// <summary>Backward-compatible: new fields (from <see cref="Schema"/> onward) are optional/nullable
/// and sourced only from EF metadata already on the compiled model - no database access is ever
/// performed to populate them. Provider-specific relational metadata (schema/view mappings, primary
/// key/index constraint names, comments) stays <see langword="null"/> for non-relational providers
/// (e.g. Cosmos, InMemory) rather than throwing.</summary>
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
    string? DiscriminatorProperty,
    string? Schema = null,
    string? ViewName = null,
    string? ViewSchema = null,
    string? Comment = null,
    string? PrimaryKeyName = null,
    IReadOnlyList<IndexSchema>? Indexes = null);

/// <summary>Backward-compatible: new fields (from <see cref="MaxLength"/> onward) are optional/nullable
/// and sourced only from EF metadata already on the compiled model. Relational-specific facets
/// (default/computed SQL, fixed-length, comment) stay <see langword="null"/> for non-relational
/// providers; core facets (<see cref="MaxLength"/>, <see cref="Precision"/>, <see cref="Scale"/>,
/// <see cref="IsUnicode"/>) are populated whenever configured, regardless of provider.
/// <see cref="ValueGenerated"/> is always populated - it reflects EF's
/// <see cref="Microsoft.EntityFrameworkCore.Metadata.IReadOnlyProperty.ValueGenerated"/>, which
/// defaults to <c>Never</c> rather than being unset when not explicitly configured.</summary>
public sealed record PropertySchema(
    string Name,
    string ClrTypeName,
    string? ColumnName,
    string? StoreType,
    bool IsNullable,
    bool IsPrimaryKey,
    bool IsForeignKey,
    bool IsConcurrencyToken,
    int? MaxLength = null,
    int? Precision = null,
    int? Scale = null,
    bool? IsUnicode = null,
    bool? IsFixedLength = null,
    string? ValueGenerated = null,
    string? DefaultValueSql = null,
    string? ComputedColumnSql = null,
    string? DefaultValue = null,
    string? Comment = null);

/// <summary>Backward-compatible: new fields (from <see cref="ConstraintName"/> onward) are
/// optional/nullable. <see cref="DeleteBehavior"/> captures the relationship's cascade/delete
/// behavior (e.g. <c>Cascade</c>, <c>Restrict</c>, <c>SetNull</c>, <c>NoAction</c>) and is core EF
/// metadata available regardless of provider; <see cref="ConstraintName"/> is relational-specific
/// and stays <see langword="null"/> for non-relational providers.</summary>
public sealed record ForeignKeySchema(
    IReadOnlyList<string> Properties,
    string PrincipalEntity,
    IReadOnlyList<string> PrincipalProperties,
    string? DependentToPrincipalNavigation,
    string? PrincipalToDependentNavigation,
    bool IsRequired,
    string? ConstraintName = null,
    string? DeleteBehavior = null,
    bool? IsUnique = null);

/// <summary>Backward-compatible: new fields (from <see cref="IsOnDependent"/> onward) are
/// optional/nullable. <see cref="DeleteBehavior"/> and <see cref="ForeignKeyProperties"/> mirror the
/// underlying <see cref="ForeignKeySchema"/> for this relationship, so callers inspecting a
/// navigation don't need to cross-reference the entity's foreign key list separately.</summary>
public sealed record NavigationSchema(
    string Name,
    string TargetEntity,
    bool IsCollection,
    bool? IsOnDependent = null,
    bool? IsEagerLoaded = null,
    string? DeleteBehavior = null,
    IReadOnlyList<string>? ForeignKeyProperties = null);
