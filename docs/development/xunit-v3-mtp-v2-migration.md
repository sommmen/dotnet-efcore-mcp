# Plan: migrate tests to `xunit.v3.mtp-v2` 4.0.1

Migrates the test project from xUnit v2 on VSTest to xUnit v3 on Microsoft Testing
Platform v2, aligning this repository with the estate-wide test standard.

The shared standard, target recipe and CI command reference live in the `stallions`
repository at `docs/guides/Testing/xunit-v3-mtp-v2-standard.md`.

## 1. Current state

There is exactly **one** test project — `tests/DotnetEfCoreMcp.Server.Tests` — and
eight *fixture applications* under `tests/Fixtures/`. The distinction is the most
important fact in this plan; see §2.

Central versions in `Directory.Packages.props`:

| Package | Version | Fate |
|---|---|---|
| `xunit` | 2.9.3 | → `xunit.v3.mtp-v2` 4.0.1 |
| `xunit.runner.visualstudio` | 4.0.0 | remove (VSTest adapter) |
| `Microsoft.NET.Test.Sdk` | 18.10.1 | remove (VSTest host) |
| `coverlet.collector` | 10.1.0 | remove (VSTest collector) |

The test project also references `Microsoft.EntityFrameworkCore.InMemory` and
`System.Reflection.MetadataLoadContext`; both are framework-agnostic and stay.

Other relevant facts:

- Central package management is enabled (`ManagePackageVersionsCentrally=true`).
- `tests/Directory.Build.props` sets `IsPackable=false` for everything under `tests/`.
- No `global.json`.
- Solution: `dotnet-efcore-mcp.slnx`. CI: `.github/workflows/ci.yml` (line 38) and
  `.github/workflows/publish.yml` (line 51), both running
  `dotnet test dotnet-efcore-mcp.slnx --no-build --no-restore --configuration Release --verbosity normal`.

### Migration surface, measured

| Construct | Count | Impact |
|---|---:|---|
| `[Fact]` | 450 | none |
| `[Theory]` | 27 | none |
| `[InlineData]` | 96 | none |
| `Assert.ThrowsAsync` | 76 | none |
| `[CollectionDefinition]` | 1 | none — see §3 |
| `[Collection]` | 3 | none |
| `IAsyncLifetime` | 0 | none |
| `async void` | 0 | none |
| `ITestOutputHelper` | 0 | none |

**No source changes are required.** `[CollectionDefinition]`, `[Collection]`,
`DisableParallelization`, `[Fact]`, `[Theory]`, `[InlineData]` and `Assert.ThrowsAsync`
all have identical shapes in v3, and the two constructs that do break between v2 and v3
(`IAsyncLifetime` returning `ValueTask`, and `async void` tests) do not occur here.
This makes the repository a pure packaging migration.

### This plan has been trial-validated

The full change described in §4 was applied to a working copy and then reverted. The
result:

- `dotnet restore` and `dotnet build` succeeded;
- `dotnet test` reported **546 passed, 0 failed**, exactly matching the VSTest
  baseline of 546 tests captured before the change;
- no source file needed editing;
- the only delta was **150 `xUnit1051` analyzer warnings** — see §5.1.

So the steps below are known to work, not merely expected to. The baseline number to
verify against is **546**.

## 2. The fixture applications must not be treated as test projects

`tests/Fixtures/` contains eight ordinary applications — `SampleApp`, `IdentityApp`,
`NoContextApp`, `SplitContextApp`, `SplitMigrationsApp`, `PackageDependencyApp`,
`BrokenDependencyApp`, `BrokenDependencyApp.Dependency`. They exist to be *inspected*
by the server under test: the test project deliberately loads `SampleApp`'s built DLL
at runtime through the same `AssemblyLoadContext` path the server uses for real target
projects, which is why it is referenced for build output only
(`ReferenceOutputAssembly="false"`) rather than as a normal reference.

Consequences for this migration:

- None of the eight gets `OutputType=Exe` added, `TestingPlatformDotnetTestSupport`,
  or the `xunit.v3.mtp-v2` package.
- Any `Directory.Build.props` change must **not** apply `OutputType=Exe` to everything
  under `tests/`. Target the single test project explicitly, or set the two properties
  directly in `DotnetEfCoreMcp.Server.Tests.csproj`.

Setting `OutputType=Exe` on the fixtures would change their build output and could
break the assembly-loading tests that are the core of this repository's value — a
failure that would surface as confusing runtime errors rather than a build break.
Given there is only one test project, **set the properties directly in that one
project file** and leave `tests/Directory.Build.props` alone.

## 3. Collection fixtures carry over unchanged

`tests/DotnetEfCoreMcp.Server.Tests/TestSupport/ApplicationFactoryEnvironmentCollection.cs`
declares `[CollectionDefinition(Name, DisableParallelization = true)]`, applied by three
test classes:

- `DbContextDiscovery/DbContextActivatorTests.cs:9`
- `Querying/OutOfProcessRoslynQueryExecutorTests.cs:13`
- `Tools/EfCoreMcpToolsApplicationFactoryTests.cs:26`

