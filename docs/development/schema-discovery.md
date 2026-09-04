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
or matching. `NoOpSchemaAccessPolicy` remains the default when no connection is active. Once a
connection is resolved, `ConnectionSchemaAccessPolicy` (P0 #9, see below) filters entities,
properties, and relationships without altering either tool's public request or response shape.
`Schema/SchemaSlicerTests.cs` and `Tools/EfCoreMcpToolsSchemaSlicingTests.cs` cover cache-only
execution (including no context construction/database access), exact slice fidelity and
unknown-name validation, search matching/order, default and maximum caps with `truncated`, invalid
arguments, and forwarding through the policy seam.

## P0 #9: policy-filtered schema discovery

The per-connection `AccessPolicy` (see [Connection management](./connections.md)) is applied
before any schema response is formed. `list_contexts` returns only contexts reachable for the
active connection. `get_schema`, `get_entity_schema`, and `search_schema` first authorize the
requested context (`EnsureContextReachable`), then route the cached `SchemaDto` through a
freshly-constructed `ConnectionSchemaAccessPolicy` (implementing `ISchemaAccessPolicy`) that
selects only permitted entities; relationships, navigations, foreign keys, and property metadata
that point to excluded entities are omitted rather than represented by dangling references. The
shared `SchemaCache` entry is never mutated — the policy projects a fresh, filtered `SchemaDto`
per request. Unknown and denied context/entity requests use the same non-enumerating denial path
(`AccessPolicyDeniedException`/a "not found" result whose "known entities" hint is drawn only from
the policy-filtered view), so cached schema data cannot reveal whether a denied name exists.

Filtering is a view over the existing cache: it neither constructs a `DbContext` nor mutates a shared cached `SchemaDocument`. `SchemaSlicer.FindEntity`/`Search` apply the policy before entity lookup, search matching, result caps, and truncation calculation, so excluded names cannot influence matches, counts, or ordering. Focused tests (`Schema/ConnectionSchemaAccessPolicyTests.cs`, `Tools/EfCoreMcpToolsAccessPolicyTests.cs`) prove a mixed-policy connection exposes only allowed contexts/entities and internally consistent relationships; denied/unknown requests do not disclose model names; and a broader allow selector wins over a conflicting deny selector.

## Proposed open work — P1 #10: enrich schema metadata

Extend the existing schema DTOs additively so current MCP clients continue to receive every
field and response shape they use today. New fields must be optional/nullable (and collection
fields defaulted compatibly where applicable); do not rename, remove, reinterpret, or make an
existing field required. Surface only information available from the compiled EF Core metadata
model—never provider queries, database inspection, raw SQL, migrations, or inferred values.

Enrich entity/property/relationship DTOs with useful relational mapping details such as schema,
table or view mapping, key/index/constraint names, value-generation and store-generation behavior,
default/computed SQL, precision/scale, max length, Unicode/fixed-length flags, comments, and
relationship delete behavior. Obtain values through EF metadata APIs and relational metadata
extensions. Provider-specific annotations (including SQL expressions, constraint names, and
mapping details that a provider does not expose) must remain nullable rather than being guessed,
normalized into fabricated values, or treated as errors.

Add focused schema-discovery tests using the existing fixture model plus provider-specific test
metadata where needed. Verify legacy DTO serialization remains valid when enrichment is present
or absent, representative EF metadata is copied faithfully, unsupported provider-specific values
serialize as null, and discovery continues to use only `DbContext.Model` without opening a
connection or querying a database.
