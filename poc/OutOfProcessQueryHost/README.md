# PoC: out-of-process, target-runtime EF Core query execution

This is a small, throwaway proof of concept for the architecture proposed in
[`docs/development/query-execution-alternatives.md`](../../docs/development/query-execution-alternatives.md):
instead of loading a target app's assembly into the MCP server's own process (today's model,
which forces the server to share EF Core/provider assembly identity with the target app), run
the query in a **separate child process** launched under the **target app's own runtime
configuration** - the same pattern `dotnet ef` uses for its design-time tooling, and
conceptually the same thing LINQPad does with its query host.

This code is **not** part of the server (`src/DotnetEfCoreMcp.Server`) and is not referenced by
it or by the main solution (`dotnet-efcore-mcp.slnx`). It is a standalone exploration, kept
under `poc/` so it can be built/run/deleted independently without touching production code.

## What it demonstrates

Three independently-built projects, with **no compile-time references between them**:

| Project | Role | Stands in for |
|---|---|---|
| `TargetApp/` | A tiny EF Core app with one `DbContext` (`CatalogDbContext`) and one entity (`Product`), using SQL Server. | The arbitrary already-built target application the real MCP server points at. |
| `QueryHost/` | A console app that loads `TargetApp.dll` **from a file path**, finds the `DbContext` type and a `DbSet` property by name via reflection, and runs a caller-supplied filter with [System.Linq.Dynamic.Core](https://dynamic-linq.net/) - no compile-time reference to `TargetApp`'s types. This is historical PoC code, not the current server's supported `run_query` engine. | The out-of-process "query host" from the alternatives doc - it would host the Roslyn-compiled `UserQuery` in a real implementation. |
| `Launcher/` | A console app that locates `TargetApp`'s own `TargetApp.runtimeconfig.json` and `TargetApp.deps.json`, then launches `QueryHost.dll` as a child process via `dotnet exec --runtimeconfig <TargetApp's> --depsfile <TargetApp's> QueryHost.dll ...`. | The MCP server process. |

The key mechanic is the `dotnet exec` invocation `Launcher` runs:

```text
dotnet exec --runtimeconfig TargetApp.runtimeconfig.json --depsfile TargetApp.deps.json QueryHost.dll <args>
```

This executes `QueryHost.dll` (a separately compiled assembly) but resolves its dependencies
(`Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.SqlServer`, ...) using
**`TargetApp`'s** dependency graph and roll-forward policy, not `QueryHost`'s own `bin` output.
That is exactly the trick `dotnet ef` relies on to run its design-time assembly under a target
project's runtime - see the "`dotnet ef` model" section of the alternatives doc. `QueryHost`
prints its own process ID and the on-disk location of the `Microsoft.EntityFrameworkCore.dll`
it actually loaded, so you can see which dependency closure won.

> **Scope note:** this environment only has one .NET SDK (`net10.0`) installed, so `TargetApp`
> and `QueryHost` are built for the same TFM/EF Core version here - this PoC cannot exercise an
> actual cross-major-version scenario on this machine. What it does prove end-to-end is the
> mechanism itself: a real child process, launched under a target app's own runtimeconfig/
> depsfile rather than the launcher's, loading the target's `DbContext` by reflection and
> executing a real query against a real database. Swapping in a `TargetApp` built for a
> different installed SDK/TFM would exercise the cross-version case without changing
> `Launcher` or `QueryHost` at all.

## Prerequisites

- .NET SDK with `net10.0` (already required by the rest of this repo).
- SQL Server LocalDB (`sqllocaldb.exe`) - this PoC uses it for a real end-to-end run against an
  actual database rather than mocking anything out. Check with:

  ```powershell
  sqllocaldb info
  ```

If LocalDB isn't available on your machine, switch `TargetApp`'s package reference from
`Microsoft.EntityFrameworkCore.SqlServer` to `Microsoft.EntityFrameworkCore.Sqlite`, change
`QueryHost/Program.cs`'s `builder.UseSqlServer(connectionString)` call to
`builder.UseSqlite(connectionString)`, and pass a file-based connection string instead.

## Run it

From the repo root:

```powershell
dotnet build poc\OutOfProcessQueryHost\TargetApp\TargetApp.csproj
dotnet build poc\OutOfProcessQueryHost\QueryHost\QueryHost.csproj
dotnet run --project poc\OutOfProcessQueryHost\Launcher
```

`Launcher` seeds a handful of `Product` rows into a `EfCoreMcpPoc` LocalDB database on first run
(via `EnsureCreated` + a plain `SaveChanges`, no migrations - kept minimal on purpose) and then
runs `Where("Price > 20")`. Pass different arguments to change the connection string and/or
predicate:

```powershell
dotnet run --project poc\OutOfProcessQueryHost\Launcher -- "Server=(localdb)\MSSQLLocalDB;Database=EfCoreMcpPoc;Trusted_Connection=True;TrustServerCertificate=True;" "Category == \"Electronics\""
```

To reset and re-seed, drop the database:

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "DROP DATABASE IF EXISTS EfCoreMcpPoc"
```

## What "success" looks like

`Launcher`'s own stdout shows its PID, the `dotnet exec` command it ran, each line of the
child's stderr diagnostics (prefixed `[child stderr]`), the child's exit code/elapsed time, and
finally the parsed JSON result: the **child's** PID (proving it's a different process), the
`Microsoft.EntityFrameworkCore.dll` path it loaded, the row count, and the matching rows -
proving a real query executed against a real SQL Server LocalDB database, out-of-process, using
the target app's own runtime wiring rather than the launcher's.

## Non-goals

This intentionally does **not** implement: IPC beyond stdout/stderr + process exit code, a warm
host pool, cancellation/timeouts, the Roslyn-compiled `UserQuery` class, or result-shaping to
match the real `run_query` MCP tool contract. Those are the natural next steps described in the
alternatives doc, once the core out-of-process/target-runtime mechanism itself is validated -
which is all this PoC set out to do.
