# CLI surface

[← Back to Development Guide](../../DEVELOPMENT.md)

Status: **delivered** (MCP host and CLI on `dotnet-agent-surface` 0.1.77-preview, session-backed equal flows,
hooks and docs). Open: the background daemon and the CLI-only package (E1), both listed under
[Future work](#future-work). Tracked in the [work tracker](./WORK-TRACKER.md).

The sections below are the original plan and its rationale. Where the shipped design differs, [What was
delivered](#what-was-delivered) is authoritative.

## What was delivered

| Item | Result |
| --- | --- |
| A. Upstream gaps ([agent-surface#88–#92](https://github.com/sommmen/dotnet-agent-surface/issues?q=is%3Aissue+88+OR+89+OR+90+OR+91+OR+92)) | Fixed upstream and published as `DotNetAgentSurface.*` 0.1.77-preview on nuget.org. |
| B. Composition refactor | `ServerComposition` (done earlier). |
| C. CLI mode | `dotnet-efcore-mcp cli <operation>` through `AgentSurfaceApplication` in [`Cli/EfCoreSurface.cs`](../../src/DotnetEfCoreMcp.Server/Cli/EfCoreSurface.cs). Host verbs `hooks`, `onboard`, `version`; global options `--assembly`, `--workspace`, `--target`, `--connection`, `--config Key=Value` (plus upstream `--session`); fail-fast on an unloadable `--assembly`; `Pooled` forced to `Auto`; `Warning` logs on stderr; CLI wording for missing target/connection (`IToolSurfaceHints`); `cli skill generate\|check`. |
| C3. Cursor key | Replaced by the session: `run_query` cursors are signed with a stable key stored in the session as `cursorKey` unless `QueryExecution:CursorSigningKey` is configured. No bespoke key file. |
| C5. Hooks | `ToolCallClassifier` recognises `dotnet-efcore-mcp cli <query-operation>` (snake or kebab case); `HookReminder` has CLI wording. |
| C4. Tests | [`tests/DotnetEfCoreMcp.Server.Tests/Cli`](../../tests/DotnetEfCoreMcp.Server.Tests/Cli): operations, pagination across runs, byte-identical output, exit codes, `--help`, session flows, hooks, and a built-DLL process smoke test. |
| C6, D. Renovate and docs | `DotNetAgentSurface.*` Renovate group (exact pin, no automerge); README "Choose MCP or CLI"; this folder's docs updated. |
| F. MCP host on agent-surface | `WithOperations` over `AddMcpServerTools`; the `tools/list` snapshot matched without regeneration and the full suite passes unchanged. F3 (moving telemetry/redaction into an invocation filter) and F4 (tool annotations) are not done. |
| G. Equal flows through session state | `load_assembly`, `select_target` and `swap_connection` stay available in the CLI and persist `targets`, `defaultTarget` and `activeConnection` through `IAgentSession`; `EfCoreSessionInitializer` rehydrates them at CLI start. Per-call `--assembly`/`--connection` stay as overrides. Never store secrets in the session. |

## Future work

- **Daemon / warm server.** Each CLI call reloads the assembly and warms Roslyn and EF Core. A background daemon (the CLI as a thin client to a warm server process) would remove that latency. It needs IPC, lifecycle, auth and stale-build handling, and it can reuse the session state model.
- **E1: a CLI-only package** (`DotnetEfCore.Cli`, command e.g. `efcore-cli`) with no `McpServer` package type, defaulting to CLI mode. Not started; see E1 below for what it needs.
- **E2, E3:** `onboard` installing the generated CLI skill, and `--confirm` for mutation and raw-SQL operations.
- **F3, F4:** share telemetry and redaction through an `IOperationInvocationFilter`; annotate tools with `ReadOnly`/`Destructive`/`Idempotent` (a deliberate `tools/list` change).

## Goal

Let consumers choose how they run `dotnet-efcore-mcp`:

- **MCP**: today's stdio MCP server. This stays the default, and its behaviour does not change.
- **CLI**: a plain command-line tool that runs the same operations, one per process. It is
  for agents that call shell commands, and for skills, scripts and CI. No MCP client is needed.

**Both surfaces** are built on [`sommmen/dotnet-agent-surface`](https://github.com/sommmen/dotnet-agent-surface)
(`DotNetAgentSurface.Core`, `.Mcp`, `.CommandLine`). agent-surface is meant to be the base for
this server and for future MCP servers, so we don't keep a parallel hand-wired MCP stack. The
library is not in production yet, and the gaps it must close first are tracked upstream:

| Issue | Scope | Blocks |
| --- | --- | --- |
| [agent-surface#88](https://github.com/sommmen/dotnet-agent-surface/issues/88) | Bug: `AddMcpServerTools` imports default tools as `Dangerous`, and the CLI crashes. Adds an `include` predicate. | everything |
| [agent-surface#89](https://github.com/sommmen/dotnet-agent-surface/issues/89) | Publish to nuget.org; align ModelContextProtocol/Toon.DotNet; make CommandLine a library package. | any package reference |
| [agent-surface#90](https://github.com/sommmen/dotnet-agent-surface/issues/90) | MCP adapter parity with the SDK: `IMcpServerBuilder` integration, `[Description]`, schema parity, result renderers, error mapping, annotations, context injection, invocation filters, parity test helper. | MCP migration (phase 2) |
| [agent-surface#92](https://github.com/sommmen/dotnet-agent-surface/issues/92) | Dual-surface `IHost` hosting, output fidelity, argument ergonomics, global options, per-surface visibility, help/skill generation. | CLI (phase 1) |
| [agent-surface#91](https://github.com/sommmen/dotnet-agent-surface/issues/91) | Surface-neutral session state, so stateful MCP flows also work across CLI calls. | equal MCP/CLI flows (phase 3) |

## Hard constraints

1. **No MCP regressions.** Existing behaviour stays the same: `FlushOnDisconnectStdioTransport`,
   server instructions, TOON/JSON output, `McpException` error semantics, tool names, and the
   `tools/list` schemas and descriptions. All existing tests keep passing unchanged. The MCP host
   moves onto agent-surface only once #90 reaches parity, verified by a `tools/list` diff test
   (B2).
2. **One tool implementation.** Both surfaces invoke `EfCoreMcpTools` and its private
   `Execute`/`ExecuteAsync` wrappers (telemetry, redaction, error references). Neither forks
   that logic. Once #90 R8 (invocation filters) lands, those wrappers can move into a shared
   filter.
3. **Pre-host verbs keep working.** `hooks`, `onboard` and `version` keep working, and
   launching with no arguments (or only `--Key=Value` config arguments) still starts the MCP
   server.

## Key findings that shape the design

| Finding | Consequence |
| --- | --- |
| All 24 tools return `string`/`Task<string>`, already formatted by `IToolResultFormatter`. They throw `McpException` on failure. They use no ambient MCP context (`McpServer`, `IProgress`, sampling, roots, or prompts/resources). | The tools are already transport-agnostic apart from the exception type. The agent-surface importer `AddMcpServerTools(assembly, targetFactory)` can wrap them as-is. |
| State lives in-process: assembly registry and default target, the active connection set by `swap_connection`, the `QueryHostPool`, and the per-process random `QueryExecution:CursorSigningKey`. | A CLI call starts empty. Target and connection must be supplied per invocation (or through config), and cursors need a stable key to survive across calls. |
| `load_assembly`, `select_target`, `swap_connection` and `reload_connections` only mutate process state. | In the CLI they are no-ops that mislead the caller. Exclude them, or replace them with per-invocation options. Error text that names them (`EfCoreMcpTools.cs` ~1399, ~1690, ~1748) also needs CLI-aware wording. |
| The agent-surface CLI adapter parses `--param <json-or-string>`, with no `--x=y` and no bare boolean switches. **Any value that parses as JSON is parsed as JSON**, so `--connectionName null` or `--name 123` fails to bind to `string`. | Flag ergonomics need upstream work (A5). |
| CLI output, by renderer setting. With **no** renderer, a `string` result is emitted as a quoted, escaped JSON string. **With** a renderer, `AgentOutputProjector` truncates strings at 1,000 characters unless `--full` is passed. In both cases `CleanOperationError` flattens multi-line errors. | A pass-through for string results is **mandatory** (A3), not cosmetic. Otherwise TOON results get silently cut. |
| `AddMcpServerTools` maps `Destructive ? Dangerous : Safe`. `McpServerToolAttribute.Destructive` **defaults to `true`**, and `EfCoreMcpTools` sets neither `ReadOnly` nor `Destructive`. | All 24 tools would import as `Dangerous`. `OperationInvoker` then throws `ConfirmationPolicyMissingException` *outside* its try/catch, so every CLI call crashes. A4 is a hard prerequisite. |
| The importer uses `McpServerTool.Name` verbatim. | CLI operation names are `run_query`, `list_contexts` and so on. Kebab-case aliases (`run-query`) need A4/A5 upstream. |
| The adapter strips `--output`, `--fields`, `--full`, `--confirm` and `--yes` from **anywhere** in the arguments. | CLI globals and subcommand flags must avoid these names. For example, use `--out-dir` instead of `--output` for skill generation, and don't add a separate `--format`. |
| agent-surface packages are published to GitHub Packages only. This repo's `NuGet.Config` lists nuget.org only. It pins MCP 2.2.0 and Toon.DotNet 3.3.2; this repo uses 2.3.1 and 4.1.1. | A nuget.org release and dependency bumps upstream are prerequisites. |
| Hooks (`ToolCallClassifier.IsEfCoreMcpTool`) recognise only MCP tool names. | Agents that use the CLI would get no query-validation reminders unless the classifier learns the CLI command shape. |
| `publish.yml` stamps the npm version from `DotnetEfCoreMcp.Server.*.nupkg` using `-First 1`. | A sibling package named `DotnetEfCoreMcp.Server.<X>` would collide with that pattern. Choose a non-prefixed package id. |

## Design

### Mode selection and packaging

The same binary serves both modes. A new pre-host verb selects the CLI:

```
dotnet-efcore-mcp                       # MCP server (unchanged)
dotnet-efcore-mcp cli <operation> ...   # CLI mode
dotnet-efcore-mcp cli --help
```

For users who want "just a CLI", publish a **second, thin tool package** in phase 3. It has id
`DotnetEfCore.Cli` and a command name such as `efcore-cli` (see E1). It ships the same server build, but with
no `McpServer` package type. Its apphost defaults to CLI mode. One way to do that is an
assembly-metadata or `runtimeconfig` switch read before the verb dispatch, so
`efcore-cli <operation>` works without the `cli` prefix. The npm wrapper gets a matching
`bin` entry.

Phase 1 ships only the `cli` verb in the existing package. It adds no new package, so the
existing install paths (`dnx`, global/local tool, npm) gain the CLI immediately.

### Composition refactor (behaviour-preserving)

Extract the service registrations from `Program.cs` into `ServerComposition.AddEfCoreServices(HostApplicationBuilder)`.
This covers configuration layering, options sections, singletons, telemetry and
`IToolResultFormatter`. Also extract the startup preload into `ServerComposition.PreloadTargets(IServiceProvider, IConfiguration, ILogger)`.
`Program.cs` then becomes:

1. Pre-host verb dispatch: `hooks`, `onboard`, `version` and the new `cli`.
2. MCP: `AddEfCoreServices` → `AddMcpServer(...)` / `ITransport` override → `Build` →
   `PreloadTargets` → `RunAsync`.

The MCP path is the same code in the same order, just moved. `McpServerShutdownTests`,
`UpdateNotificationStdoutTests` and `FlushOnDisconnectStdioTransportTests` are the regression net.

### CLI host (`Cli/CliCommand.cs`)

1. Build the same host with `AddEfCoreServices(includeHostedServices: false)`, then call
   `Build()`. **Do not** call `StartAsync`.
   - **Hosted services never run.** There is no reload watcher, no background update check and
     no OpenTelemetry export. The meter provider is started by a hosted service, so
     `Telemetry:Enabled` has no effect in CLI mode; this is documented.
   - **Force `QueryExecution:Mode` away from `Pooled`.** Use `Auto` or `OutOfProcess` instead.
     A pool would spawn a persistent worker only to tear it down at exit.
   - **The default `Auto` mode already uses the one-shot `OutOfProcessRoslynQueryExecutor`.**
     It kills the process tree on cancellation, and the QueryHost exits when its stdin closes,
     so orphaned processes are unlikely.
   - **Dispose the host with `await using` / `DisposeAsync()`.** `QueryHostPool` is a resolved
     singleton that implements only `IAsyncDisposable`, so a synchronous `Dispose()` throws.
2. Map CLI globals onto configuration *before* `Build()`, so the existing preload path loads
   the target. No new loading code is needed.
   - **Build the host from mapped configuration only, never from the raw `args`.** The
     command-line configuration provider would otherwise turn operation flags such as
     `--query …` into configuration keys.
   - **Fail fast with exit 1 if an explicit `--assembly` does not load.** The preload otherwise
     only logs a warning, and every later error then tells the caller to "call load_assembly".

   | Global flag | Maps to |
   | --- | --- |
   | `--assembly <dll>` | `TargetAssemblyPath` |
   | `--workspace <dir>` | `WorkspacePath`, and the default `workspacePath` of `list_assembly_candidates`; it defaults to the current directory when neither flag is given. Preload loads the first ranked candidate that really contains a DbContext (at most 10 are tried), and warns instead of loading an arbitrary assembly when none does |
   | `--target <name>` | injected as the `targetName` input, **only** for operations that declare it (the adapter rejects unknown flags) |
   | `--connection <name>` | injected as the `connectionName` input, only for operations that declare it |
   | `--config Key=Value` | any configuration key, for example `ToolOutput:Format=json` |

   `DOTNETEFCOREMCP_*` environment variables and user-secrets keep working. Output format is
   the existing `ToolOutput:Format` setting rather than a new `--format` flag, because the
   adapter already reserves `--output` and a second output switch would be confusing.
3. Build the catalog with
   `new OperationCatalogBuilder().AddMcpServerTools(typeof(EfCoreMcpTools).Assembly, t => host.Services.GetRequiredService(t), include: m => !excluded.Contains(name), mapSafety: false)`.
   - Register `EfCoreMcpTools` as a singleton, in CLI mode only. DI picks the public
     constructor; the internal test constructor is ignored.
   - The `include` predicate and the opt-out of `Destructive` → `Dangerous` mapping both depend
     on upstream A4.
   - Binding works for every tool signature: `IReadOnlyList<string>`, `QueryPagination`,
     `Dictionary<string,JsonElement>` and `Task<string>` are all handled. `object?[]` elements
     bind as `JsonElement`; C4 must verify this matches what the MCP SDK passes to
     `run_sql_query`.
4. Run `OperationCommandLineAdapter` with the A3 pass-through, so the already-formatted tool
   string reaches stdout byte-for-byte, untruncated and unquoted. Errors go to stderr.

   | Exit code | Meaning |
   | --- | --- |
   | 0 | success |
   | 1 | operation error: the `McpException` message, already redacted; multi-line is preserved if A3 makes flattening optional |
   | 2 | usage error |
   | 130 | cancellation; the sync `Execute` path currently wraps `OperationCanceledException` as an unexpected error, which is minor and acceptable |
5. Logging: keep the console logger on stderr, with the default level raised to `Warning` in
   CLI mode, so stdout carries only the result.
6. Ctrl+C maps to a `CancellationToken` passed to the adapter.
7. `cli generate-skill [--out-dir dir]` and `cli check-skill` wrap `SkillGeneratorCommand` to
   produce a CLI skill (`SKILL.md` + references). The skill is packed like the existing
   `skills/` folder and offered by `onboard` (phase 3).
8. CLI-aware error wording. Messages that tell the caller to use `load_assembly`,
   `swap_connection` or `reload_connections` get a CLI variant that names `--assembly` or
   `--connection`. Do this through a small `IToolSurfaceHints` service, registered differently
   per mode. The MCP text stays byte-identical.

### Connections in CLI mode

There is no `swap_connection`. The registry's default, the first non-production connection,
is used unless `--connection` is passed. If only production connections are configured there
is no default, and `--connection` is required. This is documented in the CLI usage.

### Cursor pagination across processes

`run_query` cursors are HMAC-signed with a key that is random per process
(`QueryExecutionOptions.CursorSigningKey`). The payload binds only the context type, the
entity and the ordering shape, with no other process-local state. `get_schema` cursors are
unsigned offsets and already work across processes.

In CLI mode, persist a key beside the update cache at `~/.dotnet-efcore-mcp/cli-cursor.key`
when none is configured:

- **Detect "configured" from the raw configuration value**
  (`configuration["QueryExecution:CursorSigningKey"]`), not from the options object. The
  options object is always non-empty because of its random default.
- **Create the file atomically:** `FileMode.CreateNew`, falling back to a read if it already
  exists, so concurrent first runs agree on one key.
- **Restrict it to the current user,** with an ACL on Windows and mode 0600 on Unix.

The MCP mode is untouched and keeps its random per-process key.

### Safety

The gates are unchanged: `RawSqlExecution:Enabled`, `EntityMutations:Enabled`,
`Migrations:Enabled`, `Onboarding:AllowInstall` and connection access modes all live in the
tool implementations, and both surfaces share them.

In phase 1, import every tool as `Safe` using the A4 `mapSafety: false` option, and rely on
those server gates. Two alternatives were rejected:

- **Annotating `EfCoreMcpTools` with `ReadOnly`/`Destructive`.** This would change MCP
  `tools/list` output, which breaks the no-regression rule. It would be a deliberate, separate
  change.
- **Registering a permissive confirmation policy.** It would hide intent.

Adding `--confirm` for mutation and raw-SQL operations is the optional phase-3 item E3.

### Hooks

Today, shell calls to the CLI are classified Unrelated (`ToolCallClassifier.cs` ~117-120,
~155-167). That is safe, but the CLI gets no reminders.

- Extend `ToolCallClassifier` to also recognise shell commands that invoke
  `dotnet-efcore-mcp cli <query-operation>`, or the phase-3 command name.
- Give `HookReminder` (~32-33), which names `preview_query_sql`/`run_query`, a CLI-phrased
  variant.
- Add unit cases. Existing MCP-name cases stay unchanged.

## Workstreams

### A. `dotnet-agent-surface` changes (upstream issues)

The upstream work is tracked in [#88](https://github.com/sommmen/dotnet-agent-surface/issues/88),
[#89](https://github.com/sommmen/dotnet-agent-surface/issues/89),
[#90](https://github.com/sommmen/dotnet-agent-surface/issues/90),
[#91](https://github.com/sommmen/dotnet-agent-surface/issues/91) and
[#92](https://github.com/sommmen/dotnet-agent-surface/issues/92). The table below maps this
plan's earlier A-items to those issues, so the C, F and G items can cite them.

| # | Need | Upstream |
| --- | --- | --- |
| A1 | `[Description]` on parameters | #90 R2 |
| A2 | Collection/dictionary schemas, `default`, nullability | #90 R3 |
| A3 | String pass-through; no quoting, truncation or error flattening | #92 C2 (CLI), #90 R4 (MCP) |
| A4 | Importer safety mapping, `include` predicate, annotations | #88, #90 R6 |
| A5 | Flag ergonomics: string-typed parsing, `--x=y`, switches, kebab-case | #92 C3 |
| A6 | Global options injected per operation; no crash on pre-invoke exceptions | #92 C4, #88 |
| A7 / A7b | Dependency alignment; CommandLine as a library package | #89 |
| A8 | Publish to nuget.org | #89 |
| A9 | `IHost` dual-surface helper | #92 C1 |
| A10 | `IMcpServerBuilder` integration, renderers, error mapper, invocation filters | #90 R1, R4, R5, R8 |
| A11 | Session state across CLI calls | #91 |
### B. Composition refactor (this repo, no behaviour change)

- B1: extract `ServerComposition` (registrations + preload) from `Program.cs`. Preserve
  these invariants:
  - configuration order: user-secrets (after `EnsureDirectoryExists`), then environment
    variables, then command-line arguments
  - `ToolOutput:Format` validation still runs before `Build()`
  - hosted-service registration (`AssemblyReloadWatcher`, `QueryHostPool`,
    `UpdateNotificationService`) sits behind an `includeHostedServices` parameter
  - the `ITransport` override is still registered **after** `WithStdioServerTransport()`
  - the startup `ConnectionRegistry.ConfigurationError` log still happens
  - the preload order stays `TargetAssemblyPath`/`WorkspacePath`, then `AssemblyLoader:Targets`
- B2: run the full suite (`dotnet test dotnet-efcore-mcp.slnx`). Diff `tools/list` output from
  a real stdio session before and after (scripted `initialize` + `tools/list` against the built
  DLL). It must be identical. Add this as a test while doing it: there is currently no
  end-to-end test of `tools/list`.

### C. CLI mode, phase 1 (this repo)

- C1: add `DotNetAgentSurface.Core`, `.Mcp` and `.CommandLine` to `Directory.Packages.props`
  and the server csproj.
- C2: `Cli/CliCommand.cs` and the `cli` verb dispatch in `Program.cs`, covering globals,
  catalog, adapter, exit codes, cancellation and logging.
- C3: persist the cursor key for CLI mode.
- C4: tests (new `Cli/` folder; existing tests untouched):
  - in-process `CliCommand.RunAsync(args, stdout, stderr)` against `SampleApp`/SQLite:
    `list_contexts`, `get_schema`, `run_query`, `preview_query_sql`, and `run_query`
    pagination across two separate `RunAsync` calls with separately built hosts
  - a result longer than 1,000 characters is not truncated and not JSON-quoted
  - unknown operation / flag → exit 2; gated tool disabled → exit 1 with the same message the
    MCP tool returns (exact match once A3 makes flattening optional)
  - a missing or unloadable `--assembly` → exit 1, and the message names `--assembly`, not
    `load_assembly`
  - stdout contains only the result, with no log lines
  - `--help` lists all operations except the excluded state-only ones
  - `run_sql_query` `parameters` binding matches the MCP path
  - parity: for a fixed input, the CLI stdout equals the `EfCoreMcpTools` method's return
    string
  - every imported operation has a description and a description for each parameter
  - one smoke test that runs the built DLL as a process
    (`dotnet Server.dll cli list_contexts --assembly ...`). Confirm that `QueryHostLocator`
    finds the bundled QueryHost under CI's Release `--no-build` run.
- C5: the hooks classifier recognises CLI invocations, and `HookReminder` has CLI wording
  (+ tests).
- C6: Renovate. Add a `DotNetAgentSurface.*` group with an exact pin and no automerge. Check
  the existing ModelContextProtocol automerge rule still makes sense alongside it.

### D. Distribution & docs, phase 2

- D1: README gets a "Choose MCP or CLI" section with install and usage for both. It covers
  `dnx DotnetEfCoreMcp.Server -- cli ...`, global/local tool, and npm `npx ... cli ...`.
- D2: a section on CLI mode in [mcp-tools.md](./mcp-tools.md). Link this doc from
  `DEVELOPMENT.md`. Update [agent-onboarding.md](./agent-onboarding.md),
  [agent-hooks.md](./agent-hooks.md), [vscode-integration.md](./vscode-integration.md),
  [update-notification.md](./update-notification.md), `npm/README.md`, and the
  `skills/dotnet-efcore-mcp-feedback` wording ("MCP server" → "MCP server or CLI").
- D3: in CLI mode, no background update check runs on each call. `UpdateNotificationService.Result`
  stays null, so `onboard` reports `checkedForUpdates=false`. The existing `version` verb
  remains the way to check for updates, and the docs say so.

### E. Optional, phase 3

- E1: the `DotnetEfCore.Cli` thin tool package. The id is free on nuget.org; claim it on
  GitHub Packages early. The command name avoids confusion with Microsoft's `dotnet-ef`, for
  example `efcore-cli`. It runs in CLI mode by default and has no `McpServer` package type.
  It needs:
  - its own exclusion of `.mcp/server.json`
  - a deliberate decision on bundling QueryHost and skills
  - `InstallMethodDetector` taught the second package id and its upgrade commands (currently
    hard-coded to `dotnetefcoremcp.server`)
  - packing in `publish.yml` with explicit, non-overlapping id globs (today
    `DotnetEfCoreMcp.Server.*.nupkg` with `-First 1` is fragile)
  - an npm `bin` entry
- E2: `onboard` installs the generated CLI skill (`what=cli-skill`), following the existing
  `AllowInstall` opt-in.
- E3: `--confirm` for mutation and raw-SQL operations in CLI mode, via an agent-surface
  confirmation policy.

### F. MCP host onto agent-surface (after #90)

- F1: replace `.WithTools<EfCoreMcpTools>()` with the #90 R1 builder integration. Keep
  `AddMcpServer(...)`, the server instructions, `WithStdioServerTransport()` and the
  `FlushOnDisconnectStdioTransport` `ITransport` override exactly as they are. Register a
  pass-through result renderer and an error mapper that keeps `McpException` messages verbatim.
- F2: the gate is the B2 `tools/list` diff test, which must be identical apart from deliberate
  annotation additions. The full existing suite must also pass unchanged. If either fails, the
  gap goes back upstream as a #90 follow-up rather than a local workaround.
- F3: once #90 R8 lands, move the telemetry and redaction wrappers (`Execute`/`ExecuteAsync`)
  into an `IOperationInvocationFilter` shared by both surfaces.
- F4 (separate, deliberate change): annotate tools with `ReadOnly`/`Destructive`/`Idempotent`.
  This changes `tools/list` intentionally, so it gets its own commit and its own test update.

### G. Equal MCP/CLI flows via session state (after #91)

A quick check of whether the MCP "shape" can survive one-process-per-call. All in-process
state that flows depend on is *intent* that can be serialised:

| State | Where | Persistable intent |
| --- | --- | --- |
| Loaded targets + default target | `AssemblyLoaderService._targets`, `_defaultTargetName` | `{ targetName → assemblyPath }` and `defaultTarget`. Rehydrate with `Load(...)` and `SelectDefault(...)`. |
| Active connection | `ConnectionRegistry._activeName` | `activeConnection`. Rehydrate with `SetActive(...)`. Connection strings stay in configuration/user-secrets. |
| Cursor HMAC key | `QueryExecutionOptions.CursorSigningKey` | `cursorKey`, which replaces the C3 bespoke key file. |
| Schema cache, compiled queries, QueryHost pool | caches | Not needed. These are performance-only and rebuilt on demand. |

So the flows can be kept identical **without a daemon**:

```
cli load_assembly --assemblyPath X
cli list_contexts
cli run_query ...
```

This works once #91 lands:

- G1: write the session keys from `load_assembly`, `select_target` and `swap_connection`, and
  read `cursorKey` from the session.
- G2: add an `IAgentSessionInitializer` that rehydrates targets and the active connection at
  CLI start.
- G3: re-include those tools in the CLI (they were excluded in C2) and drop the CLI-specific
  error wording for them. Per-call `--assembly`/`--connection` globals stay as overrides.
- G4: tests that replay the MCP flow through separate CLI invocations and get identical
  results.

The remaining difference is **latency**: each call still reloads the assembly and warms
Roslyn/EF Core. A background daemon (CLI as a thin client to a warm server process) would
remove that. It is explicitly **future work**: it needs IPC, lifecycle, auth and stale-build
handling, and it can reuse #91's state model when it comes.

## Order and dependencies

```
#88 #89 + #92 (C1–C4) ──► C1 ──► C2 ──► C3, C4, C5, C6 ──► D ──► E      (phase 1: CLI)
B1 ──► B2 ──────────────────────► C2
#90 ──► F1 ──► F2 ──► F3 ──► F4                                         (phase 2: MCP on agent-surface)
#91 ──► G1 ──► G2 ──► G3 ──► G4                                         (phase 3: equal flows)
```

B can land immediately and independently. C needs #88, #89 and #92. For local development
before publication, use a `ProjectReference` to `C:\Repos\dotnet-agent-surface` or a local
feed, but never commit it. F and G can run in parallel once their upstream issues land.

## Risks

- **Per-call startup cost.** Each CLI call builds the host, loads the target assembly and
  initialises Roslyn/EF Core. With no `--assembly` or session, it also scans the workspace and
  picks `discovered[0]`, which is slow in monorepos and may pick the wrong project. Measure
  `run_query` wall time in C4. The structural fix is the future daemon.
- **Upstream API churn.** agent-surface is `0.1-preview`. Pin exact versions, and let Renovate
  handle bumps without automerge (C6).
- **Schema/description drift between surfaces.** This goes away after F, when one catalog feeds
  both surfaces. Until then, C4 asserts that every imported operation and parameter has a
  description.
- **Behavioural drift during F.** Mitigated by the `tools/list` diff gate (B2/F2) and the
  unchanged existing suite.

## Decisions

- **agent-surface is the base for both surfaces.** Gaps are fixed upstream (#88–#92), not
  worked around here. The MCP host moves over only once the parity gate passes, so there is
  no regression window.
- **Make phase 1 a `cli` verb in the existing package.** A separate CLI-only package is E1,
  because it adds packaging and publish-workflow risk without adding capability.
- **Keep the MCP flow shape in the CLI through persisted session intent (#91), not a daemon.**
  A daemon is future work.