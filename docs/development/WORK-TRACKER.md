# Work tracker

[← Back to Development Guide](../../DEVELOPMENT.md)

This is the single place to check what remains open across `dotnet-efcore-mcp`. Full
per-module checklists (including everything already completed, with implementation notes)
live in the [feature/module slices](../../DEVELOPMENT.md#feature-and-module-guides); this
document only tracks items that are still outstanding, plus how to add new ones.

## Open items

| Item | Area | Notes |
|---|---|---|
| Metrics/telemetry hooks | [Auditing & observability](./observability.md) | Optional, later-stage. No metrics pipeline exists yet; would need an OpenTelemetry (or similar) dependency decision before implementation. |

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
