#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Installs the skills shipped in this repository into an agent's skills
    directory.

.DESCRIPTION
    Agent CLIs discover skills by scanning a directory for folders containing a
    SKILL.md. This script copies (or links) the skills under `skills/` into the
    directory your client reads, so you do not have to remember each client's
    layout.

    Supported targets:

      claude   ~/.claude/skills            (Claude Code)
      agents   ~/.agents/skills            (shared location read by several CLIs)
      repo     <repo>/.github/skills       (committed, repo-scoped)

    Re-running is safe: existing installs of the same skill are replaced.

.PARAMETER Target
    Where to install. One of claude, agents, repo, or all. Defaults to agents.

.PARAMETER Skill
    Install only the named skill. Defaults to every skill in this directory.

.PARAMETER Path
    For -Target repo, the repository root to install into. Defaults to the
    current directory.

.PARAMETER Link
    Create a directory junction/symlink instead of copying, so edits to the repo
    are picked up immediately. Useful while developing a skill; may require
    elevation or Developer Mode on Windows.

.PARAMETER Uninstall
    Remove the skills instead of installing them.

.EXAMPLE
    ./skills/install-skills.ps1
    Installs every skill into ~/.agents/skills.

.EXAMPLE
    ./skills/install-skills.ps1 -Target claude -Skill dotnet-efcore-mcp-feedback

.EXAMPLE
    ./skills/install-skills.ps1 -Target repo -Path ../my-app
    Installs into ../my-app/.github/skills so the whole team gets them.
#>
[CmdletBinding()]
param(
    [ValidateSet('claude', 'agents', 'repo', 'all')]
    [string]$Target = 'agents',

    [string]$Skill,

    [string]$Path = '.',

    [switch]$Link,

    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$sourceRoot = $PSScriptRoot

$available = Get-ChildItem -Path $sourceRoot -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'SKILL.md') }

if (-not $available) {
    throw "No skills found in $sourceRoot (expected subdirectories containing SKILL.md)."
}

if ($Skill) {
    $available = $available | Where-Object { $_.Name -eq $Skill }
    if (-not $available) {
        $names = (Get-ChildItem -Path $sourceRoot -Directory |
            Where-Object { Test-Path (Join-Path $_.FullName 'SKILL.md') }).Name -join ', '
        throw "Skill '$Skill' not found. Available: $names"
    }
}

function Resolve-TargetRoots {
    param([string]$Which)

    # $HOME is a read-only automatic variable in PowerShell, so use another name.
    $userHome = if ($env:HOME) { $env:HOME } else { $env:USERPROFILE }
    switch ($Which) {
        'claude' { @(Join-Path $userHome '.claude/skills') }
        'agents' { @(Join-Path $userHome '.agents/skills') }
        'repo'   { @(Join-Path (Resolve-Path $Path) '.github/skills') }
        'all'    {
            @(
                (Join-Path $userHome '.claude/skills'),
                (Join-Path $userHome '.agents/skills')
            )
        }
    }
}

$roots = Resolve-TargetRoots -Which $Target

foreach ($root in $roots) {
    if (-not $Uninstall -and -not (Test-Path $root)) {
        New-Item -ItemType Directory -Force -Path $root | Out-Null
    }

    foreach ($skillDir in $available) {
        $destination = Join-Path $root $skillDir.Name

        if (Test-Path $destination) {
            # Remove-Item on a junction deletes the link, not the target, but be
            # explicit about it so a mistake cannot eat the repo copy.
            $existing = Get-Item $destination -Force
            if ($existing.LinkType) {
                $existing.Delete()
            } else {
                Remove-Item -Recurse -Force $destination
            }
        }

        if ($Uninstall) {
            Write-Host "removed  $destination"
            continue
        }

        if ($Link) {
            New-Item -ItemType Junction -Path $destination -Target $skillDir.FullName | Out-Null
            Write-Host "linked   $destination -> $($skillDir.FullName)"
        } else {
            Copy-Item -Recurse -Path $skillDir.FullName -Destination $destination
            # Eval scaffolding is for skill development, not for users of the skill.
            $evals = Join-Path $destination 'evals'
            if (Test-Path $evals) { Remove-Item -Recurse -Force $evals }
            Write-Host "installed $destination"
        }
    }
}

if (-not $Uninstall) {
    Write-Host ''
    Write-Host 'Restart your agent CLI (or reload the window) so it rescans the skills directory.'
}
