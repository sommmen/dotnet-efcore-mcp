# Work tracker

[← Back to Development Guide](../../DEVELOPMENT.md)

This is the single place to check what remains open across `dotnet-efcore-mcp`. Full
per-module checklists (including everything already completed, with implementation notes)
live in the [feature/module slices](../../DEVELOPMENT.md#feature-and-module-guides); this
document only tracks items that are still outstanding, plus how to add new ones.

## Open items

| Item | Area | Notes |
|---|---|---|
| P1 #13 — bounded nested include paths | [Query execution](./query-execution.md) · [MCP tools](./mcp-tools.md) | Extend `run_query.include` from one navigation to validated dot-separated EF model paths. Enforce `MaxIncludeDepth` and `MaxIncludeCount`; recursively project only bounded scalar/navigation data and cap every included collection level. Reject cycles, repeated navigations, and unknown/non-navigation path segments before execution. Cover model-path validation, limits, recursive output, per-level collection caps, and rejections with focused tests. |
| P1 #14 — keyset/cursor pagination | [Query execution](./query-execution.md) · [MCP tools](./mcp-tools.md) | Add an opt-in `pagination: { mode: "cursor", cursor?: string }`/`nextCursor` keyset-paging mode to `run_query`, mutually exclusive with `skip`. Require a unique deterministic order by auto-appending the primary key as a tie-breaker; encode cursors as opaque tokens bound to entity/context/ordering shape and reject malformed, mismatched, or tampered cursors without disclosing decoded values. Scope v1 to forward-only paging. Keep coexisting with `take`/`hasMoreRows` (P0 #2) and legacy offset `skip`/`take` paging, which stay fully supported when `pagination` is omitted. Cover parity, issuance/resumption, tie-breaking, rejection cases, and `hasMoreRows`/`nextCursor` agreement with focused tests. |
| P0 #8 — database-side `run_query` collection-include cap | [Query execution](./query-execution.md) · [MCP tools](./mcp-tools.md) | Make `MaxIncludedCollectionItems` a per-parent, database-side cap: translate each requested collection include to ordered filtered-include SQL (or an equivalently bounded split-query plan) with a per-collection `Take`, before materialization. Do not truncate already-loaded navigation collections. Keep reference includes, root paging, and the one-level include contract unchanged; cover per-parent behavior, zero/exact/below-cap boundaries, deterministic ordering, root paging, and SQL/command evidence that child rows are limited before materialization. |
There are currently no other open items from the original MVP roadmap — [project
scaffolding](./project-scaffolding.md), [assembly loading](./assembly-loading.md),
[`DbContext` discovery](./dbcontext-discovery.md), [connection
management](./connections.md), [schema discovery](./schema-discovery.md), [query
execution](./query-execution.md), and the [MCP tool surface](./mcp-tools.md) are all
complete and covered by tests (`dotnet test`).

## Adding new work

When starting new feature work or discovering a gap:

1. Add a row to the table above (or a new table/section if the work doesn't fit an
   existing area) describing the item and linking to the relevant slice doc.
2. If the work is substantial enough to need its own reference documentation, add or
   extend a slice under [`docs/development/`](.) and link it from
   [`DEVELOPMENT.md`](../../DEVELOPMENT.md).
3. Once the work lands, move the checklist detail into the relevant slice document (with
   implementation notes, as the existing slices do) and remove the row from this table.
