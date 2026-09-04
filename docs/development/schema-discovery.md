# Schema / model discovery

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: [`src/DotnetEfCoreMcp.Server/Schema`](../../src/DotnetEfCoreMcp.Server/Schema) ·
Tests: [`tests/DotnetEfCoreMcp.Server.Tests/Schema`](../../tests/DotnetEfCoreMcp.Server.Tests/Schema)

- [x] Build the EF Core model for a resolved `DbContext` instance (`context.Model`)
- [x] Expose entity types, their CLR properties, and mapped column names/types
- [x] Expose primary keys, foreign keys, and navigation properties (relationships)
- [x] Expose owned types and inheritance hierarchies (TPH/TPT/TPC) where present
  - `EntityTypeSchema.IsOwned`, `BaseEntityName`, `DiscriminatorProperty`. Not exercised
    by an integration test in this session (the SampleApp fixture has no owned
    types/inheritance), but the reflection code is straightforward metadata access
    (`entityType.IsOwned()`, `.BaseType`, `.FindDiscriminatorProperty()`) with no
    provider-specific behavior — flagged here as a coverage gap rather than a design gap.
- [x] Serialize the discovered schema into a compact, agent-friendly format (e.g. JSON) suitable for an MCP tool response
- [x] Cache the discovered schema per loaded assembly/context and invalidate it when the assembly is reloaded
  - `SchemaCache` keys by `DbContext` CLR type; `load_assembly` reloading into a new
    `AssemblyLoadContext` produces new `Type` instances (old ones are collectible/unloaded),
    so old cache entries can never be returned for a stale assembly — invalidation is a
    natural consequence of the identity change rather than an explicit cache-clear call.
- [x] Slice and search the cached schema without constructing a `DbContext` or rediscovering
  the model (`get_entity_schema`, `search_schema`) — see P0 #6 below.
- [x] Enrich entity/property/relationship DTOs with relational mapping, constraint/index,
  store-generation, and facet metadata — see P1 #10 below.

## Tool contract

`get_schema` accepts either a DbContext short name (for example, `CommerceDbContext`) or its
fully qualified CLR type name. `contextName` may be omitted only when the loaded assembly exposes
exactly one DbContext; `load_assembly` reports that default context and an explicit hint in that
case. Ambiguous, missing, and invalid selections list the available short names and direct callers
to `list_contexts`.

Schema responses are bounded at the tool boundary, while the complete model remains cached.
`get_schema` returns entity pages using one-based `page` (default `1`) and `pageSize` (default
`25`, maximum `100`), along with `totalEntityCount`, `truncated`, `hasMore`, and `nextPage`. When
more entities remain, the response includes a continuation hint showing the next request.

## P0 #6 — schema slicing/search

Added two read-only tools over the existing `SchemaCache`; neither constructs a `DbContext`,
queries a database, or rediscovers a model — if nothing is cached yet for the resolved context,
both throw a validation error directing the caller to call `get_schema` first.
`get_entity_schema(entityName, contextName?)` returns the complete cached entity definition
(properties, keys, foreign keys, navigations, ownership, and inheritance metadata) for the exact,
case-sensitive entity name, or the established sanitized validation error (listing known entity
names) when the context or entity is unknown.

`search_schema(contextName?, query, maxResults?)` searches only cached schema metadata and returns
compact matches (`entityName`, `entityNameMatched`, `matchingProperties`,
`matchingRelationships`) for entity names plus matching properties and relationships — never full
entity definitions. `query` must be non-empty; matching is a deterministic, case-insensitive
substring comparison ordered by entity name. `maxResults` defaults to 10, rejects invalid values,
and is capped at 25. The response always reports `totalMatchCount` (before the cap) and
`truncated` (whether the cap omitted further matches).

