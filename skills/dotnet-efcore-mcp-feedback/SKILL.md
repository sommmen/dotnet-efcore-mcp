---
name: dotnet-efcore-mcp-feedback
description: >-
  File a self-contained GitHub issue against sommmen/dotnet-efcore-mcp when the
  dotnet-efcore-mcp MCP server behaves badly, is confusing, blocks you, or just
  feels awkward to use. Use this skill whenever a user says something like
  "report this", "file feedback", "that EF Core MCP tool is broken",
  "preview_query_sql keeps failing", "run_query gave a useless error",
  "load_assembly can't find my DbContext", "the MCP server docs are wrong", or
  expresses frustration with any dotnet-efcore-mcp tool, error message,
  configuration step, or install path — even when they do not explicitly ask for
  an issue to be filed. Also use it proactively after you hit a dotnet-efcore-mcp
  wall yourself and had to work around it. Do not use it for bugs in the user's
  own EF Core application, for general EF Core/LINQ questions, or for other
  repositories.
---

# dotnet-efcore-mcp feedback

Turn a frustrating encounter with the `dotnet-efcore-mcp` MCP server into a
GitHub issue a maintainer can act on months later, from a different machine,
without ever seeing your repo.

## Why this skill exists

The people who hit `dotnet-efcore-mcp`'s rough edges are agents and developers
deep inside someone else's private codebase. The evidence lives there: a real
`DbContext`, a real connection string, a half-finished LINQ query. None of that
can go into a public issue, and even if it could, the maintainer cannot run it.

So the job here is not "copy the error into an issue." It is **translation**:
take a concrete failure inside a private system and re-express it as a
reproducible, self-contained report built only from the server's own public
surface — tool names, parameters, error text, configuration keys, documented
behavior. Done well, the maintainer reads the issue, recreates the situation
with the repo's own `tests/Fixtures/SampleApp`, and never needs to ask a
follow-up question.