This pattern is unchanged in v3. The serialization it provides matters here (these
tests mutate process-wide environment state), so verify after migration that the three
classes still do not run in parallel — a regression would show up as flaky,
order-dependent failures rather than a clean break.

## 4. Changes

### 4.1 `Directory.Packages.props`

Remove `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` and
`coverlet.collector`. Add:

```xml
<PackageVersion Include="xunit.v3.mtp-v2" Version="4.0.1" />
<PackageVersion Include="Microsoft.Testing.Extensions.GitHubActionsReport" Version="2.4.1" />
```

### 4.2 `tests/DotnetEfCoreMcp.Server.Tests/DotnetEfCoreMcp.Server.Tests.csproj`

Add to the existing `PropertyGroup`:

```xml
<OutputType>Exe</OutputType>
<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
```

Replace the four removed `PackageReference` entries with:

```xml
<PackageReference Include="xunit.v3.mtp-v2" />
<PackageReference Include="Microsoft.Testing.Extensions.GitHubActionsReport" />
```

Keep `<Using Include="Xunit" />`, `Microsoft.EntityFrameworkCore.InMemory`,
`System.Reflection.MetadataLoadContext`, and both `ProjectReference` entries — including
the `ReferenceOutputAssembly="false"` one, whose comment explains why it is not a normal
reference.

### 4.3 New `global.json`

At the repository root, next to `dotnet-efcore-mcp.slnx`:

```json
{
  "sdk": {
    "version": "10.0.401",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

### 4.4 CI

Both workflows keep working unchanged — a plain `dotnet test <solution>` is valid under
MTP. Optionally add `--report-gh` to get GitHub annotations and a run summary, which is
what §4.1 adds the extension package for.

One thing to watch in `publish.yml`: it runs `dotnet pack` over the whole solution.
`OutputType=Exe` on the test project does not make it packable, because
`tests/Directory.Build.props` already forces `IsPackable=false` for everything under
`tests/`. Confirm `dotnet pack` still produces exactly the expected package set after
the change.

### 4.5 Coverage

`coverlet.collector` is a VSTest data collector with no MTP equivalent. Nothing in CI
consumes coverage output today, so drop it; add
`Microsoft.Testing.Extensions.CodeCoverage` 18.12.0 with `dotnet test --coverage` later
if coverage is ever wanted.

## 5. Known consequences

### 5.1 150 new `xUnit1051` analyzer warnings

`xunit.v3.mtp-v2` 4.0.1 brings `xunit.analyzers` 2.1.0, which fires `xUnit1051`
("calls to methods which accept `CancellationToken` should use
`TestContext.Current.CancellationToken`"). The trial migration produced exactly 150 of
these and no other new rule.

This repository does **not** set `TreatWarningsAsErrors`, so the build still succeeds
and these are advisory. Three options, in order of preference:

1. **Land the migration, then fix the warnings separately.** Passing
   `TestContext.Current.CancellationToken` genuinely improves test cancellation
   responsiveness, so this is real value rather than noise-silencing.
2. Suppress narrowly with `<NoWarn>$(NoWarn);xUnit1051</NoWarn>` in the test project if
   the warning volume obscures other output.
3. Fix all 150 as part of the migration — defensible, but it turns a zero-source-change
   migration into a large diff, which makes the packaging change harder to review.

Prefer option 1 and open a follow-up issue.

## 6. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| A `Directory.Build.props` change leaks `OutputType=Exe` onto the eight fixture apps | Medium | Set the properties in the single test project file only; never under `tests/` broadly (§2) |
| Assembly-loading tests break | Low | They depend on fixture build output, which is unchanged; run `SharedFrameworkAssemblyClosureTests` and the `AssemblyLoadContext` tests explicitly |
| Collection serialization regression | Low | Verify the three `[Collection]` classes still serialize (§3) |
| Zero discovery | Medium | Missing `OutputType=Exe`; compare against the baseline test count |
| `dotnet pack` output changes | Low | `IsPackable=false` under `tests/` already covers it; verify the package set (§4.4) |

## 7. Verification

```powershell
dotnet restore dotnet-efcore-mcp.slnx
dotnet build dotnet-efcore-mcp.slnx --configuration Release
dotnet test dotnet-efcore-mcp.slnx --no-build --configuration Release --verbosity normal
dotnet pack dotnet-efcore-mcp.slnx --configuration Release
```

Definition of done:

1. No `xunit` (v2), `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` or
   `coverlet.collector` entry remains in `Directory.Packages.props`.
2. `DotnetEfCoreMcp.Server.Tests` references `xunit.v3.mtp-v2` 4.0.1; none of the eight
   fixture projects reference it or set `OutputType=Exe`.
3. `dotnet test` reports **546 passed, 0 failed** — the trial-validated figure (§3).
4. `dotnet pack` produces the same package set as before.
5. `ci` green on the pull request.
