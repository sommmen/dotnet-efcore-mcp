# Agent skills

[← Back to Development Guide](../../DEVELOPMENT.md)

Skills: [`skills/`](../../skills) ·
User-facing setup: [README "Install agent skills"](../../README.md#install-agent-skills)

## What a skill is

A folder containing a `SKILL.md`: YAML frontmatter with a `name` and a
`description`, followed by markdown instructions. Agent CLIs keep every
installed skill's name and description in context permanently, and load the body
only when the description matches what the user is doing. That split is why the
description does most of the work — it is the only part the agent sees when
deciding whether the skill is relevant.

Skills can bundle supporting files. Anything in `references/` is loaded on
demand when `SKILL.md` points at it, and anything in `scripts/` can be executed
without being read into context at all. The practical effect is that a skill can
carry far more material than would fit in a prompt, as long as `SKILL.md` is
clear about when each piece is needed.

## How skills relate to hooks

[Agent hooks](./agent-hooks.md) and skills solve adjacent problems and are easy
to confuse:

| | Hooks | Skills |
|---|---|---|
| Trigger | A lifecycle event (agent wrote a file matching a pattern) | The agent judging a description relevant to the task |
| Payload | A short reminder injected as context | A full workflow the agent reads and follows |
| Scope | Repo-local config files, committed | A folder installed into the user's skills directory |
| Good for | Counteracting drift — re-delivering something already known | Teaching a procedure the agent does not know |

Hooks nudge; skills instruct. The LINQ validation reminder is a hook because the
agent already knows how to validate and just forgets. Filing a good issue is a
skill because the procedure — sanitize, generalize, rebuild against the fixture
— is not something an agent arrives knowing.

## Shipped skills

### `dotnet-efcore-mcp-feedback`

Files a self-contained GitHub issue against this repository when the server
misbehaves or is awkward to use.

The problem it solves is structural. This server runs inside someone's private
codebase, against a real `DbContext` and a real connection string, which means
the person best placed to report a bug is also the person least able to share
the evidence. Reports therefore tend toward one of two failure modes: nothing
gets filed, or something gets filed that has been scrubbed past the point of
usefulness.

The skill treats report-writing as translation rather than redaction. It
separates sanitization (remove secrets) from generalization (re-express the
structure in neutral terms), and anchors the result on
[`tests/Fixtures/SampleApp`](../../tests/Fixtures/SampleApp) so the maintainer's
reproduction cost is a `dotnet build` they already run. Where a model
configuration is too intricate for prose — TPH with a converted discriminator,
owned types, split assemblies — it escalates to a draft PR carrying a minimal
repro project under `tests/Fixtures/`, while still requiring the issue to stand
alone.

Layout:

```
skills/dotnet-efcore-mcp-feedback/
├── SKILL.md                          # workflow
├── references/
│   ├── issue-template.md             # structure + worked example
│   ├── writing-guide.md              # phrasing, before/after rewrites
│   ├── sanitization.md               # strip vs. generalize
│   └── draft-pr-repro.md             # when and how to scaffold a repro PR
├── scripts/
│   ├── collect_environment.ps1       # environment table, Windows
│   └── collect_environment.sh        # environment table, POSIX
└── evals/evals.json                  # test prompts + assertions
```

[Issue #85](https://github.com/sommmen/dotnet-efcore-mcp/issues/85) is the
quality bar the skill targets, and the worked example in `issue-template.md` is
modelled on it.

## Installation

[`skills/install-skills.ps1`](../../skills/install-skills.ps1) and
[`install-skills.sh`](../../skills/install-skills.sh) copy skill folders into
whichever directory the user's client reads — `~/.agents/skills`,
`~/.claude/skills`, or a repository's `.github/skills`.

There is no build step: a skill is markdown and scripts, so installation is a
directory copy. The script exists because clients disagree about where that
directory is, not because the copy is complicated.

`-Link`/`--link` installs a junction or symlink instead, which is what you want
while editing a skill — the installed copy tracks the repo. The `evals/` folder
is excluded from plain copies, since test scaffolding is noise for someone
merely using the skill.

## Developing a skill

`evals/evals.json` holds the test prompts and their assertions. The prompts are
deliberately written the way a frustrated user actually types — lowercase, run-on,
with private identifiers and a live connection string embedded in the complaint —
because a skill that only handles tidy input will not survive contact with real
reports.

Two properties are worth checking mechanically rather than by eye, since both
are exact string matches and both are easy to miss on the fifth file:

- **Nothing private survives.** No fragment of a connection string, hostname,
  employer name, or proprietary type name reaches the issue body.
- **Something useful survives.** The structural shape that caused the bug is
  still present under neutral names, rather than deleted along with the secrets.

The second is the one that silently regresses. A change that makes the skill
more cautious can quietly turn good reports into unusable ones, and only a
structural assertion catches that.

When changing the skill, rerun the eval prompts against both the new version and
the previous one, and compare — a skill that scores well in isolation can still
be worse than what it replaced.
