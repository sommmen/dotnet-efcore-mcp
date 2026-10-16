# Update notification

[← Back to Development Guide](../../DEVELOPMENT.md)

Status: **implemented.** Closes
[#98](https://github.com/sommmen/dotnet-efcore-mcp/issues/98).

Code: [`src/DotnetEfCoreMcp.Server/Updates`](../../src/DotnetEfCoreMcp.Server/Updates) ·
Tests: [`tests/DotnetEfCoreMcp.Server.Tests/Updates`](../../tests/DotnetEfCoreMcp.Server.Tests/Updates) ·
User-facing: [README "Updating"](../../README.md#updating)

Related: [Agent onboarding](./agent-onboarding.md) · [Agent hooks](./agent-hooks.md) ·
[Observability](./observability.md)

## The problem

[#98](https://github.com/sommmen/dotnet-efcore-mcp/issues/98): there is no update delivery path
for consumers. Someone who ran

```bash
dotnet tool install --global DotnetEfCoreMcp.Server --prerelease
```

in March is still running that build in September. Nothing in the product tells them a newer
version exists, and nothing updates them. While the project is shipping preview builds frequently,
that gap is the difference between a user hitting a bug that was fixed months ago and a user who is
current.

The gap is not uniform across install methods — and that turns out to drive the whole design:

| Install method | Documented in | Update behavior today |
|---|---|---|
| `dotnet tool install --global ... --prerelease` | [README](../../README.md#install-as-a-net-tool) | **Pinned forever.** No notice, no update. The gap in #98. |
| `dotnet tool install --local` (manifest) | — | **Pinned by design.** The manifest is a committed, reviewable pin; updating it is a deliberate act. |
| `dnx DotnetEfCoreMcp.Server --prerelease --yes` | [README](../../README.md#install-as-a-net-tool) | **Already self-updating** — measured, see [Does `dnx` refetch?](#does-dnx-refetch). |
| `npx -y dotnet-efcore-mcp` | [README](../../README.md#install-via-npm) | **Already self-updating.** The [npm shim](../../npm/bin/dotnet-efcore-mcp.js) resolves its own package version and runs `dotnet tool install`/`update --global` to make the .NET tool match before exec'ing it. |
| Hook-invoked `dnx` processes | [Agent hooks](./agent-hooks.md) | Version-pinned on purpose, so a session cannot pick up a different reminder implementation mid-flight. |

So this is really two problems, and only the first is open:

1. **Global `dotnet tool` installs have no signal and no update path.** This is #98.
2. **The user cannot tell which of the above they are on**, so even a correct notice has to be
   careful about which upgrade command it suggests.

## Research summary

Two research passes informed this plan: one on how CLI ecosystems do update notification generally,
one on .NET tool/NuGet mechanics and what MCP actually permits. The findings that changed the design
are below; the rest is cited inline in the relevant sections.

### What the ecosystem agrees on

| Tool | Notify or auto-update | Cadence | Cache location | Opt-out | Channel |
|---|---|---|---|---|---|
| [`update-notifier`](https://github.com/sindresorhus/update-notifier) | Notify | 24h default | `configstore` → XDG config / `%LOCALAPPDATA%` | `NO_UPDATE_NOTIFIER`, `--no-update-notifier`, auto-off in CI | stderr, deferred to `process.on('exit')` |
| npm CLI | Notify | 7d stable, **1d on a prerelease train** | `<npm-cache>/../_update-notifier-last-checked` (mtime as clock) | `npm config set update-notifier false`, CI | stdout banner after the command |
| [GitHub CLI](https://github.com/cli/cli) | Notify | 24h | `$XDG_STATE_HOME/gh/state.yml` (`%LOCALAPPDATA%` on Windows), YAML | `GH_NO_UPDATE_NOTIFIER`, `CODESPACES`, CI, non-TTY | **stderr, after the command completes** |
| Azure CLI | Notify; auto-upgrade **opt-in** (`auto-upgrade.enable`, defaults `False`) | ~11d | `~/.azure/versionCheck.json` | config; separate `auto-upgrade.prompt` knob | stderr |
| Vue CLI | Notify | 24h, stale-while-revalidate | `~/.vuerc` | — | stderr |
| Terraform (HashiCorp Checkpoint) | Notify | caller-set, 48h cache | caller-specified | `CHECKPOINT_DISABLE`, `CHECKPOINT_TIMEOUT` | stderr |
| Homebrew | Auto-updates its *index* | every invocation | — | `HOMEBREW_NO_AUTO_UPDATE` | stdout |
| .NET SDK | Notify, **on demand only** (`dotnet sdk check`) | n/a | n/a | n/a | stdout |
| rustup | `check-only` default; `auto-self-update` opt-in | per invocation | — | distro builds compile it out | stderr |

Five conclusions carry directly into this design:

1. **Notify-only is the default; auto-update is opt-in everywhere it exists at all.** Only Azure CLI
   and rustup auto-install, both strictly opt-in. Homebrew auto-updating its index as a side effect
   of unrelated commands is the single most-complained-about behavior in the surveyed set — strong
   evidence that *unsolicited network work as a side effect of an unrelated command* is the main
   source of backlash, independent of whether anything gets installed.
2. **24 hours is the de facto cadence**, and npm's prerelease branch (1 day instead of 7) is the
   precedent for shipping-fast channels.
3. **CI detection and TTY detection are two separate, both-required gates.** GitHub CLI's
   `ShouldCheckForUpdate` requires `!ci.IsCI() && IsTerminal(stdout) && IsTerminal(stderr)`, plus an
   explicit `CODESPACES` exclusion. `ci-info` deliberately honors `CI=false` as a force-off-CI escape
   hatch.
4. **Failure is always silent and tightly bounded.** HashiCorp Checkpoint uses a 3000 ms default
   timeout with the comment *"not critical enough to block on if checkpoint is broken/slow"*;
   `update-notifier`'s detached child self-terminates after 30 s to survive being offline.
5. **Prerelease handling is the highest-risk detail for this project**, because the tool ships
   preview-only today. Vue CLI's `getVersions.js` is the cleanest prior art:
   `const includePrerelease = !!semver.prerelease(local)` — if the installed version is a
   prerelease, resolve "latest" *including* prereleases. Getting this wrong has two concrete failure
   modes, both bad: a preview user is told "up to date" forever because no stable has shipped, or —
   since `2.0.0-beta.1` sorts *below* `2.0.0` — they get nagged to "upgrade" to something older than
   what they have.

GitHub CLI contributes a sixth idea worth stealing outright: its updater is **compiled out unless
the binary was built by the official Homebrew formula** (`//go:build updateable`). The maintainers
refuse to nag users whose install channel they cannot name, because they cannot know the right
upgrade command. That is exactly our situation across five install paths.

### What MCP permits

The stdio transport spec is unambiguous
([spec](https://modelcontextprotocol.io/specification/2025-06-18/basic/transports#stdio)):

> The server MUST NOT write anything to stdout that is not a valid MCP message.
> The server MAY write UTF-8 strings to stderr for logging purposes. Clients MAY capture, forward,
> or ignore this logging.

So the entire "print a boxen banner on exit" pattern that the npm ecosystem is built around is
**unavailable to us on stdout**, and only best-effort on stderr.

| Channel | Spec | Client reality | Suitable? |
|---|---|---|---|
| stdout text | [transports](https://modelcontextprotocol.io/specification/2025-06-18/basic/transports#stdio) | — | **Forbidden.** Corrupts the JSON-RPC stream. |
| stderr | same | Clients may capture, forward, or ignore | Diagnostic fallback only |
| `notifications/message` | [logging](https://modelcontextprotocol.io/specification/2025-06-18/server/utilities/logging) | Requires `logging` capability; spec mandates no UI. Not guaranteed to reach user *or* model | Best passive protocol option, still best-effort |
| `instructions` at `initialize` | [lifecycle](https://modelcontextprotocol.io/specification/2025-06-18/basic/lifecycle#initialization) | Sent once per connection, before the check could finish | No — would gate startup on a network call |
| Tool result `content` | [tools](https://modelcontextprotocol.io/specification/2025-06-18/server/tools) | Reliably reaches the model | **Only dependably model-visible carrier** |
| `_meta` | — | Client-specific by definition | No |
| `elicitation/create`, sampling | [elicitation](https://modelcontextprotocol.io/specification/2025-06-18/client/elicitation) | Requires client capability; framed for task input, human-deniable | No — wrong tool for "want to update?" |
| `serverInfo.version` | lifecycle | No client is required to display it | Report it, don't rely on it |

Ecosystem prior art search found **no MCP server that notifies about or performs self-updates** —
not in `modelcontextprotocol/servers`, not in the indexed popular servers. The registry
(`registry.modelcontextprotocol.io`) is discovery/metadata only; it defines no server→client "new
version" push. There is nothing to copy, which argues for protocol conservatism over invention.

One structural point: **the client owns the server process.** It chose the command, the version, and
the launch mechanism. A server that reinstalls itself underneath its own client is fighting the
thing that started it.

### .NET and NuGet mechanics

- **Version discovery**: the V3 service index
  (`https://api.nuget.org/v3/index.json`) → `PackageBaseAddress/3.0.0` → flat container
  `https://api.nuget.org/v3-flatcontainer/dotnetefcoremcp.server/index.json`. Returns an ordered
  version list *including prereleases and unlisted versions*, so filtering is on us. A plain
  `HttpClient` GET is sufficient; `NuGet.Protocol` is materially heavier and is only worth it if we
  decide to honor `NuGet.Config`, private feeds, source mapping, and credential providers.
- **Current version**: this repo already embeds the real NB.GV-computed `$(PackageVersion)` as
  assembly metadata via the `EmbedPackageVersionMetadata` target in
  [`DotnetEfCoreMcp.Server.csproj`](../../src/DotnetEfCoreMcp.Server/DotnetEfCoreMcp.Server.csproj),
  and [`HooksCommand.GetPackageVersion()`](../../src/DotnetEfCoreMcp.Server/Hooks/HooksCommand.cs)
  already reads it with an `AssemblyInformationalVersionAttribute` fallback. That logic is reusable
  as-is and is strictly better than parsing the informational version, which does not reproduce
  NB.GV's package-version suffix verbatim.
- **Comparison**: `NuGet.Versioning.NuGetVersion`, not `System.Version` — `System.Version` has no
  concept of prerelease ordering and will mis-sort `0.2.0-preview.g1` against `0.2.0`.
- **Install-method detection is a heuristic, not a fact.** There is no runtime API that says how the
  process was installed. `AppContext.BaseDirectory` landing under `~/.dotnet/tools/.store/` suggests
  a global install but gives false negatives for custom `--tool-path`, symlinks, and wrappers.
  `dnx` exposes no documented marker. The only *reliable* signal is one a wrapper sets deliberately
  — which the npm shim can do, since we own it.
- **Feed policy matters.** A hard-coded `api.nuget.org` query can advertise a version the user
  cannot install, because of source mapping, a corporate mirror, or an air gap — and in some
  environments the call itself is policy-prohibited.

## Decisions

### D1 — Notify only. Never auto-update.

The server will not install anything. Rationale, in order of weight:

1. The client owns the process; self-reinstalling underneath it is hostile and racy.
2. Ecosystem consensus is overwhelming (see table above); the two auto-updaters are both opt-in.
3. A stdio MCP server has no interactive terminal to confirm with, and elicitation is the wrong
   mechanism for it.
4. Updating a global .NET tool while a hook-invoked `dnx` process of the pinned version may be
   running concurrently is an unforced hazard.

The npm path is the exception that proves the rule: the shim updates the tool *before* the server
starts, from a wrapper the user explicitly invoked, with the version it chose. That is a launcher
doing launcher work, not a server updating itself.

### D2 — Only notify on install paths we can name.

Following GitHub CLI's `updaterEnabled` precedent. The notice includes the specific upgrade command
for the detected install method, and **stays silent when the method cannot be determined** rather
than guessing. Detection strategy:

| Signal | Method inferred | Suggested command |
|---|---|---|
| `DOTNETEFCOREMCP_INSTALL_METHOD=npm` (set by the shim) | npm | *(suppressed — the shim already updates)* |
| `AppContext.BaseDirectory` under a NuGet-package-cache layout (`.nuget/packages/dotnetefcoremcp.server/<version>/`) | `dnx` | *(suppressed — refetches per run; [measured](#does-dnx-refetch))* |
| `AppContext.BaseDirectory` under a `.store/dotnetefcoremcp.server/<version>/` layout rooted at the user's tools dir | global tool | `dotnet tool update --global DotnetEfCoreMcp.Server --prerelease` |
| `.config/dotnet-tools.json` found walking up from cwd, containing our package id | local manifest | `dotnet tool update --local DotnetEfCoreMcp.Server --prerelease` |
| anything else (custom `--tool-path`, F5, unknown) | unknown | *(silent)* |

Only the **global tool** path actually produces a notice. That is the narrow target of #98, and the
narrowness is the point: every other path either updates itself or cannot be named with confidence.

This requires a small, deliberate change to the [npm shim](../../npm/bin/dotnet-efcore-mcp.js):
set `DOTNETEFCOREMCP_INSTALL_METHOD=npm` in the child environment before exec. That is the one
high-reliability signal available, and it exists only because we own the wrapper.

`--prerelease` is included in the suggested command whenever the *installed* version is a
prerelease, which is every published build today.

### D3 — Prerelease-aware comparison, modeled on Vue CLI.

- Installed version is a prerelease → resolve latest **including** prereleases.
- Installed version is stable → resolve latest **stable only**; never advertise a prerelease to
  someone on a stable build.
- Only notify when `latest > installed` under `NuGetVersion` ordering. Never suggest a downgrade.
- Skip when the installed version is unparseable or absent (local F5 builds without packaging
  metadata — `GetPackageVersion()` already returns `null` there).

Cadence follows npm's precedent: **24 h on a prerelease channel, 7 d on stable.** The project will
keep publishing prereleases for the foreseeable future, so the 24 h prerelease branch is the default
path in practice; the stable branch exists so the channel logic is already correct when a stable
release first ships.

### D4 — Delivery: three surfaces, ranked by reliability.

1. **`onboard` tool result (primary).** The [`onboard`](./agent-onboarding.md) tool is already
   read-only, already advertised in `ServerInstructions`, and already exists to answer "what is
   available and what is set up." Version/update state belongs there: current version, install
   method, latest known version, and the exact upgrade command. This is the only dependably
   model-visible carrier, and it is *pull*, not push — no noise added to query results.
2. **One `notifications/message` at `notice` level, once per process (secondary).** Passive, spec-
   sanctioned, costs nothing when ignored. Emitted only after a successful check, gated by the same
   rules as everything else.
3. **One stderr line, once per process (fallback).** For humans tailing the server log. Mirrors
   GitHub CLI's channel choice.

**Explicitly rejected: appending a notice to every tool result.** It inflates model context, risks
the model treating an infrastructure nag as task content, and would corrupt the machine-consumed
TOON/JSON payloads produced by the
[tool result formatters](../../src/DotnetEfCoreMcp.Server/Tools). The research flagged this as the
main cost of the tool-result channel, and the `onboard` tool neutralizes it by giving the
information a natural home.

A fourth surface for humans: a `version` CLI verb (`dotnet-efcore-mcp version [--check-updates]`),
alongside the existing `hooks` and `onboard` verbs. On-demand, prints to stdout — safe, because CLI
verbs return before the stdio MCP server ever starts (see the verb dispatch at the top of
[`Program.cs`](../../src/DotnetEfCoreMcp.Server/Program.cs)). This mirrors `dotnet sdk check`.

### D5 — Enabled by default, with layered opt-out.

Checks run by default, but every gate below must pass. Any failure means silence, never an error.

| Gate | Behavior |
|---|---|
| `DOTNETEFCOREMCP_NO_UPDATE_CHECK` set (any value) | Disabled. Follows the `GH_NO_UPDATE_NOTIFIER` / `CHECKPOINT_DISABLE` naming convention. |
| `UpdateCheck:Enabled=false` in configuration | Disabled. Same `IConfiguration` surface as existing options. |
| CI detected (`CI` ≠ `false` and any of `CI`/`GITHUB_ACTIONS`/`TF_BUILD`/`BUILD_ID`/…) | Disabled. `CI=false` is honored as a force-off-CI escape hatch, per `ci-info`. |
| `DOTNET_CLI_TELEMETRY_OPTOUT=1` | Disabled. Someone who opted out of .NET phoning home did not mean "except this". |
| Install method unknown (D2) | Disabled. |
| Installed version unparseable | Disabled. |
| Cache fresh (within cadence) | Uses the cached result; no network call. |

Deliberately **not** gated on TTY, unlike GitHub CLI: an MCP stdio server never has one, so a TTY
gate would disable the feature entirely.

The alternative — **off by default, opt-in** — was considered and **rejected**, confirmed by the
maintainer. A feature nobody enables does not solve #98, and the privacy objection does not hold up
here: the request is an unauthenticated GET for a public package's version list on nuget.org, the
same feed this package is published to and the same data a human reads off the package page. It
carries no user identity, no usage data, and no payload. The enterprise case is served by the four
opt-outs above plus the `UpdateCheck:Source` override in O1.

### D6 — Non-blocking, time-bounded, failure-silent.

- The check runs as fire-and-forget background work started *after* the host is built, never on the
  `initialize` path. Startup must not depend on the network.
- HTTP timeout **3 s** (HashiCorp Checkpoint's value), overall budget 5 s, then abandon.
- Any exception — DNS, proxy, 403 from a mirror, malformed JSON — is caught, logged at `debug`, and
  the check is treated as "no result". Never surfaces as a tool error.
- Results land in a cache file read synchronously by `onboard`; a check that has not finished yet
  simply reports "not checked".

### D7 — Cache: one JSON file, XDG/LocalAppData, 24 h/7 d TTL.

```
%LOCALAPPDATA%\dotnet-efcore-mcp\update-check.json        (Windows)
$XDG_STATE_HOME/dotnet-efcore-mcp/update-check.json       (Linux/macOS, ~/.local/state fallback)
```

Honors the existing `DOTNETEFCOREMCP_HOMEDIR` override from
[`UserDirectory`](../../src/DotnetEfCoreMcp.Server/Onboarding/UserDirectory.cs), which is the
mechanism tests already use to stay out of the real profile. Contents: last check timestamp,
installed version at check time, latest version found, channel. Writes are best-effort and
corruption is treated as a cache miss — never a startup failure.

## Options still open

### O1 — Feed policy

| Option | Pros | Cons |
|---|---|---|
| **A. NuGet.org only** (recommended for v1) | Tiny; one `HttpClient`; no new dependency beyond `NuGet.Versioning` | Can advertise a version a user with source mapping or an air gap cannot install |
| B. Honor `NuGet.Config` via `NuGet.Protocol` | Correct for private feeds, credentials, offline | Heavy dependency; much larger surface for a cosmetic feature |
| C. A, with a documented `UpdateCheck:Source` override | Escape hatch for mirrors at near-zero cost | Still not credential-aware |

Recommendation: **A + C**. Document plainly that the check queries nuget.org and does not honor
`NuGet.Config`, and that `DOTNETEFCOREMCP_NO_UPDATE_CHECK` turns it off. Revisit B only if a user
reports a real private-feed deployment.

### O2 — Does the `dnx` path need anything? — resolved, measured

**No code. Documentation only.** Measured directly rather than assumed; see
[Does `dnx` refetch?](#does-dnx-refetch) for the experiment. `dnx` fetches the newest matching
version on each invocation even when an older one is already in the package cache, so the `dnx` path
is self-updating and a notice there would be pure noise.

`InstallMethod.Dnx` is therefore detected (so the `version` verb can *report* it honestly) but
suppressed from notification, exactly like the npm path.

## What was built

All four phases landed together. The module is
[`src/DotnetEfCoreMcp.Server/Updates`](../../src/DotnetEfCoreMcp.Server/Updates).

| Type | Role |
|---|---|
| `PackageVersionProvider` | Reads the build's `$(PackageVersion)` from assembly metadata. Lifted out of [`HooksCommand`](../../src/DotnetEfCoreMcp.Server/Hooks/HooksCommand.cs), which now delegates to it — both callers need the real NuGet version, for different reasons. |
| `InstallMethod` / `InstallMethodDetector` | D2's detection table. Returns `Unknown` freely. |
| `VersionChannel` | D3's prerelease-aware candidate selection and per-channel cadence. |
| `INuGetVersionLookup` / `FlatContainerVersionLookup` | Flat-container GET against nuget.org. Returns an empty list for every failure. |
| `UpdateCheckGate` | D5's gate table. |
| `UpdateCheckCache` | D7's JSON cache under `UserDirectory.Resolve()`. |
| `UpdateChecker` | Orchestrates gate → cache → lookup → compare. Never throws. |
| `UpdateNotificationService` | `BackgroundService` running the check after startup; holds the result. |
| `UpdateCheckOptions` | `UpdateCheck:Enabled` / `UpdateCheck:Source`. |
| `VersionCommand` | The `version [--check-updates]` CLI verb. |

Delivery is through the `onboard` tool's new `version` block
([`EfCoreMcpTools.DescribeVersion`](../../src/DotnetEfCoreMcp.Server/Tools/EfCoreMcpTools.cs)) plus a
single stderr log line, exactly as D4 specified. `ServerInstructions` now tells the agent that
`onboard` reports version state and that it should relay a notice rather than act on it.

The [npm shim](../../npm/bin/dotnet-efcore-mcp.js) sets `DOTNETEFCOREMCP_INSTALL_METHOD=npm` in the
child environment. Without it an npm launch is indistinguishable from a global install — it lands in
the same tools directory — and the user would be told to run an update the wrapper had just run.

### Notes from implementation

**`UpdateChecker`'s installed-version parameter is a separate constructor, not an optional one.**
It started as `string? installedVersion = null` with a `?? PackageVersionProvider.GetPackageVersion()`
fallback, which made "this build has no version" impossible to express — null meant "use the
default". A test asserting that a metadata-less build performs no lookup caught it. The two meanings
are now two constructors.

**The background service yields before checking.** A `BackgroundService`'s synchronous prologue runs
inline inside `StartAsync`, so without `await Task.Yield()` the first part of the check would run on
the startup path — the exact thing D6 forbids.

**Failed lookups are deliberately not cached.** Caching "no result" would suppress checks for a full
day because the network blipped once. Only a completed comparison is written.

### Verification

- 80 tests under `tests/DotnetEfCoreMcp.Server.Tests/Updates`; full suite 711 passing.
- `UpdateNotificationStdoutTests` is the critical guard: it redirects `Console.Out`, runs a check
  that *does* find an update, and asserts stdout stayed empty — plus that the notice contains no
  newline and survives JSON round-tripping, since it travels inside a tool result.
- Verified against a live stdio session: every stdout line parsed as JSON-RPC, and the `onboard`
  result carried the `version` block.

## Risks

| Risk | Mitigation |
|---|---|
| **Writing to stdout corrupts JSON-RPC.** The single highest-severity failure mode. | Notice paths never touch stdout; the `version` verb returns before the server starts; an explicit test asserts stdout purity. |
| Prerelease comparison nags users to downgrade | D3, plus the stable↔prerelease test matrix in Phase 2. |
| Suggesting an upgrade command that does not apply | D2 — silence when the install method is unknown. |
| Advertising a version a private-feed user cannot install | O1 documentation + opt-out; revisit `NuGet.Protocol` on demand. |
| Perceived as telemetry | Pull-only request carrying no user data; four opt-outs; documented explicitly. |
| Slow/hung network degrading startup | D6: background, 3 s timeout, 5 s budget, abandon silently. |
| Notice noise in agent context | D4: `onboard` only, never appended to query results. |

## Resolved questions

All four questions that originally blocked Phase 3 are settled.

### Default on or off? — **On**

Confirmed by the maintainer. The check is a plain GET against a well-known public service
(nuget.org), which is the same request a human makes by browsing the package page. It is a *pull*
carrying no user data, not a push of usage data, and it targets the public feed this package is
actually published to. The four opt-outs in D5 remain for users who want it off regardless.

### Does `dnx` refetch? — **Yes. Measured.**

Resolved empirically rather than by reading documentation, since
[the `dotnet tool exec` docs](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-exec)
do not state cache behavior precisely enough to rely on.

The package cache held `0.1.32-preview` while the feed's newest prerelease was `0.1.39-preview`:

```text
cached BEFORE:  0.1.20-preview  0.1.32-preview  0.1.32-preview.g842ad5eb9d
$ dnx DotnetEfCoreMcp.Server --prerelease --yes -- hooks --help
cached AFTER:   0.1.20-preview  0.1.32-preview  0.1.32-preview.g842ad5eb9d  0.1.39-preview
```

`dnx` acquired the newer version despite a usable cached one. The path is self-updating, so it is
detected but never notified (see [O2](#o2--does-the-dnx-path-need-anything--resolved-measured)).

### Cadence — **prerelease by default, 24 h**

The project will keep publishing prerelease versions for the foreseeable future, so prerelease is
the default channel rather than a special case. Cadence is 24 h, matching npm's prerelease-train
interval and the ecosystem's general default. The stable branch (7 d, stable-only candidates) stays
in the code because D3's channel logic needs it the moment a stable release ships, but it is not the
path anyone exercises today.

### Should the npm shim notify about itself? — **No**

Decision: **no**, for three reasons.

1. It cannot be done well. The shim is a thin launcher whose whole job is to exec and get out of the
   way; adding a registry round-trip puts a network call on the critical path of *every* server
   start, which D6 explicitly forbids for the server itself.
2. It would be redundant in the common case. `npx -y dotnet-efcore-mcp` — the invocation the README
   documents and the one MCP clients generate — already resolves the newest package per run. Only a
   global `npm i -g` install can go stale, and that is not a path this project documents.
3. The server already covers it. When launched through the shim the server knows it
   (`DOTNETEFCOREMCP_INSTALL_METHOD=npm`), so if the *npm package itself* is ever stale enough to
   matter, the right fix is for the shim to pass its own version to the server and let the existing
   check report it — a later refinement, not a second notifier.

## Out of scope

- Self-updating or self-installing from inside the server (D1).
- A `dotnetup`/`rustup`-style channel manager.
- Honoring `NuGet.Config`, credential providers, or private feeds in v1 (O1).
- Notifying about EF Core, .NET SDK, or target-app versions — this is about the MCP server only.
- Any change to how [hooks pin their version](./agent-hooks.md); that pinning is deliberate.
