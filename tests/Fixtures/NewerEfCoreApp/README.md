# NewerEfCoreApp

A reproduction fixture for DbContext discovery failing when the **target project references a
newer EF Core patch than the server does**.

## Why this fixture exists

Every other fixture under `tests/Fixtures/` inherits its EF Core version from the repo root
`Directory.Packages.props` through Central Package Management — the same file the server itself
uses. Host and target therefore *always* agree on the EF Core version, and the version-unification
path in `TargetAssemblyLoadContext.Load` is never exercised by the test suite.

This fixture sets `ManagePackageVersionsCentrally=false` and pins EF Core one patch **above** the
server's pinned version. That is the ordinary situation for a real target project that updated
EF Core before the MCP server did.

## The mechanism

`SharedFrameworkAssemblyNames` requires EF Core and
`Microsoft.AspNetCore.Identity.EntityFrameworkCore` to resolve from the **default**
`AssemblyLoadContext`, so that `typeof(DbContext).IsAssignableFrom(...)` keeps working across the
isolation boundary. `TargetAssemblyLoadContext.Load` implements that by returning `null` for those
simple names, which delegates the bind to the server's own PackageReference-restored copy.

The CLR's binder will satisfy an assembly reference with a **higher** version, but never with a
lower one. So when the target was compiled against a newer EF Core than the server restored, that
`null` cannot be satisfied: the bind fails with `FileNotFoundException`, every type whose base
chain reaches `DbContext` is dropped from `Assembly.GetTypes()`, and discovery reports
"no DbContext found" for an assembly that plainly has two.

## The two contexts

| Type | Base class | Extra dependency |
|---|---|---|
| `CatalogDbContext` | `DbContext` | none — isolates the EF Core version issue alone |
| `AccountDbContext` | `IdentityDbContext<IdentityUser>` | `Microsoft.AspNetCore.Identity.EntityFrameworkCore`, which lives in the **ASP.NET Core shared framework** |

The two are separate on purpose. `CatalogDbContext` should start being discovered as soon as the
EF Core version issue is addressed. `AccountDbContext` can keep failing after that, because
`DotnetEfCoreMcp.Server.runtimeconfig.json` declares only `Microsoft.NETCore.App` — so there is no
`Microsoft.AspNetCore.App` resolution context for the Identity assemblies to bind against.

## Reproducing

```bash
dotnet test tests/DotnetEfCoreMcp.Server.Tests \
  --filter "FullyQualifiedName~TargetAssemblyNewerEfCoreVersionTests"
```

All three tests in `TargetAssemblyNewerEfCoreVersionTests` fail on `main`; the other 42 tests in
the `AssemblyLoading` namespace pass unchanged.

Observed scan result for this fixture:

```
2 of 3 type(s) in the assembly failed to load and were excluded from DbContext discovery.
Type load error: Could not load file or assembly 'Microsoft.EntityFrameworkCore, Version=10.0.14.0, Culture=neutral, PublicKeyToken=adb9793829ddae60'.
Type load error: Could not load file or assembly 'Microsoft.AspNetCore.Identity.EntityFrameworkCore, Version=10.0.14.0, Culture=neutral, PublicKeyToken=adb9793829ddae60'.
```

## Maintenance note

The pinned versions here must stay **ahead** of the root `Directory.Packages.props` EF Core
version for the fixture to keep reproducing. If Renovate bumps the server to `10.0.14` or higher,
bump this fixture too — otherwise these tests will start passing for the wrong reason.
