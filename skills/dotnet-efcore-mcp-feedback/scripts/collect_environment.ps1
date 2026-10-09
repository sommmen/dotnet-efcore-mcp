#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Collects the environment facts a dotnet-efcore-mcp issue needs, as a
    ready-to-paste markdown table.

.DESCRIPTION
    Prints only facts it can actually determine. Anything it cannot find is
    reported as "unknown" rather than guessed, because a wrong version in an
    issue is worse than an admitted gap.

    Nothing here reads secrets: user-secrets and connection strings are never
    touched. Config files are scanned only for the QueryExecution:Mode key.

.PARAMETER ProjectPath
    Optional path to the target project (.csproj) or its directory, used to
    report the EF Core version and target framework.

.EXAMPLE
    ./collect_environment.ps1 -ProjectPath ../MyApp/MyApp.csproj
#>
[CmdletBinding()]
param(
    [string]$ProjectPath
)

$ErrorActionPreference = 'Continue'

function Get-ValueOrUnknown {
    param([scriptblock]$Probe)
    try {
        $value = & $Probe
        if ([string]::IsNullOrWhiteSpace($value)) { return 'unknown' }
        return $value.ToString().Trim()
    } catch {
        return 'unknown'
    }
}

$serverVersion = Get-ValueOrUnknown {
    $line = (dotnet tool list --global 2>$null) |
        Where-Object { $_ -match '(?i)DotnetEfCoreMcp\.Server' } |
        Select-Object -First 1
    if ($line) { ($line -split '\s+' | Where-Object { $_ })[1] }
}

$installMethod = if ($serverVersion -ne 'unknown') {
    '.NET global tool'
} else {
    'unknown (not installed as a global tool - dnx, npm wrapper, or from source?)'
}

$sdkVersion = Get-ValueOrUnknown { dotnet --version 2>$null }

$osDescription = Get-ValueOrUnknown {
    [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
}

# QueryExecution:Mode may live in an MCP client config, appsettings, or an
# environment variable. Check the cheap, non-secret sources only.
$queryMode = 'unknown (check your MCP config / appsettings; default is Auto)'
if ($env:QueryExecution__Mode) {
    $queryMode = "$($env:QueryExecution__Mode) (from QueryExecution__Mode env var)"
} else {
    $configCandidates = @(
        '.vscode/mcp.json', '.mcp.json', 'appsettings.json',
        'appsettings.Development.json', '.vscode/settings.json'
    )
    foreach ($candidate in $configCandidates) {
        if (-not (Test-Path $candidate)) { continue }
        $match = Select-String -Path $candidate -Pattern 'QueryExecution(__|:|")\s*[:"]?\s*Mode' -ErrorAction SilentlyContinue
        if ($match) {
            $queryMode = "see $candidate (line $($match[0].LineNumber))"
            break
        }
    }
}

$efCoreVersion = 'unknown'
$targetFramework = 'unknown'
if ($ProjectPath) {
    $csproj = if (Test-Path $ProjectPath -PathType Container) {
        Get-ChildItem -Path $ProjectPath -Filter *.csproj -File | Select-Object -First 1
    } else {
        Get-Item -Path $ProjectPath -ErrorAction SilentlyContinue
    }

    if ($csproj) {
        $content = Get-Content -Raw -Path $csproj.FullName
        $tfmMatch = [regex]::Match($content, '<TargetFrameworks?>([^<]+)</TargetFrameworks?>')
        if ($tfmMatch.Success) { $targetFramework = $tfmMatch.Groups[1].Value }

        $efMatch = [regex]::Match(
            $content,
            '<PackageReference\s+Include="Microsoft\.EntityFrameworkCore[^"]*"\s+Version="([^"]+)"')
        if ($efMatch.Success) {
            $efCoreVersion = $efMatch.Groups[1].Value
        } else {
            # Central package management hoists versions into a
            # Directory.Packages.props somewhere *above* the project, so walk up.
            $dir = $csproj.Directory
            while ($dir -and $efCoreVersion -eq 'unknown') {
                $props = Join-Path $dir.FullName 'Directory.Packages.props'
                if (Test-Path $props) {
                    $efPropsMatch = [regex]::Match(
                        (Get-Content -Raw -Path $props),
                        '<PackageVersion\s+Include="Microsoft\.EntityFrameworkCore[^"]*"\s+Version="([^"]+)"')
                    if ($efPropsMatch.Success) { $efCoreVersion = $efPropsMatch.Groups[1].Value }
                }
                $dir = $dir.Parent
            }
        }
    }
}

@"
| | |
|---|---|
| Server version | $serverVersion |
| Install method | $installMethod |
| ``QueryExecution:Mode`` | $queryMode |
| Provider | unknown (fill in: SQL Server / PostgreSQL / SQLite / InMemory) |
| EF Core version | $efCoreVersion |
| Target framework | $targetFramework |
| .NET SDK | $sdkVersion |
| OS | $osDescription |
| MCP client | unknown (fill in: VS Code / Claude Code / Copilot CLI / Codex CLI) |

<!-- Rows marked unknown need filling in by hand. Leave them as "unknown" rather
     than guessing, and drop any row that does not apply to this report. -->
"@
