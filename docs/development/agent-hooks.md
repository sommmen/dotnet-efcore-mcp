# Agent hooks (LINQ validation reminders)

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: [`src/DotnetEfCoreMcp.Server/Hooks`](../../src/DotnetEfCoreMcp.Server/Hooks) ·
Tests: [`tests/DotnetEfCoreMcp.Server.Tests/Hooks`](../../tests/DotnetEfCoreMcp.Server.Tests/Hooks)

See also the [README "Install agent hooks"](../../README.md#install-agent-hooks) section for
user-facing setup instructions.

## Why hooks exist

The server sets `ServerInstructions` (in [`Program.cs`](../../src/DotnetEfCoreMcp.Server/Program.cs))
telling the agent to validate LINQ with this server. That text only reaches the agent when the
client actually loads the MCP server, and with dynamic tool loading, clients frequently load a
server late or not at all. The instructions are also easy to forget over a long session — a
failure mode commonly called agent drift.

The cost of that drift is specific and recurring: an agent writes LINQ that compiles and reads
correctly but throws at runtime because EF Core cannot translate it to SQL. The query looks fine
until it is executed against the real provider.

Hooks close that gap by re-delivering the guidance at the moment it matters — right after the
agent writes a LINQ query — instead of once at session start. This mirrors the approach
[Serena](https://oraios.github.io/serena/02-usage/030_clients.html) adopted for the same problem.

## Client support

All three supported clients expose lifecycle hooks with a near-identical schema, so one
implementation serves all of them.

| | Claude Code | Copilot CLI | Codex CLI |
|---|---|---|---|
| Repo-local file | `.claude/settings.json` | `.github/hooks/dotnet-efcore-mcp.json` | `.codex/hooks.json` |
| Handler shape | matcher group wrapping handlers | flat handler list | matcher group wrapping handlers |
| Invocation | `command` (shell) | `exec` + `args` (no shell) | `command` (shell) |
| Context field | `hookSpecificOutput.additionalContext` | `additionalContext` | `hookSpecificOutput.additionalContext` |
| Timeout field | `timeout` (seconds) | `timeoutSec` (seconds) | `timeout` (seconds) |

Reference documentation: [Claude Code hooks](https://code.claude.com/docs/en/hooks),
[Copilot hooks](https://docs.github.com/en/copilot/reference/hooks-reference),
[Codex hooks](https://developers.openai.com/codex/hooks).

## Design decisions

- **`PostToolUse`, not `PreToolUse`.** Serena uses `PreToolUse` with a deny because it wants to
  *stop* a redundant search. The goal here is the opposite: let the write succeed, then nudge.
  Decisively, Copilot CLI's `preToolUse` command hooks are **fail-closed** — a crash, a non-zero
  exit, or any error denies the tool call. A reminder must never be able to block a user's file
  edit, and `PostToolUse` is fail-open on every client.
- **One PascalCase dialect everywhere.** Claude Code and Codex use PascalCase event names natively,
  and Copilot CLI explicitly supports them with Claude matcher semantics. Installing one dialect
  keeps a single matcher vocabulary and a single payload parser.
- **Both output envelopes are always emitted.** The response carries the reminder in a top-level
  `additionalContext` (Copilot) *and* in `hookSpecificOutput.additionalContext` (Claude, Codex).
  Each client ignores fields it does not recognize, so one response shape is correct everywhere.
- **Always exit 0.** `hooks remind` swallows every exception and falls back to `{}`. The reminder
  is advisory, so it must never fail the tool call that triggered it.
- **Pin the package version at install time.** The installed command resolves
  `DotnetEfCoreMcp.Server@<version>` so a running session cannot pick up a different reminder
  implementation mid-flight. The version comes from a `NuGetPackageVersion` assembly-metadata
  attribute embedded by the `.csproj`, because `AssemblyInformationalVersion` does not reproduce
  Nerdbank.GitVersioning's package suffix (`0.1.32-preview.g842ad5eb9d`) verbatim and an
  approximate version would not resolve. When that metadata is absent (a local build), the command
  is written unpinned with `--prerelease`, which is required while releases are preview-only.

## Detection heuristics

Precision matters more than recall. A reminder that fires on every `List<T>.Where()` would train
both the agent and the user to ignore it, so
[`ToolCallClassifier`](../../src/DotnetEfCoreMcp.Server/Hooks/ToolCallClassifier.cs) requires
**two** independent signals before treating a write as EF Core LINQ:

1. A **LINQ query operator** — `.Where(`, `.Select(`, `.Include(`, `from x in …`, and similar.
2. An **EF Core signal** — `DbSet<`, `IQueryable`, `.Include(`, an `…Async` materializer, or a
   query rooted on a context-like member such as `_context.Orders`.

In-memory LINQ over a `List<T>` satisfies only the first, so it does not trigger a reminder.
Writes are further ignored when they are not C#, or when the path is a migration, a designer file,
or build output.

Shell calls are matched separately: `dotnet run`/`test`/`build`/`ef` are the commands where an
untranslatable query actually surfaces.

## Reminder rate limiting

Each hook invocation is a separate short-lived process, so the counters live on disk, keyed by the
client's session id (see
[`HookSessionStore`](../../src/DotnetEfCoreMcp.Server/Hooks/HookSessionStore.cs)). State is stored
under `%LOCALAPPDATA%/dotnet-efcore-mcp/hook-data/` by default, overridable with
`DOTNETEFCOREMCP_HOOKDATADIR`, and deleted by the `SessionEnd` cleanup hook.

- A LINQ write reminds immediately (threshold 1).
- Entity-source reads remind only after 3 reads, keeping the secondary nudge quiet.
- A 10-minute per-session cooldown prevents nagging during a long editing burst.
- **Calling any dotnet-efcore-mcp tool resets the counters and clears the cooldown** — the agent
  did the thing the reminder exists to produce, so the next query starts from a clean slate.

## Implementation checklist

- [x] `hooks` CLI verb dispatched before the host is built, leaving MCP server startup unchanged
- [x] `hooks install` / `uninstall` / `status`, with `--client`, `--path`, and `--force`
- [x] Payload parsing across all three client dialects, including tool input sent as a JSON string
- [x] EF Core LINQ detection requiring both a query operator and an EF signal
- [x] Entity-read detection for the secondary schema-discovery nudge
- [x] Per-session counters with cooldown, reset by dotnet-efcore-mcp tool use
- [x] Dual-envelope `additionalContext` response, fail-open on every error path
- [x] Installers that merge with existing config and preserve unrelated hooks
- [x] Tests covering parsing, detection false positives, rate limiting, and installer merging

## Possible follow-ups

- User-level (global) installation, in addition to today's repo-local files.
- A `SessionStart` hook that prompts the agent to load the server up front, as Serena's
  `activate` hook does.
- Feeding the reminder the specific entity names found in the query, so it can point at the exact
  `get_entity_schema` call to make.
