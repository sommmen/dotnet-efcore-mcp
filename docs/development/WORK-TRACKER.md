# Work tracker

[← Back to Development Guide](../../DEVELOPMENT.md)

This is the single place to check what remains open across `dotnet-efcore-mcp`. Full
per-module checklists (including everything already completed, with implementation notes)
live in the [feature/module slices](../../DEVELOPMENT.md#feature-and-module-guides); this
document only tracks items that are still outstanding, plus how to add new ones.

## Open items

There are currently no open items — [project scaffolding](./project-scaffolding.md), [assembly
loading](./assembly-loading.md), [`DbContext` discovery](./dbcontext-discovery.md),
[connection management](./connections.md), [schema discovery](./schema-discovery.md),
[query execution](./query-execution.md), the [MCP tool surface](./mcp-tools.md), the
[named multi-target assembly registry](./assembly-registry.md), and
[startup-derived connections](./startup-derived-connections.md) are all complete and
covered by tests (`dotnet test`).

## Adding new work

When starting new feature work or discovering a gap:

1. Add a table (with an `Item`/`Area`/`Notes` row per open item, as prior entries in this
   file did) under "Open items" describing the item and linking to the relevant slice doc.
2. If the work is substantial enough to need its own reference documentation, add or
   extend a slice under [`docs/development/`](.) and link it from
   [`DEVELOPMENT.md`](../../DEVELOPMENT.md).
3. Once the work lands, move the checklist detail into the relevant slice document (with
   implementation notes, as the existing slices do) and remove the row from this table.