A good benchmark for the bar: [issue #85](https://github.com/sommmen/dotnet-efcore-mcp/issues/85).
It reports a real failure from a private audit-log endpoint, but every detail a
reader needs is in the issue itself, the repro is expressed in generic entity
terms, and it names the specific code path it suspects.

## Workflow

### 1. Establish what actually happened

Before writing anything, pin down the sequence. If you hit the problem yourself
in this session, read back over your own transcript — the exact tool call, the
exact arguments, the exact error. If the user is reporting it, ask for whatever
you are missing rather than guessing; a wrong repro wastes more of the
maintainer's time than no repro.

You need, at minimum:

- The tool or command involved (`run_query`, `load_assembly`, `hooks install`, …)
  or the documentation page, if the complaint is about docs.
- What you expected versus what happened, stated as two concrete outcomes.
- The verbatim error message or output, if there was one.
- Whether it fails every time or intermittently.

Users paraphrase error messages constantly — "it said something about a missing
policy" — and a paraphrase is not quotable, because the maintainer greps for the
literal string. When you only have a paraphrase, recover the real text: the
server is open source, so find the message in its source and quote that. If you
cannot find it, label what you have as the reporter's paraphrase rather than
presenting it inside a code fence where it reads as a transcript.

If the user's complaint is vague ("this thing is annoying"), dig for the moment
it went wrong. "Annoying" usually compresses a specific event — a confusing
error, a required step nobody documented, a tool that exists but refuses to run.

### 2. Collect the environment facts

Maintainers triage half of all reports from version and mode alone. Gather these
from the environment, not from memory:

| Fact | Where to find it |
|---|---|
| Server version | `dotnet tool list --global` (look for `DotnetEfCoreMcp.Server`), or the `version` field in the client's MCP config |
| Install method | .NET global tool / `dnx` / npm wrapper / `dotnet run --project` from source |
| `QueryExecution:Mode` | The MCP config, `appsettings.json`, or user-secrets; note `Auto` explicitly if left at the default — it is the default most reports come from |
| Database provider | SQL Server, PostgreSQL, SQLite, InMemory, … |
| EF Core + target TFM | The target project's `.csproj` |
| OS and .NET SDK | `dotnet --version` |
| MCP client | VS Code, Claude Code, Copilot CLI, Codex CLI, … |

Run `scripts/collect_environment.ps1` (or the `.sh`) rather than assembling this
by hand — it reads the installed version, SDK, OS, and target TFM directly and
marks anything it cannot determine, which is both faster and harder to get
wrong than recalling them.

Three outcomes, and the distinction carries information:

- **Known** — state it.
- **Not reached** — the failure happened before this mattered. A connection
  misconfiguration that fails at startup never touches the provider, so "not
  reached" is more informative than listing one.
- **Unknown** — it applies, you could not determine it. Say so. A silently
  guessed version number is worse than an admitted gap, because the maintainer
  will trust it and triage against the wrong build.

Omit rows that cannot apply at all; a documentation typo needs no provider row.

### 3. Build a repro that runs on the maintainer's machine

This is where most reports fail, and it is the part worth spending real effort
on. The question to keep asking is: *could someone with only a clone of
`dotnet-efcore-mcp` reproduce this?*

Work from the inside out:

- **Rename every type the user gave you — including the ones that already look
  neutral.** This is the step that quietly fails, so do it first and do it
  mechanically: list the user's type and property names, assign each a new one,
  and build the repro from the new list. Do not build the repro from their names
  and plan to scrub it later; by then the names are load-bearing in your own
  draft and each one has a reason to stay.

  The trap is specifically the plausible-looking name. `AuditLogEntry` obviously
  needs renaming. `Document`, `ContractDocument`, `InvoiceDocument` feel like
  they are already generic — and they are also the exact class names in
  someone's private assembly, which makes them greppable and confirmatory. You
  cannot tell from inside the codebase which names are distinctive, because
  every name looks ordinary once you have read it a hundred times. Rename them
  all; it costs a minute and the structure is what reproduces the bug, not the
  nouns.

- **Keep the shape, drop the domain.** `AuditLog`/`MessageArgument` becomes
  `Order`/`OrderLine` — one-to-many with a collection navigation, which is the
  structural fact the bug depended on. Keep the relationship cardinality, the
  key types, the nullability, the inheritance strategy, and any configuration
  that is load-bearing; discard everything else.
- **Prefer the repo's own fixture.** `tests/Fixtures/SampleApp` is a real EF Core
  app the maintainer already builds on every test run. If you can express the
  repro against entities shaped like its own, the maintainer's setup cost drops
  to near zero. Say so explicitly: "reproducible against `SampleApp`'s
  `Blog`/`Post` pair, or any one-to-many."
- **Number the steps.** Each step is one action with one observable result.
  Someone should be able to follow them without inferring anything.
- **Show the exact call.** Include the tool name and the argument values as they
  were sent. A paraphrase ("I queried the orders") is not a repro.

Verify your own repro before shipping it. Reread the steps as if you have never
seen the codebase: is any step only executable by someone who already has the
private project? If so, that step needs generalizing or replacing.

#### When a written repro is not enough

Some failures resist prose — they need a specific model configuration, a TPH
hierarchy, an owned type, a provider-specific column mapping, a multi-project
assembly layout. If you find yourself writing "you'd need a context where…" and
the description is growing past a paragraph, a runnable artifact is worth more
than more words.

In that case, open a **draft pull request** containing a minimal reproduction
project and link it from the issue. Keep it small and obviously disposable:

- Put it under `tests/Fixtures/` next to the existing sample, named for the bug
  (for example `tests/Fixtures/ReproIncludeOverConcat/`).
- Include only what the failure needs — the `DbContext`, the entities involved,
  and a README with the command that triggers it and the output you saw.
- Open it as a **draft** with a title like
  `repro: Include over Concat fails in Auto mode` and a body that links back to
  the issue. Draft signals "evidence, not a proposed fix" and avoids implying
  the maintainer should merge it.
- In the issue, link the draft PR and still include the written steps. The PR is
  a supplement, never a replacement — an issue that says "see the PR" is not
  self-contained.

Use `references/draft-pr-repro.md` for the exact commands and scaffolding.

Do not open a draft PR when plain steps suffice. Most issues do not need one,
and an unnecessary repro project is a maintenance cost for someone else.

### 4. Check it is not already known

Three cheap checks, in increasing cost. Each can turn a bug report into
something more useful — or into no report at all, which is a good outcome when
the alternative was noise.

**Is it already fixed?** Compare the user's installed version against the latest
release. "Reported a bug that was fixed two releases ago" is the single most
common wasted report, and the fix is one command:

```bash
gh release list --repo sommmen/dotnet-efcore-mcp --limit 5
```

If a newer version exists, check whether its changelog or merged PRs mention the
area. When you find a likely fix, say so and recommend upgrading rather than
filing — and if the user cannot upgrade, file it with the version gap stated
explicitly, since "still broken on the latest" and "broken on an old build" are
different reports.

**Is it a documented limitation?** Grep `docs/` for the feature. This repo is
candid about what is untested or unsupported, and a known limitation is an
`enhancement` ("please support X"), not a `bug` ("X is broken"). Filing it as a
bug invites a "works as designed" close that helps nobody.

**Is it already reported?** Search the tracker for the tool name, the
distinctive part of the error, and the configuration key:

```bash
gh issue list --repo sommmen/dotnet-efcore-mcp --state all --limit 50 \
  --search "preview_query_sql"
```

What to do with a match depends on its state. If the issue is **open**, comment
with your environment and repro — a second reporter is evidence the bug has more
than one victim, which is more useful to the maintainer than a near-duplicate.
If it is **closed**, do not comment; comments on closed issues are routinely
missed. File a new issue and cross-reference the old one, explaining how yours
differs. Either way, tell the user what you did and why.

### 5. Write the issue

Use `references/issue-template.md` for the structure and
`references/writing-guide.md` for how to phrase each section. Two habits matter
most:

**Separate observation from theory.** Report what happened as fact; mark your
explanation as a hypothesis. "This appears to be because `PreviewSqlAsync` does
not route through the out-of-process executors — though I have not verified
that" lets the maintainer use your lead without inheriting your mistake if you
are wrong. Confident wrong diagnoses send people down dead ends.

**Say why it matters.** A bug report without impact is a curiosity. "This meant
an agent following the server's own instructions could not detect the bug" tells
the maintainer where it sits on their priority list far better than any severity
label.

Scrub the draft before filing — `references/sanitization.md` lists what to strip
and what is safe to keep. Read the draft as a stranger would and search it for
each of the user's original names one at a time, rather than skimming for
anything that "looks private". You renamed the types in step 3, but names leak
back in while drafting: quoted in an error, mentioned in a hypothesis, left
behind in a `HasValue<...>` line inside a configuration snippet.

If the user pasted a credential anywhere in the conversation, keeping it out of
the issue is not enough — it has already left their control. Tell them to rotate
it, at the moment you notice.

### 6. File it

Show the user the full rendered body and get a yes before anything is created.
They know their own secrecy constraints better than you do, and an issue is
public the moment it exists.

```bash
gh issue create --repo sommmen/dotnet-efcore-mcp \
  --title "run_query surfaces a generic error for untranslatable LINQ" \
  --body-file issue-body.md \
  --label bug
```

Write the body to a file rather than passing it inline — multi-line markdown
through shell quoting mangles code fences, and on PowerShell it mangles them
differently than on bash.

Labels available on the repo: `bug`, `enhancement`, `documentation`, `question`,
`accessibility`, `good first issue`, `help wanted`. Pick one, maybe two. For
ergonomics complaints — the thing works but is awkward — `enhancement` is
usually right, and the issue should lead with the friction rather than framing
working behavior as broken.

Report the issue URL back to the user when you are done.

## Title conventions

Titles are read in a list of fifty. Make the subject the thing that broke and
the predicate the symptom, specific enough to be searchable:

- Good: `preview_query_sql unavailable outside InProcess mode`
- Good: `load_assembly reports "no DbContext found" when the context lives in a referenced project`
- Weak: `Bug in query tool` — unsearchable, tells the reader nothing
- Weak: `Please fix the MCP server` — no subject at all

Mirror the repo's commit style where it reads naturally (`preview_query_sql` /
`run_query` as the scope), but do not force a Conventional Commits prefix onto
an issue title — that convention applies to commits and PR titles here.

## Reference files

- `references/issue-template.md` — the section-by-section issue structure, with a
  worked example.
- `references/writing-guide.md` — how to phrase each section; before/after
  rewrites of weak reports.
- `references/sanitization.md` — what must be stripped, what is safe to keep, and
  how to generalize a domain model without losing the detail that matters.
- `references/draft-pr-repro.md` — scaffolding a minimal repro project and
  opening it as a draft PR.
- `scripts/collect_environment.ps1` / `scripts/collect_environment.sh` — gather
  the environment table automatically; run one instead of assembling it by hand.
