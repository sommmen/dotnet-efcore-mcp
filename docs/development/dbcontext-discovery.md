# DbContext discovery

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: [`src/DotnetEfCoreMcp.Server/DbContextDiscovery`](../../src/DotnetEfCoreMcp.Server/DbContextDiscovery) ·
Tests: [`tests/DotnetEfCoreMcp.Server.Tests/DbContextDiscovery`](../../tests/DotnetEfCoreMcp.Server.Tests/DbContextDiscovery)

- [x] Scan a loaded assembly for types deriving from `Microsoft.EntityFrameworkCore.DbContext`
- [x] Support assemblies with multiple `DbContext` types (list them, let the caller pick one by name)
- [x] Handle `DbContext` types that require constructor arguments (e.g. `DbContextOptions<T>`) — construct via `DbContextOptionsBuilder` rather than assuming a parameterless constructor
- [x] Support `DbContext` types configured via `OnConfiguring` (no options passed in) as a distinct case from ones requiring externally-supplied options
- [x] Handle design-time factories (`IDesignTimeDbContextFactory<T>`) if present, as an alternative construction path
