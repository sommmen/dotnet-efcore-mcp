# Assembly loading

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: [`src/DotnetEfCoreMcp.Server/AssemblyLoading`](../../src/DotnetEfCoreMcp.Server/AssemblyLoading) ·
Tests: [`tests/DotnetEfCoreMcp.Server.Tests/AssemblyLoading`](../../tests/DotnetEfCoreMcp.Server.Tests/AssemblyLoading)

- [x] Load a target project's compiled output (`bin/Debug/<tfm>/*.dll`) given a file path
- [x] Load the assembly in an isolated, **collectible** `AssemblyLoadContext` so it can be unloaded/reloaded (e.g. after a rebuild) without restarting the server
- [x] Resolve the target assembly's dependencies (its own `.deps.json` / referenced DLLs alongside it), so it doesn't fail to load due to missing EF Core / provider assemblies
  - Implemented via `AssemblyDependencyResolver` in `TargetAssemblyLoadContext`, so the
    target project's own `.deps.json` / adjacent DLLs resolve correctly.
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
