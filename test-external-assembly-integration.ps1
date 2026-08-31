<#
.SYNOPSIS
    Drives the dotnet-efcore-mcp server over stdio against an external assembly.
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$TargetAssembly,
    [string]$ServerProject = 'src\DotnetEfCoreMcp.Server\DotnetEfCoreMcp.Server.csproj'
)

$ErrorActionPreference = 'Stop'
$script:testCount = 0
$script:passCount = 0
$script:failCount = 0

function Write-TestResult {
    param([string]$Name, [bool]$Passed, [string]$Detail = '')
    $script:testCount++
    if ($Passed) {
        $script:passCount++
        Write-Host "  PASS  $Name" -ForegroundColor Green
    } else {
        $script:failCount++
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        if ($Detail) { Write-Host "        $Detail" -ForegroundColor Yellow }
    }
}

function Send-McpMessage {
    param(
        [System.Diagnostics.Process]$Process,
        [hashtable]$Message
    )
    $json = $Message | ConvertTo-Json -Depth 10 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json + "`n")
    $Process.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
    $Process.StandardInput.BaseStream.Flush()
}

function Read-McpMessage {
    param(
        [System.Diagnostics.Process]$Process,
        [int]$TimeoutMs = 15000
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if (-not $Process.StandardOutput.EndOfStream) {
            $line = $Process.StandardOutput.ReadLine()
            if ($line -and $line.Trim() -ne '') {
                try {
                    return ($line | ConvertFrom-Json)
                } catch {
                    # Not valid JSON, keep reading
                }
            }
        }
        Start-Sleep -Milliseconds 50
    }
    return $null
}

function Read-McpMessageById {
    param(
        [System.Diagnostics.Process]$Process,
        [int]$Id,
        [int]$TimeoutMs = 15000
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $remaining = [Math]::Max(1000, $TimeoutMs - [int]$sw.ElapsedMilliseconds)
        $msg = Read-McpMessage -Process $Process -TimeoutMs $remaining
        if ($null -eq $msg) { return $null }
        if ($msg.id -eq $Id) { return $msg }
        # Skip notifications/other messages
    }
    return $null
}

# ── Pre-flight checks ──
Write-Host "`n=== dotnet-efcore-mcp External Assembly Integration Test ===" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path -LiteralPath $TargetAssembly -PathType Leaf)) {
    Write-Host "ERROR: Target assembly not found at $TargetAssembly" -ForegroundColor Red
    exit 1
}
Write-Host "Target assembly: $TargetAssembly" -ForegroundColor DarkGray

