#!/usr/bin/env bash
# Installs the skills shipped in this repository into an agent's skills
# directory.
#
# Agent CLIs discover skills by scanning a directory for folders containing a
# SKILL.md. This script copies (or links) the skills under `skills/` into the
# directory your client reads, so you do not have to remember each client's
# layout.
#
# Targets:
#   claude   ~/.claude/skills       (Claude Code)
#   agents   ~/.agents/skills       (shared location read by several CLIs)
#   repo     <path>/.github/skills  (committed, repo-scoped)
#   all      claude + agents
#
# Usage:
#   ./skills/install-skills.sh [--target claude|agents|repo|all]
#                              [--skill NAME] [--path DIR] [--link] [--uninstall]
#
# Re-running is safe: existing installs of the same skill are replaced.

set -euo pipefail

target="agents"
skill=""
repo_path="."
link=0
uninstall=0

while [ $# -gt 0 ]; do
  case "$1" in
    --target)    target="$2"; shift 2 ;;
    --skill)     skill="$2"; shift 2 ;;
    --path)      repo_path="$2"; shift 2 ;;
    --link)      link=1; shift ;;
    --uninstall) uninstall=1; shift ;;
    -h|--help)   sed -n '2,25p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

source_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# find -printf is GNU-only, so derive the directory with dirname for BSD/macOS too.
available=()
while IFS= read -r skill_md; do
  available+=("$(dirname "$skill_md")")
done < <(find "$source_root" -mindepth 2 -maxdepth 2 -name SKILL.md | sort)

if [ ${#available[@]} -eq 0 ]; then
  echo "No skills found in $source_root (expected subdirectories containing SKILL.md)." >&2
  exit 1
fi

if [ -n "$skill" ]; then
  filtered=()
  skill_lower=$(printf '%s' "$skill" | tr '[:upper:]' '[:lower:]')
  for dir in "${available[@]}"; do
    [ "$(basename "$dir" | tr '[:upper:]' '[:lower:]')" = "$skill_lower" ] && filtered+=("$dir")
  done
  if [ ${#filtered[@]} -eq 0 ]; then
    names=""
    for dir in "${available[@]}"; do
      names="${names:+$names, }$(basename "$dir")"
    done
    echo "Skill '$skill' not found. Available: $names" >&2
    exit 1
  fi
  available=("${filtered[@]}")
fi

roots=()
case "$target" in
  claude) roots=("$HOME/.claude/skills") ;;
  agents) roots=("$HOME/.agents/skills") ;;
  repo)   roots=("$(cd "$repo_path" && pwd)/.github/skills") ;;
  all)    roots=("$HOME/.claude/skills" "$HOME/.agents/skills") ;;
  *) echo "unknown target: $target (expected claude, agents, repo, or all)" >&2; exit 2 ;;
esac

for root in "${roots[@]}"; do
  [ "$uninstall" -eq 1 ] || mkdir -p "$root"

  for skill_dir in "${available[@]}"; do
    name="$(basename "$skill_dir")"
    destination="$root/$name"

    # Only replace installs created by this script; preserve unrelated skills.
    if [ -e "$destination" ] || [ -L "$destination" ]; then
      if [ -L "$destination" ]; then
        managed_target="$(cd "$destination" && pwd -P)"
        [ "$managed_target" = "$(cd "$skill_dir" && pwd -P)" ] || {
          echo "refusing to replace unmanaged destination: $destination" >&2
          exit 1
        }
      elif [ ! -f "$destination/.dotnet-efcore-mcp-skill-install" ]; then
        echo "refusing to replace unmanaged destination: $destination" >&2
        exit 1
      fi
      rm -rf "$destination"
    fi

    if [ "$uninstall" -eq 1 ]; then
      echo "removed   $destination"
      continue
    fi

    if [ "$link" -eq 1 ]; then
      ln -s "$skill_dir" "$destination"
      echo "linked    $destination -> $skill_dir"
    else
      cp -R "$skill_dir" "$destination"
      # Eval scaffolding is for skill development, not for users of the skill.
      rm -rf "$destination/evals"
      : > "$destination/.dotnet-efcore-mcp-skill-install"
      echo "installed $destination"
    fi
  done
done

if [ "$uninstall" -eq 0 ]; then
  echo
  echo "Restart your agent CLI (or reload the window) so it rescans the skills directory."
fi
