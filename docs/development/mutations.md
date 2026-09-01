# Structured entity mutations

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: TBC (planned) · Tests: `tests/DotnetEfCoreMcp.Server.Tests/Mutations`

> **P1 #12 — Structured entity mutations.** This page documents the intended contract only;
> no implementation exists yet.

## Goal

Provide narrowly scoped, metadata-validated writes for a single EF Core entity without accepting
raw SQL or exposing raw connection strings. The planned `insert_entity`, `update_entity`, and
`delete_entity` tools complement read-only `run_query`; they are an explicitly enabled,
non-production escape hatch rather than a replacement for application workflows or bulk writes.

## Security model

- Resolve only a logical `connectionName` through `ConnectionRegistry` (or the active
  connection). Clients never provide connection strings.
- All three tools require `EntityMutations:Enabled` to be `true` (default `false`) and a
  non-production, `ReadWrite` registry entry. Production and `ReadOnly` entries are always
  rejected, even when the flag is enabled.
- Build and dispose a `DbContext` through the existing factory path. Do not accept SQL,
  expressions, arbitrary CLR type names, navigation graphs, or client-directed transactions.
- Log and return only safe identifiers and stable error classifications. Never disclose
  connection strings, values, provider exception text, inner exceptions, or stack traces.

## Tool contract

Each tool takes `contextName`, `entity`, and optional `connectionName`. `entity` is the exact
EF model entity name; all property names are exact EF-mapped scalar property names. Input values
are JSON values converted using the property CLR type/converter. Navigation properties, owned
object graphs, and bulk mutations are out of scope.

### `insert_entity`

| Parameter | Type | Notes |
|---|---|---|
| `contextName` | `string` | CLR `DbContext` type name returned by `list_contexts`. |
| `entity` | `string` | EF entity name. |
| `values` | `object` | Values for writable scalar properties. Required properties without a store-generated/default value must be supplied. |
| `connectionName?` | `string` | Logical registry connection; otherwise the active connection. |

Creates one entity and calls `SaveChanges` once. Database-generated keys and other values may be
returned only as EF-mapped scalar properties, subject to the normal response redaction policy.

### `update_entity`

| Parameter | Type | Notes |
|---|---|---|
| `contextName` | `string` | CLR `DbContext` type name returned by `list_contexts`. |
| `entity` | `string` | EF entity name. |
| `key` | `object` | Complete primary-key values by exact property name. |
| `values` | `object` | Non-empty set of writable scalar properties to change. |
| `concurrency?` | `object` | Original values for every concurrency-token property, when the entity has any. |
| `connectionName?` | `string` | Logical registry connection; otherwise the active connection. |

Updates exactly the entity identified by `key`; it must not treat omitted properties as null or
perform an upsert. The executor applies supplied original concurrency values before `SaveChanges`.

### `delete_entity`

| Parameter | Type | Notes |
|---|---|---|
| `contextName` | `string` | CLR `DbContext` type name returned by `list_contexts`. |
| `entity` | `string` | EF entity name. |
| `key` | `object` | Complete primary-key values by exact property name. |
| `concurrency?` | `object` | Original values for every concurrency-token property, when the entity has any. |
| `connectionName?` | `string` | Logical registry connection; otherwise the active connection. |

Deletes exactly the keyed entity. Cascade behavior remains the EF model/database's configured
behavior and must not be expanded into a client-selectable graph operation.

Successful responses have a common shape:

```jsonc
{
  "contextName": "MyAppContext",
  "connectionName": "dev",
  "entity": "User",
  "operation": "update",
  "affectedRows": 1,
  "values": { "Id": 42, "DisplayName": "Ada" }
}
```

`affectedRows` is the integer returned by the one `SaveChanges` call. A successful single-entity
operation reports its actual count; it must not claim that one row changed when EF reports zero.
`values` is returned for insert/update only when it can be produced from the tracked entity using
mapped scalar metadata; delete omits it.

## EF metadata validation

Resolve the target `IEntityType` before constructing or attaching an entity. Reject unknown,
ambiguous, key-incomplete, duplicate, shadow, navigation, inaccessible, and non-scalar property
names. Do not infer writable fields through reflection; determine them from EF metadata.

For `values`, reject any primary-key change on update and reject properties that EF marks computed,
store-generated, or read-only. This includes properties using computed SQL, `ValueGenerated` on
add/update, or metadata that prevents setting a current value. Insert may omit store-generated
properties, but callers may never supply them. Keys, requiredness, nullability, type conversion,
and configured value converters must be validated before database work.

## Concurrency and failures

An entity with one or more `IsConcurrencyToken` properties requires `concurrency` with an original
value for every token for update and delete; missing, extra, invalid, or write-only token values
are rejected before execution. The executor sets originals so EF includes them in the `UPDATE` or
`DELETE` predicate. A `DbUpdateConcurrencyException`, an affected-row count of zero, or a missing
keyed entity is returned as a stable not-found-or-concurrency-conflict result, never as a success
or an unredacted provider error. The result must identify the operation and safe entity/key
classification, but never echo client values.

## Configuration surface

Add an `EntityMutations` options block:

- `EntityMutations:Enabled` (boolean, default `false`) — master opt-in for all mutation tools.

No connection-level setting can override the mandatory production or `ReadOnly` rejection. The
existing connection environment, access mode, timeout, cancellation, disposal, and redaction rules
continue to apply.

## Focused test plan

- Tool-surface tests: method discovery/descriptions; parameter binding and forwarding; disabled,
  production, and `ReadOnly` rejection; and redacted, stable failures.
- Executor tests: unknown entity; key completeness; unknown, duplicate, shadow, navigation, and
  non-scalar properties; conversion/nullability/required-value failures; and rejection of computed,
  store-generated, read-only, and attempted key-update values before any write.
- Mutation tests: one successful insert, update, and delete; accurate `affectedRows` and result
  shape; exactly one `SaveChanges` call; and deterministic context disposal/cancellation.
- Concurrency tests: original-token requirement, token binding, zero-row and
  `DbUpdateConcurrencyException` conflict responses, and no success result after a conflict.
