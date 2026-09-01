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
