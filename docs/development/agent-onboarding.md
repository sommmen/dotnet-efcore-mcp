# Agent onboarding

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: [`src/DotnetEfCoreMcp.Server/Onboarding`](../../src/DotnetEfCoreMcp.Server/Onboarding) ·
Tests: [`tests/DotnetEfCoreMcp.Server.Tests/Onboarding`](../../tests/DotnetEfCoreMcp.Server.Tests/Onboarding) ·
User-facing setup: [README "Onboarding: hooks and skills"](../../README.md#onboarding-hooks-and-skills)

Related: [Agent hooks](./agent-hooks.md) · [Agent skills](./agent-skills.md)

## The problem

The [hooks](./agent-hooks.md) and the [feedback skill](./agent-skills.md) both existed before this
module and both worked. Almost nobody used them, for a reason that had nothing to do with either
feature: **you could not find out they existed from inside a session.**

The discovery path for each was "read the right section of the README." Hooks were installed by one
command (`hooks install`), skills by a different mechanism (a PowerShell/bash script requiring a
clone of this repository), documented in two separate sections. Someone who installed the server
with `dnx` and hit a rough edge in their own codebase had neither the clone nor any reason to
suspect a feedback skill was available.

That is the same gap the hooks themselves address, one level up. Hooks exist because
`ServerInstructions` only reach an agent that loads the server; onboarding exists because the hooks
and skills only reach a user who reads the README.

## Prior art: Serena

[Serena](https://oraios.github.io/serena/02-usage/030_clients.html) hit this and solved it with two
mechanisms worth separating:

- **A `SessionStart` hook** (`serena-hooks activate`) that prompts the agent to activate the project
  and read the instructions at session start, alongside the `PreToolUse` reminder hook.
- **Tools that carry the onboarding itself** — `onboarding` and `check_onboarding_performed` return
  instruction text, and `initial_instructions` exists specifically "for clients that do not read the
  initial instructions when the MCP server is connected."

The takeaway is the split: a lifecycle hook makes the server *visible*, and a tool makes the setup
*actionable from inside the conversation* rather than only from a shell the user has to find.

What this server does **not** copy is Serena's project-memory onboarding (exploring the repo and
writing `.serena/memories/*`). Serena needs durable per-project knowledge because it is a
general-purpose code navigator. Here the equivalent knowledge — entities, keys, navigations, column
types — is already derivable on demand from the compiled EF Core model, so persisting a summary of
it would only add something to go stale.

## What onboarding covers

| Component | Delivery | Discoverability problem it solves |
|---|---|---|
| Hooks | `SessionStart` + `PostToolUse` config per client | Agent does not know the server exists, or forgets mid-session |
| Skills | Folder copied into a directory the client scans | User does not know the feedback skill exists at all |

## The three entry points

All three resolve through [`OnboardingInspector`](../../src/DotnetEfCoreMcp.Server/Onboarding/OnboardingStatus.cs),
so they cannot disagree about what is installed.

1. **`onboard` CLI verb** — [`OnboardCommand`](../../src/DotnetEfCoreMcp.Server/Onboarding/OnboardCommand.cs).
   One command for hooks and skills together. A bare `onboard` prints status rather than a usage
   block, because someone typing the verb to find out what it does should get the answer, and status
   changes nothing.
2. **`onboard` MCP tool** — in [`EfCoreMcpTools`](../../src/DotnetEfCoreMcp.Server/Tools/EfCoreMcpTools.cs).
   Read-only by default (`apply: false`); writing additionally requires
   `Onboarding:AllowInstall` (see [Why installing over MCP is gated](#why-installing-over-mcp-is-gated)).
   `ServerInstructions` points at it.
3. **`SessionStart` hook** — [`SessionActivation`](../../src/DotnetEfCoreMcp.Server/Hooks/SessionActivation.cs).
   Announces the server's tools, and names what is missing *only when something is missing*, so a
   fully set-up session does not pay context for a setup note on every start.

The tool matters more than it looks. The user who most needs the feedback skill is mid-task, deep in
a private codebase, already annoyed — exactly the person who will not go read a README. Being able
to say "set that up for me" in the conversation is the difference between the feature existing and
the feature being used.

## Why installing over MCP is gated

Reporting status over MCP is always allowed — it only reads, and it is the thing that makes these
features discoverable from inside a session at all. *Installing* over MCP is off unless an operator
sets `Onboarding:AllowInstall` ([`OnboardingOptions`](../../src/DotnetEfCoreMcp.Server/Onboarding/OnboardingOptions.cs)),
matching `RawSqlExecution:Enabled` and `EntityMutations:Enabled`.

The asymmetry with the CLI verb is deliberate. `onboard install` from a shell is a human deciding
to write files. The MCP tool is an *agent* deciding, and what it writes is not inert: an installed
hook adds a command the agent's CLI executes on every subsequent tool call. That is a broader grant
than inserting a row, which this server already gates.

The tool description does ask the agent to confirm with the user first, and it should. But a
description is a prompt, and prompts are exactly what a malicious instruction smuggled through
query results or a file can talk its way past. The gate is the boundary; the description is a
courtesy.

Refusing stays actionable rather than becoming a dead end: the error names the setting and includes
the exact CLI command, so the agent can hand it to the user, who runs it themselves. That keeps the
default both safe and useful, which is why the gate costs almost no discoverability.

## Install scope

Both scopes are first-class; neither is a fallback for the other.

| | `InstallScope.Workspace` (default) | `InstallScope.User` |
|---|---|---|
| Claude Code | `.claude/settings.json` | `~/.claude/settings.json` |
| Copilot CLI | `.github/hooks/dotnet-efcore-mcp.json` | `~/.copilot/hooks/dotnet-efcore-mcp.json` |
| Codex CLI | `.codex/hooks.json` | `~/.codex/hooks.json` |
| Skills | `.github/skills/` | `~/.agents/skills/` |
| Good for | A team sharing one setup via a commit | One person across many repositories |

Design decisions:

- **Workspace is the default** because it is the reviewable choice. The config lands in the
  repository where a human sees it in a diff, and it cannot silently change behavior in unrelated
  projects the way a user-level install can.
- **One skills directory per scope, not one per client.** `~/.agents/skills` and `.github/skills`
  are both scanned by Copilot CLI, Claude Code, and Codex, so installing once avoids three copies of
  the same markdown drifting apart.
- **Copilot's user path is not its workspace path.** Every other client uses the same relative
  layout in both scopes; Copilot reads `.github/hooks/` in a repo but `~/.copilot/hooks/`
  (or `$COPILOT_HOME/hooks/`) per user, so `COPILOT_HOME` is honored.
- **A user-scoped `SessionStart` hook must earn its noise.** It fires in every session on the
  machine, including repositories with no EF Core in them, so it stays silent unless
  [`EfCoreWorkspaceProbe`](../../src/DotnetEfCoreMcp.Server/Hooks/EfCoreWorkspaceProbe.cs) finds an
  EF Core reference. A workspace-scoped hook always speaks, because installing it into that
  repository was already the opt-in. Announcing EF Core tooling in a front-end repo is the kind of
  noise that trains agents — and users — to ignore injected context.

### The EF Core probe

The probe reads *project* files (`*.csproj`, `Directory.Packages.props`,
`Directory.Build.props`, `packages.lock.json`) rather than compiled output, because the hook runs
before the agent has done anything and there may be no build yet. It is bounded on purpose — depth
4, 400 files, a 64 KB read per file — since it runs inside the client's hook timeout on every
session start. `bin`/`obj`/`node_modules` and dot-directories are skipped: they hold thousands of
files, and a stale EF Core reference in build output should not keep announcing in a project that
has since dropped the dependency.

## Packaging skills into the tool

`onboard install` has to work for someone who ran `dnx DotnetEfCoreMcp.Server` and has no clone —
that is the whole point. So the skill folders are packed into the NuGet tool under
`tools/<tfm>/any/skills/` (see the `IncludeSkillsInPackage` target in
[`DotnetEfCoreMcp.Server.csproj`](../../src/DotnetEfCoreMcp.Server/DotnetEfCoreMcp.Server.csproj)),
mirroring how the query host is bundled.

[`SkillCatalog`](../../src/DotnetEfCoreMcp.Server/Onboarding/SkillCatalog.cs) resolves that folder
next to the server binaries, then falls back to walking up to the repository's own `skills/` during
local development — so an F5 build and a packaged run install the same skills, and there is no copy
step on every inner-loop build.

`evals/` is excluded from the package: it is skill-development scaffolding, and
[`SkillInstaller`](../../src/DotnetEfCoreMcp.Server/Onboarding/SkillInstaller.cs) excludes it again
on copy so a source-tree install matches a packaged one.

## Why there are still two skill installers

[`skills/install-skills.ps1`](../../skills/install-skills.ps1) was not replaced, because it does
something `onboard` cannot: `-Link` installs a junction/symlink so edits to the repo are picked up
without reinstalling, which is what you want while *writing* a skill.

The two share the `.dotnet-efcore-mcp-skill-install` marker file, so either can recognize and manage
an install the other created rather than refusing to touch it.

## Never clobber

An install destination that is not marked as ours is reported and skipped, never replaced.
Overwriting a hand-written skill that happens to share a name is data loss, not an upgrade. The same
principle already governs hook installs, which merge into existing config and preserve unrelated
hooks and settings.

Freshness is compared by **content**, not timestamps: a packaged install's files carry the package's
extraction time rather than the skill's, so a timestamp check would report every install as stale.
Skills are a handful of small markdown files, so hashing the copied set is cheap and exact.

## Failure contract

Everything a hook invokes is fail-open and always exits 0, matching
[the hooks' existing contract](./agent-hooks.md): `hooks activate` swallows every exception and falls
back to `{}`. A session-start hook that throws would break the session it exists to help, and the
context it injects is advisory.

`SessionActivation` emits both output envelopes — a top-level `additionalContext` for Copilot CLI and
`hookSpecificOutput.additionalContext` for Claude Code and Codex — so one response shape is correct
on every client.

## Tests

| File | Covers |
|---|---|
| [`HookInstallScopeTests`](../../tests/DotnetEfCoreMcp.Server.Tests/Onboarding/HookInstallScopeTests.cs) | User-scope paths, the `SessionStart` entry, scope passed to the hook command, unrelated user settings preserved |
| [`SkillInstallerTests`](../../tests/DotnetEfCoreMcp.Server.Tests/Onboarding/SkillInstallerTests.cs) | Copy/update/remove, `evals/` exclusion, refusal to replace an unmanaged directory |
| [`OnboardCommandTests`](../../tests/DotnetEfCoreMcp.Server.Tests/Onboarding/OnboardCommandTests.cs) | CLI verb: default-to-status, scope and component selection, uninstall |
| [`SessionActivationTests`](../../tests/DotnetEfCoreMcp.Server.Tests/Hooks/SessionActivationTests.cs) | Scope-dependent gating, both output envelopes, fail-open on bad payloads |
| [`EfCoreWorkspaceProbeTests`](../../tests/DotnetEfCoreMcp.Server.Tests/Hooks/EfCoreWorkspaceProbeTests.cs) | Detection via `PackageReference` and CPM, build output ignored |
| [`EfCoreMcpToolsOnboardTests`](../../tests/DotnetEfCoreMcp.Server.Tests/Tools/EfCoreMcpToolsOnboardTests.cs) | MCP tool: read-only default, the `AllowInstall` gate (including the unconfigured default), apply paths, argument validation |

Tests must never read or write the real home directory, or their result depends on what the
developer happens to have installed. `DOTNETEFCOREMCP_HOMEDIR`
([`UserDirectory`](../../src/DotnetEfCoreMcp.Server/Onboarding/UserDirectory.cs)) redirects
user-scoped paths into the test's own temp directory, and it deliberately takes precedence over
`COPILOT_HOME` — a developer machine with `COPILOT_HOME` pointing at their real profile would
otherwise let a test write into it.