Both operations are implemented in `Schema/SchemaSlicer.cs` (`FindEntity`, `Search`) and route the
cached schema through `ISchemaAccessPolicy` (`Schema/SchemaAccessPolicy.cs`) before entity lookup
or matching. P0 #6 supplies no access policy — the default `NoOpSchemaAccessPolicy` is a no-op —
but this seam lets a later evaluator (P0 #9) filter entities, properties, and relationships without
altering either tool's public request or response shape. `Schema/SchemaSlicerTests.cs` and
`Tools/EfCoreMcpToolsSchemaSlicingTests.cs` cover cache-only execution (including no context
construction/database access), exact slice fidelity and unknown-name validation, search
matching/order, default and maximum caps with `truncated`, invalid arguments, and forwarding
through the policy seam.

## Proposed open work — P0 #9: policy-filtered schema discovery

Apply the per-connection `AccessPolicy` before any schema response is formed. `list_contexts` returns only contexts permitted for that connection. `get_schema`, `get_entity_schema`, and `search_schema` first authorize the requested context, then select only permitted entities; relationships, navigations, foreign keys, and property metadata that point to excluded entities are omitted rather than represented by dangling references. Unknown and denied context/entity requests use the same non-enumerating denial path, so cached schema data cannot reveal whether a denied name exists.

Filtering is a view over the existing cache: it neither constructs a `DbContext` nor mutates a shared cached `SchemaDocument`. Apply filtering before search matching, result caps, truncation calculation, and entity lookup, so excluded names cannot influence matches, counts, or ordering. Focused tests prove a mixed-policy connection exposes only allowed contexts/entities and internally consistent relationships; denied/unknown requests do not disclose model names; and a broader allow selector wins over a conflicting deny selector.

## P1 #10 — enrich schema metadata

Extended `EntityTypeSchema`, `PropertySchema`, `ForeignKeySchema`, and `NavigationSchema` (plus a
new `IndexSchema`) with optional, trailing, backward-compatible fields sourced only from the
compiled EF Core model. Every new parameter defaults to `null` so existing positional
constructor calls (for example, in `Schema/SchemaSlicerTests.cs`) keep compiling unmodified, and
both `JsonToolResultFormatter`/`ToonToolResultFormatter` already omit `null` fields from
serialized tool responses (`JsonIgnoreCondition.WhenWritingNull`), so no serializer changes were
needed for the new fields to stay compatible with existing clients.

`EntityTypeSchema` gained `Schema`, `ViewName`, `ViewSchema`, `Comment`, `PrimaryKeyName`
(relational-only), and `Indexes` (an `IndexSchema` list with `Properties`, `Name`, `IsUnique`,
`Filter`). `PropertySchema` gained `MaxLength`, `Precision`, `Scale`, `IsUnicode`, and
`ValueGenerated` (core EF metadata, populated regardless of provider), plus `IsFixedLength`,
`DefaultValueSql`, `ComputedColumnSql`, `DefaultValue`, and `Comment` (relational-only).
`ForeignKeySchema` gained `DeleteBehavior` and `IsUnique` (core) plus `ConstraintName`
(relational-only). `NavigationSchema` gained `IsOnDependent`, `IsEagerLoaded`,
`DeleteBehavior`, and `ForeignKeyProperties` (mirrored from the navigation's underlying foreign
key so callers don't need to cross-reference the entity's foreign key list separately) — all
core EF metadata, populated regardless of provider.

`Schema/SchemaBuilder.cs` reads relational-only facets by checking `context.Database.IsRelational()`
up front (the pre-existing pattern used for `StoreType`) and calling the relational metadata
extensions (`RelationalPropertyExtensions`, `RelationalEntityTypeExtensions`,
`RelationalKeyExtensions`, `RelationalForeignKeyExtensions`, `RelationalIndexExtensions`) only
when `true`; non-relational providers (for example, InMemory) get `null` for those facets instead
of an `InvalidCastException`. Core facets (`GetMaxLength`, `GetPrecision`, `GetScale`,
`IsUnicode`, `ValueGenerated`, `DeleteBehavior`, `IsUnique`, `IsOnDependent`, `IsEagerLoaded`) are
defined directly on `IReadOnlyProperty`/`IReadOnlyForeignKey`/`IReadOnlyNavigationBase` and are
populated unconditionally. The model is sourced via
`context.GetService<IDesignTimeModel>().Model` rather than `context.Model`: the latter is a
"read-optimized" runtime model that sheds design-time-only metadata (comments, some default-value
annotations) for performance, and calling relational extensions like `GetComment()` against it
throws `InvalidOperationException`. `IDesignTimeModel` is still purely compiled model metadata —
no database round-trip is performed.

`Schema/SchemaBuilderTests.cs` exercises the enrichment against the SampleApp fixture (extended
in `SampleAppDbContext.OnModelCreating` with a table comment, a unique index, `HasMaxLength`,
`IsUnicode(false)`, property comments, `HasPrecision`, `HasDefaultValueSql`, and
`OnDelete(DeleteBehavior.Cascade)`) for relational metadata fidelity, and against a dedicated
non-relational `InMemoryProbeContext`/`InMemoryProbeChild` pair for null handling: relational-only
facets stay `null` under a non-relational provider while core facets remain populated.
