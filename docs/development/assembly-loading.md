# Assembly loading

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: [`src/DotnetEfCoreMcp.Server/AssemblyLoading`](../../src/DotnetEfCoreMcp.Server/AssemblyLoading) ·
Tests: [`tests/DotnetEfCoreMcp.Server.Tests/AssemblyLoading`](../../tests/DotnetEfCoreMcp.Server.Tests/AssemblyLoading)

- [x] Load a target project's compiled output (`bin/Debug/<tfm>/*.dll`) given a file path
- [x] Load the assembly in an isolated, **collectible** `AssemblyLoadContext` so it can be unloaded/reloaded (e.g. after a rebuild) without restarting the server
- [x] Resolve the target assembly's dependencies (its own `.deps.json` / referenced DLLs alongside it), so it doesn't fail to load due to missing EF Core / provider assemblies
  - Resolution in `TargetAssemblyLoadContext` is two-stage. `AssemblyDependencyResolver` is
    tried first because it applies the host's own asset-selection rules, then
    `TargetDependencyProbe` handles what it cannot see.
  - `AssemblyDependencyResolver` alone is **not** sufficient for the common case. It reads the
    target's `.deps.json` for package-relative paths but needs *probing roots* to resolve them
    against, and those only exist when the build emits a `*.runtimeconfig.dev.json` or bakes in
    `additionalProbingPaths`. A plain `dotnet build` of a **class library** emits neither, and
    the SDK does not copy NuGet DLLs into `bin/` without `CopyLocalLockFileAssemblies=true`. So
    for a typical DAL library every `"type": "package"` entry resolves to `null` and only
    adjacent project outputs are found — which previously surfaced as mass type-load failures
    and zero discovered `DbContext`s.
  - `TargetDependencyProbe` closes that gap the way the host would, without requiring the target
    project to be rebuilt or modified:
    - **Probing roots**, in order: `*.runtimeconfig.dev.json` `additionalProbingPaths`, then
      `packageFolders` from the target's `obj/project.assets.json` (found by walking up from the
      output folder), then `NUGET_PACKAGES`, then `%USERPROFILE%/.nuget/packages`. Reading
      `packageFolders` is the same trick `dotnet-ef` uses — it respects custom package folders
      without re-implementing NuGet config resolution.
    - **Asset selection** from `.deps.json`: `runtime`, `native` and RID-specific
      `runtimeTargets` groups, skipping the `_._` placeholder. The output folder is always
      tried first so a fresh build wins over a stale cache copy.
    - **Shared frameworks**: the target's `runtimeconfig.json` frameworks (or, for a class
      library that has none, `frameworkReferences` from the assets file) are mapped onto
      installed shared frameworks with roll-forward, so a target built against
      `Microsoft.AspNetCore.App 10.0.0` binds to an installed `10.0.11`.
      `Microsoft.NETCore.App` is deliberately **excluded** — the process already chose its
      runtime, and loading a second copy of `System.*` would break type identity rather than
      fix anything.
  - Anything neither stage resolves returns `null` and falls back to the default context, which
    is the correct home for genuinely shared framework types.
  - **Shared type identity:** assemblies referenced by the server and target whose types cross a
    public API boundary must resolve to the server's default context, not a second target-local
    copy. `TargetAssemblyLoadContext.SharedAssemblyNames` includes EF Core and its required
    companion assemblies, including `Microsoft.Extensions.Logging` and
    `Microsoft.Extensions.Logging.Abstractions`. Omitting one can create incompatible copies of
    types used by shared EF Core diagnostics and surface as runtime member-resolution failures
    such as `MissingFieldException` for `RelationalEventId.CommandExecuting`. When adding a shared
    assembly, also share every assembly that defines a type exposed by its public
    fields, parameters, or return values.
  - Non-fatal problems (e.g. a required shared framework that is not installed) are collected as
    `LoadedAssemblyHandle.DependencyDiagnostics` and surfaced ahead of type-load warnings in the
    `load_assembly` result, so the cause is reported above its symptoms.
- [x] Handle assembly load failures gracefully (missing file, wrong TFM/runtime mismatch, missing dependencies) with clear error messages surfaced to the MCP client
- [x] Detect and handle stale/locked DLLs (e.g. project was rebuilt while the server was running)
  - `AssemblyLoaderService` exposes a staleness check based on the DLL's last-write time
    vs. what was loaded; `load_assembly` can always be called again to force a reload
    (old collectible `AssemblyLoadContext` is unloaded first).
- [x] Automatically reload the loaded assembly when its DLL changes on disk (e.g. MSBuild finishes a rebuild), so the server stays up to date without a manual `load_assembly` call
  - `AssemblyReloadWatcher` (a hosted service) watches the currently loaded assembly's
    file with a `FileSystemWatcher`, debounces rapid successive write events (MSBuild
    writes the DLL more than once per build), and re-invokes `AssemblyLoaderService.Load()`
    — reusing its existing file-lock probing so a DLL still being written mid-build is
    retried (bounded attempts) rather than treated as a hard failure. Only active once
    an assembly has been loaded (manually or via startup auto-discovery); failures
    during a watched reload log a warning and keep serving the previously loaded
    assembly rather than crash. Opt out with `AssemblyLoader:AutoReloadEnabled=false`.

## Proposed open work — P2 #15: named multi-target assembly registry

Today the server holds exactly one loaded target (`AssemblyLoaderService.Current`); loading a
new assembly always unloads and replaces it. See [Multi-target assembly
registry](./assembly-registry.md) for the proposed named registry that lets several targets
stay loaded at once, each with its own isolated `AssemblyLoadContext` and reload watcher,
while preserving today's single-target behavior for callers that never pass a `targetName`.