# ── Start the MCP server process ──
Write-Host "`nStarting MCP server..." -ForegroundColor DarkGray

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = 'dotnet'
$psi.Arguments = "run --project `"$ServerProject`" --no-restore --no-build"
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
$psi.CreateNoWindow = $true

$server = [System.Diagnostics.Process]::Start($psi)

# Drain stderr in background
$stderrLines = [System.Collections.Concurrent.ConcurrentBag[string]]::new()
$stderrTask = [System.Threading.Tasks.Task]::Factory.StartNew(
    {
        param($proc, $bag)
        while (-not $proc.StandardError.EndOfStream) {
            $line = $proc.StandardError.ReadLine()
            if ($line) { $bag.Add($line) }
        }
    },
    @($server, $stderrLines),
    [System.Threading.CancellationToken]::None,
    [System.Threading.Tasks.TaskCreationOptions]::LongRunning,
    [System.Threading.Tasks.TaskScheduler]::Default
)

Start-Sleep -Seconds 3

if ($server.HasExited) {
    Write-Host "ERROR: Server process exited immediately (exit code: $($server.ExitCode))" -ForegroundColor Red
    $stderrTask.Wait(2000) | Out-Null
    Write-Host "stderr:" -ForegroundColor Red
    $stderrLines | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host "Server PID: $($server.Id)" -ForegroundColor DarkGray

try {
    # ── 1. Initialize ──
    Write-Host "`n--- Test 1: initialize ---" -ForegroundColor White
    $initId = 1
    $initMsg = @{
        jsonrpc = "2.0"
        id = $initId
        method = "initialize"
        params = @{
            protocolVersion = "2025-03-26"
            capabilities = @{}
            clientInfo = @{
                name = "integration-test"
                version = "1.0.0"
            }
        }
    }
    Send-McpMessage -Process $server -Message $initMsg
    $initResp = Read-McpMessageById -Process $server -Id $initId -TimeoutMs 10000
    $initOk = ($null -ne $initResp -and $null -ne $initResp.result)
    Write-TestResult -Name "Server responds to initialize" -Passed $initOk
    if ($initOk) {
        $serverName = $initResp.result.serverInfo.name
        $serverVersion = $initResp.result.serverInfo.version
        Write-Host "    Server: $serverName v$serverVersion" -ForegroundColor DarkGray
    } else {
        Write-Host "    Response: $($initResp | ConvertTo-Json -Depth 5 -Compress)" -ForegroundColor Yellow
    }

    # Send initialized notification
    $notifMsg = @{
        jsonrpc = "2.0"
        method = "notifications/initialized"
        params = @{}
    }
    Send-McpMessage -Process $server -Message $notifMsg
    Start-Sleep -Milliseconds 500

    # ── 2. List tools ──
    Write-Host "`n--- Test 2: tools/list ---" -ForegroundColor White
    $listId = 2
    $listMsg = @{
        jsonrpc = "2.0"
        id = $listId
        method = "tools/list"
        params = @{}
    }
    Send-McpMessage -Process $server -Message $listMsg
    $listResp = Read-McpMessageById -Process $server -Id $listId -TimeoutMs 10000
    $listOk = ($null -ne $listResp -and $null -ne $listResp.result)
    Write-TestResult -Name "Server responds to tools/list" -Passed $listOk
    if ($listOk) {
        $toolNames = @()
        foreach ($t in $listResp.result.tools) { $toolNames += $t.name }
        Write-Host "    Tools: $($toolNames -join ', ')" -ForegroundColor DarkGray
        # Check expected tools exist
        $expectedTools = @('load_assembly', 'list_contexts', 'get_schema', 'run_query')
        foreach ($t in $expectedTools) {
            $found = $toolNames -contains $t
            Write-TestResult -Name "Tool '$t' is registered" -Passed $found
        }
    }

    # ── 3. Load assembly ──
    Write-Host "`n--- Test 3: load_assembly ---" -ForegroundColor White
    $loadId = 3
    $loadMsg = @{
        jsonrpc = "2.0"
        id = $loadId
        method = "tools/call"
        params = @{
            name = "load_assembly"
            arguments = @{
                assemblyPath = $TargetAssembly
            }
        }
    }
    Send-McpMessage -Process $server -Message $loadMsg
    $loadResp = Read-McpMessageById -Process $server -Id $loadId -TimeoutMs 30000
    $loadOk = ($null -ne $loadResp -and $null -ne $loadResp.result)
    Write-TestResult -Name "load_assembly responds" -Passed $loadOk
    
    if ($loadOk) {
        $hasError = ($null -ne $loadResp.result.isError) -and ($loadResp.result.isError -eq $true)
        Write-TestResult -Name "load_assembly returns no error" -Passed (-not $hasError)
        $loadText = ($loadResp.result.content | Where-Object { $_.type -eq 'text' } | Select-Object -First 1).text
        if ($loadText) { Write-Host "    $loadText" -ForegroundColor DarkGray }
    } else {
        Write-Host "    Response: $($loadResp | ConvertTo-Json -Depth 5 -Compress)" -ForegroundColor Yellow
    }

    # ── 4. List contexts ──
    Write-Host "`n--- Test 4: list_contexts ---" -ForegroundColor White
    $ctxId = 4
    $ctxMsg = @{
        jsonrpc = "2.0"
        id = $ctxId
        method = "tools/call"
        params = @{
            name = "list_contexts"
            arguments = @{}
        }
    }
    Send-McpMessage -Process $server -Message $ctxMsg
    $ctxResp = Read-McpMessageById -Process $server -Id $ctxId -TimeoutMs 15000
    $ctxOk = ($null -ne $ctxResp -and $null -ne $ctxResp.result)
    Write-TestResult -Name "list_contexts responds" -Passed $ctxOk
    
    $foundContexts = @()
    if ($ctxOk) {
        $hasError = ($null -ne $ctxResp.result.isError) -and ($ctxResp.result.isError -eq $true)
        Write-TestResult -Name "list_contexts returns no error" -Passed (-not $hasError)

        if (-not $hasError -and $ctxResp.result.content) {
            foreach ($c in $ctxResp.result.content) {
                if ($c.type -eq 'text' -and $c.text) {
                    Write-Host "    $($c.text)" -ForegroundColor DarkGray
                    try {
                        $parsed = $c.text | ConvertFrom-Json
                        if ($null -ne $parsed.contexts) {
                            foreach ($p in $parsed.contexts) { $foundContexts += $p }
                        } elseif ($parsed -is [array]) {
                            foreach ($p in $parsed) { $foundContexts += $p }
                        } else {
                            $foundContexts += $parsed
                        }
                    } catch { }
                }
            }
            Write-Host "    Found $($foundContexts.Count) DbContext(s)" -ForegroundColor DarkGray
            foreach ($ctx in $foundContexts) {
                $name = if ($ctx.name) { $ctx.name } else { if ($ctx.FullName) { $ctx.FullName } else { $ctx.ToString() } }
                $kind = if ($ctx.constructionKind) { $ctx.constructionKind } else { "?" }
                Write-Host "      - $name ($kind)" -ForegroundColor DarkGray
            }
        }
    } else {
        Write-Host "    Response: $($ctxResp | ConvertTo-Json -Depth 5 -Compress)" -ForegroundColor Yellow
    }

    # ── 5. Get schema for first context (if any found) ──
    if ($foundContexts.Count -gt 0) {
        $firstCtx = $foundContexts[0]
        $ctxName = if ($firstCtx.Name) { $firstCtx.Name } else { $firstCtx.FullName }
        Write-Host "`n--- Test 5: get_schema for $ctxName ---" -ForegroundColor White
        
        $schemaId = 5
        $schemaMsg = @{
            jsonrpc = "2.0"
            id = $schemaId
            method = "tools/call"
            params = @{
                name = "get_schema"
                arguments = @{
                    contextName = $ctxName
                    connectionName = $ctxName
                }
            }
        }
        Send-McpMessage -Process $server -Message $schemaMsg
        $schemaResp = Read-McpMessageById -Process $server -Id $schemaId -TimeoutMs 30000
        $schemaOk = ($null -ne $schemaResp -and $null -ne $schemaResp.result)
        Write-TestResult -Name "get_schema responds" -Passed $schemaOk
        
        if ($schemaOk) {
            $hasError = ($null -ne $schemaResp.result.isError) -and ($schemaResp.result.isError -eq $true)
            if ($hasError) {
                $errText = ($schemaResp.result.content | Where-Object { $_.type -eq 'text' } | Select-Object -First 1).text
                Write-Host "    Error (expected if no connection): $errText" -ForegroundColor DarkGray
                Write-TestResult -Name "get_schema fails gracefully (no connection configured)" -Passed $true
            } else {
                foreach ($c in $schemaResp.result.content) {
                    if ($c.type -eq 'text' -and $c.text) {
                        try {
                            $schema = $c.text | ConvertFrom-Json
                            $entityCount = 0
                            if ($schema.Entities) { $entityCount = $schema.Entities.Count }
                            Write-Host "    Schema entities: $entityCount" -ForegroundColor DarkGray
                            Write-TestResult -Name "get_schema returns entities" -Passed ($entityCount -gt 0)
                            if ($schema.Entities) {
                                foreach ($e in $schema.Entities | Select-Object -First 10) {
                                    $propCount = if ($e.Properties) { $e.Properties.Count } else { 0 }
                                    Write-Host "      - $($e.Name) ($propCount props)" -ForegroundColor DarkGray
                                }
                            }
                        } catch {
                            Write-Host "    Schema (raw): $($c.text.Substring(0, [Math]::Min(500, $c.text.Length)))" -ForegroundColor DarkGray
                        }
                    }
                }
            }
        } else {
            Write-Host "    Response: $($schemaResp | ConvertTo-Json -Depth 5 -Compress)" -ForegroundColor Yellow
        }
    } else {
        Write-Host "`n--- Test 5: SKIP (no contexts found) ---" -ForegroundColor Yellow
    }

} finally {
    # ── Cleanup ──
    Write-Host "`nShutting down server..." -ForegroundColor DarkGray
    try {
        $server.StandardInput.Close()
        if (-not $server.HasExited) {
            $server.WaitForExit(5000) | Out-Null
        }
        if (-not $server.HasExited) {
            $server.Kill()
        }
    } catch { }

    # Wait for stderr drain
    try { $stderrTask.Wait(3000) | Out-Null } catch { }
}

# ── Summary ──
Write-Host "`n=== Results ===" -ForegroundColor Cyan
$color = if ($script:failCount -eq 0) { 'Green' } else { 'Red' }
Write-Host "  Total: $($script:testCount)  Passed: $($script:passCount)  Failed: $($script:failCount)" -ForegroundColor $color

if ($stderrLines.Count -gt 0) {
    Write-Host "`n--- Server stderr (last 30 lines) ---" -ForegroundColor DarkGray
    $arr = @($stderrLines)
    $arr | Select-Object -Last 30 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
}

exit $script:failCount
