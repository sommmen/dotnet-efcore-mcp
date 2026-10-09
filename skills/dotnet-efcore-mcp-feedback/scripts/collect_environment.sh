#!/usr/bin/env bash
# Collects the environment facts a dotnet-efcore-mcp issue needs, as a
# ready-to-paste markdown table.
#
# Prints only facts it can actually determine. Anything it cannot find is
# reported as "unknown" rather than guessed, because a wrong version in an issue
# is worse than an admitted gap.
#
# Nothing here reads secrets: user-secrets and connection strings are never
# touched. Config files are scanned only for the QueryExecution:Mode key.
#
# Usage: ./collect_environment.sh [path/to/target.csproj | path/to/project/dir]

set -uo pipefail

project_path="${1:-}"

server_version="$(dotnet tool list --global 2>/dev/null \
  | grep -i 'dotnetefcoremcp.server' \
  | awk '{print $2}' \
  | head -n1)"
server_version="${server_version:-unknown}"

if [ "$server_version" != "unknown" ]; then
  install_method=".NET global tool"
else
  install_method="unknown (not installed as a global tool - dnx, npm wrapper, or from source?)"
fi

sdk_version="$(dotnet --version 2>/dev/null || echo unknown)"
os_description="$(uname -srm 2>/dev/null || echo unknown)"

query_mode="unknown (check your MCP config / appsettings; default is Auto)"
if [ -n "${QueryExecution__Mode:-}" ]; then
  query_mode="${QueryExecution__Mode} (from QueryExecution__Mode env var)"
else
  for candidate in .vscode/mcp.json .mcp.json appsettings.json \
                   appsettings.Development.json .vscode/settings.json; do
    [ -f "$candidate" ] || continue
    if grep -qE 'QueryExecution(__|:|")[[:space:]]*[:"]?[[:space:]]*Mode' "$candidate" 2>/dev/null; then
      query_mode="see $candidate"
      break
    fi
  done
fi

ef_core_version="unknown"
target_framework="unknown"
if [ -n "$project_path" ]; then
  if [ -d "$project_path" ]; then
    csproj="$(find "$project_path" -maxdepth 1 -name '*.csproj' | head -n1)"
  else
    csproj="$project_path"
  fi

  if [ -n "${csproj:-}" ] && [ -f "$csproj" ]; then
    target_framework="$(grep -oE '<TargetFrameworks?>[^<]+' "$csproj" \
      | sed -E 's/<TargetFrameworks?>//' | head -n1)"
    target_framework="${target_framework:-unknown}"

    ef_core_version="$(grep -oE '<PackageReference[[:space:]]+Include="Microsoft\.EntityFrameworkCore[^"]*"[[:space:]]+Version="[^"]+"' "$csproj" \
      | grep -oE 'Version="[^"]+"' | sed -E 's/Version="([^"]+)"/\1/' | head -n1)"

    # Central package management hoists versions into a Directory.Packages.props
    # somewhere *above* the project, so walk up until one is found.
    if [ -z "$ef_core_version" ]; then
      dir="$(cd "$(dirname "$csproj")" && pwd)"
      while [ -n "$dir" ] && [ "$dir" != "/" ]; do
        props="$dir/Directory.Packages.props"
        if [ -f "$props" ]; then
          ef_core_version="$(grep -oE '<PackageVersion[[:space:]]+Include="Microsoft\.EntityFrameworkCore[^"]*"[[:space:]]+Version="[^"]+"' "$props" \
            | grep -oE 'Version="[^"]+"' | sed -E 's/Version="([^"]+)"/\1/' | head -n1)"
          [ -n "$ef_core_version" ] && break
        fi
        dir="$(dirname "$dir")"
      done
    fi
    ef_core_version="${ef_core_version:-unknown}"
  fi
fi

cat <<EOF
| | |
|---|---|
| Server version | $server_version |
| Install method | $install_method |
| \`QueryExecution:Mode\` | $query_mode |
| Provider | unknown (fill in: SQL Server / PostgreSQL / SQLite / InMemory) |
| EF Core version | $ef_core_version |
| Target framework | $target_framework |
| .NET SDK | $sdk_version |
| OS | $os_description |
| MCP client | unknown (fill in: VS Code / Claude Code / Copilot CLI / Codex CLI) |

<!-- Rows marked unknown need filling in by hand. Leave them as "unknown" rather
     than guessing, and drop any row that does not apply to this report. -->
EOF
