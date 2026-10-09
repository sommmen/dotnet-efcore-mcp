# Skills

Agent skills shipped with `dotnet-efcore-mcp`. A skill is a folder containing a
`SKILL.md`: a description your agent reads to decide when the skill applies, plus
instructions it follows once it does. Agent CLIs discover them by scanning a
skills directory, so installing one means getting the folder into the directory
your client reads.

| Skill | What it does |
|---|---|
| [`dotnet-efcore-mcp-feedback`](./dotnet-efcore-mcp-feedback) | Turns a frustrating encounter with this MCP server into a self-contained GitHub issue — sanitized of your private code, with a reproduction the maintainer can actually run. |

## Install

```powershell
# Windows / PowerShell
./skills/install-skills.ps1
```

```bash
# macOS / Linux
./skills/install-skills.sh
```

By default this installs every skill into `~/.agents/skills`, which several
agent CLIs read. Pick a different destination with `-Target` / `--target`:

| Target | Destination | Use when |
|---|---|---|
| `agents` (default) | `~/.agents/skills` | Shared location read by several CLIs |
| `claude` | `~/.claude/skills` | Claude Code |
| `repo` | `<path>/.github/skills` | You want it committed so your whole team gets it |
| `all` | both user-level directories | You switch between clients |

```powershell
# Just the feedback skill, for Claude Code
./skills/install-skills.ps1 -Target claude -Skill dotnet-efcore-mcp-feedback

# Into another repository, so the team gets it on clone
./skills/install-skills.ps1 -Target repo -Path ../my-app

# Remove it again
./skills/install-skills.ps1 -Uninstall
```

Restart your agent CLI (or reload the VS Code window) afterwards so it rescans
the skills directory.

### Installing without this repo

If you do not have a clone, copy the skill folder straight out of GitHub:

```bash
mkdir -p ~/.agents/skills
curl -sL https://github.com/sommmen/dotnet-efcore-mcp/archive/refs/heads/main.tar.gz \
  | tar -xz --strip-components=2 -C ~/.agents/skills \
      dotnet-efcore-mcp-main/skills/dotnet-efcore-mcp-feedback
```

A skill is just a directory of markdown and scripts — there is nothing to build
and no runtime to install, so copying the folder is the whole installation.

### Developing a skill

Use `-Link` / `--link` to install a junction/symlink instead of a copy, so edits
in this repo take effect without reinstalling:

```powershell
./skills/install-skills.ps1 -Link
```

On Windows this may need Developer Mode or an elevated prompt, since creating
links is a privileged operation by default.

The eval prompts and assertions live in each skill's `evals/evals.json`, and
`docs/development/agent-skills.md` covers how to run them.

### Packaging a `.skill` archive

Some clients install a skill from a single `.skill` file. Build one with the
[`skill-creator`](https://github.com/anthropics/skills) packager:

```bash
python -m scripts.package_skill <path-to>/skills/dotnet-efcore-mcp-feedback
```

The archive is deliberately **not** committed. It is byte-for-byte derivable
from the source folder, so a checked-in copy adds nothing except a second thing
to keep in sync — and a stale `.skill` that silently installs an old version is
a worse failure than not having one. Build it when a client needs it.

## Why ship a feedback skill at all

This server sits between an agent and someone's private database, inside a
codebase the maintainer will never see. When it misbehaves, the person best
placed to report it is also the person least able to share the evidence — the
`DbContext` is proprietary, the connection string is a secret, and the query is
business logic.

The result is predictable: either no report, or a report stripped so thoroughly
that nobody can act on it. The feedback skill exists to make the useful middle
path cheap — strip the secrets, generalize the shape, rebuild the failure
against the repo's own `tests/Fixtures/SampleApp`, and file something a
maintainer can reproduce on a different machine months later.
