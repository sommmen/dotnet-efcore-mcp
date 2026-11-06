# NewerEfCoreApp

Regression fixture for DbContext discovery against a target project that references a **newer
EF Core patch than the server does**
([#115](https://github.com/sommmen/dotnet-efcore-mcp/issues/115)).

## Why this fixture exists

Every other fixture under `tests/Fixtures/` inherits its EF Core version from the repo root
`Directory.Packages.props` through Central Package Management — the same file the server itself
uses. Host and target therefore *always* agree on the EF Core version, so without this fixture no
test exercises the version-unification path in `TargetAssemblyLoadContext.Load`, and a patch-level
mismatch can ship unnoticed.

This fixture sets `ManagePackageVersionsCentrally=false` and pins EF Core one patch **above** the
server's pinned version. That is the ordinary situation for a real target project that updated
EF Core before the MCP server did.

## The bug this guards against

`SharedFrameworkAssemblyNames` requires EF Core and
`Microsoft.AspNetCore.Identity.EntityFrameworkCore` to resolve to a **single** copy, so that
`typeof(DbContext).IsAssignableFrom(...)` keeps working across the isolation boundary.
`TargetAssemblyLoadContext.Load` used to implement that by returning `null` for those simple names,
delegating the bind to the default context.

Delegating that way forwards the *target's own* reference, including the version it was compiled
against — and the CLR binder satisfies a reference only with an equal-or-**higher** version, never
a lower one. So when the target was compiled against a newer EF Core than the server restored, the
bind failed with `FileNotFoundException`, every type whose base chain reached `DbContext` was
dropped from `Assembly.GetTypes()`, and discovery reported "no DbContext found" for an assembly
that plainly had two:

```
2 of 3 type(s) in the assembly failed to load and were excluded from DbContext discovery.
Type load error: Could not load file or assembly 'Microsoft.EntityFrameworkCore, Version=10.0.14.0, Culture=neutral, PublicKeyToken=adb9793829ddae60'.
Type load error: Could not load file or assembly 'Microsoft.AspNetCore.Identity.EntityFrameworkCore, Version=10.0.14.0, Culture=neutral, PublicKeyToken=adb9793829ddae60'.
```

`SharedAssemblyResolver` now resolves those names by **simple name**, which ignores the requested
version entirely while still yielding one shared copy. The gap is reported as a
`LoadedAssemblyHandle.DependencyDiagnostics` warning naming both versions, so a genuine
incompatibility stays attributable instead of silent.

## The two contexts

| Type | Base class | Extra dependency |
|---|---|---|
| `CatalogDbContext` | `DbContext` | none — covers the EF Core version mismatch alone |
| `AccountDbContext` | `IdentityDbContext<IdentityUser>` | `Microsoft.AspNetCore.Identity.EntityFrameworkCore`, which also ships in the ASP.NET Core shared framework |

The two are separate on purpose: they failed for independent reasons, so a partial fix shows up as
one passing and one failing rather than as a single opaque result.

## Running these tests

```bash
dotnet test tests/DotnetEfCoreMcp.Server.Tests \
  --filter "FullyQualifiedName~TargetAssemblyNewerEfCoreVersionTests"
```

## Maintenance note

The pinned versions here must stay **ahead** of the root `Directory.Packages.props` EF Core
version for this fixture to keep covering the mismatch. If Renovate bumps the server to `10.0.14`
or higher, bump this fixture too — otherwise these tests keep passing for the wrong reason, because
host and target would agree again.
