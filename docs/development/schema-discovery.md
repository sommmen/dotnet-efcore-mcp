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

## Proposed open work — P0 #6: schema slicing/search

Add two read-only tools over the existing `SchemaCache`; neither may construct a `DbContext`,
query a database, or rediscover a model. `get_entity_schema(contextName, entityName)` returns the
complete cached entity definition (properties, keys, foreign keys, navigations, ownership, and
inheritance metadata) for the exact entity name, or the established sanitized validation error when
the context or entity is unknown.

`search_schema(contextName, query, maxResults?)` searches only cached schema metadata and returns
compact matches for entity names plus matching properties and relationships. Require a non-empty
query, use deterministic case-insensitive matching and a documented stable ordering, default
`maxResults` to 10, reject invalid values, and enforce an absolute cap of 25. Include `truncated`
so callers know a capped result set omitted further matches; do not return full entity definitions
from search.

Route both operations through a policy-ready cached-schema selection seam before entity lookup or
matching. P0 #6 supplies no access policy, but a later evaluator must be able to filter entities,
properties, and relationships at that seam without altering either tool's public request or response
shape. Focused tests should prove cache-only execution (including no context construction/database
access), exact slice fidelity and unknown-name validation, search matching/order, default and
maximum caps with `truncated`, invalid arguments, and forwarding through the policy seam.

## Proposed open work — P0 #9: policy-filtered schema discovery

Apply the per-connection `AccessPolicy` before any schema response is formed. `list_contexts` returns only contexts permitted for that connection. `get_schema`, `get_entity_schema`, and `search_schema` first authorize the requested context, then select only permitted entities; relationships, navigations, foreign keys, and property metadata that point to excluded entities are omitted rather than represented by dangling references. Unknown and denied context/entity requests use the same non-enumerating denial path, so cached schema data cannot reveal whether a denied name exists.

Filtering is a view over the existing cache: it neither constructs a `DbContext` nor mutates a shared cached `SchemaDocument`. Apply filtering before search matching, result caps, truncation calculation, and entity lookup, so excluded names cannot influence matches, counts, or ordering. Focused tests prove a mixed-policy connection exposes only allowed contexts/entities and internally consistent relationships; denied/unknown requests do not disclose model names; and a broader allow selector wins over a conflicting deny selector.

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
