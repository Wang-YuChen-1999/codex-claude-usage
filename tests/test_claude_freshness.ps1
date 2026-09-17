$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$executable = Join-Path $root 'dist\CodexClaudeUsage.exe'
$historyPath = Join-Path $env:TEMP "CodexClaudeUsage-history-$PID.json"
$bridgePath = Join-Path $env:TEMP "CodexClaudeUsage-bridge-source-$PID.json"
$snapshotPath = Join-Path $env:TEMP "CodexClaudeUsage-freshness-$PID.json"
$env:CODEX_CLAUDE_USAGE_CLAUDE_HISTORY_PATH = $historyPath
$env:CODEX_CLAUDE_USAGE_BRIDGE_PATH = $bridgePath

function Write-History([DateTimeOffset]$capturedAt, [double]$fiveHour, [double]$sevenDay) {
    $record = @{
        version = 2
        samples = @(@{
            t = $capturedAt.ToUnixTimeMilliseconds()
            u = @{ fh = $fiveHour; sd = $sevenDay }
        })
    } | ConvertTo-Json -Depth 8 -Compress
    [IO.File]::WriteAllText($historyPath, $record)
}

function Write-Bridge([DateTimeOffset]$capturedAt, [double]$fiveHour, [double]$sevenDay) {
    $record = @{
        capturedAt = $capturedAt.ToString('o')
        rate_limits = @{
            five_hour = @{ used_percentage = $fiveHour; resets_at = [DateTimeOffset]::UtcNow.AddHours(3).ToUnixTimeSeconds() }
            seven_day = @{ used_percentage = $sevenDay; resets_at = [DateTimeOffset]::UtcNow.AddDays(3).ToUnixTimeSeconds() }
        }
    } | ConvertTo-Json -Depth 8 -Compress
    [IO.File]::WriteAllText($bridgePath, $record)
}

function Write-BridgeRaw($rateLimits, [DateTimeOffset]$capturedAt) {
    $record = @{
        capturedAt = $capturedAt.ToString('o')
        rate_limits = $rateLimits
    } | ConvertTo-Json -Depth 8 -Compress
    [IO.File]::WriteAllText($bridgePath, $record)
}

function Read-ClaudeSnapshot {
    Start-Process -FilePath $executable -ArgumentList @('--snapshot', $snapshotPath) -WindowStyle Hidden -Wait
    $snapshot = [IO.File]::ReadAllText($snapshotPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    [IO.File]::Delete($snapshotPath)
    return $snapshot.claude
}

try {
    Write-History ([DateTimeOffset]::UtcNow.AddMinutes(-5)) 10 40
    Write-Bridge ([DateTimeOffset]::UtcNow) 22 67
    $newBridge = Read-ClaudeSnapshot
    if (($newBridge.buckets | Where-Object label -eq '所有模型 · 每週').usedPercent -ne 67) {
        throw '較新的 statusLine 沒有優先於 Desktop 快取。'
    }

    Write-History ([DateTimeOffset]::UtcNow) 10 40
    Write-Bridge ([DateTimeOffset]::UtcNow.AddHours(-2)) 22 67
    $newHistory = Read-ClaudeSnapshot
    $weekly = $newHistory.buckets | Where-Object label -eq '所有模型 · 每週'
    if ($weekly.usedPercent -ne 40 -or -not $weekly.resetsAt) {
        throw '較新的 Desktop 百分比或仍有效的 bridge 重設時間合併錯誤。'
    }

    Write-Bridge ([DateTimeOffset]::UtcNow.AddHours(-25)) 22 67
    $expiredBridge = Read-ClaudeSnapshot
    $expiredWeekly = $expiredBridge.buckets | Where-Object label -eq '所有模型 · 每週'
    if ($expiredWeekly.usedPercent -ne 40 -or $expiredWeekly.resetsAt) {
        throw '超過 24 小時的 bridge 應完全失效。'
    }

    Write-History ([DateTimeOffset]::UtcNow) 10 40
    Write-BridgeRaw @{
        five_hour = @{ resets_at = [DateTimeOffset]::UtcNow.AddHours(3).ToUnixTimeSeconds() }
        seven_day = @{ resets_at = [DateTimeOffset]::UtcNow.AddDays(3).ToUnixTimeSeconds() }
    } ([DateTimeOffset]::UtcNow)
    $missingUsage = Read-ClaudeSnapshot
    $missingWeekly = $missingUsage.buckets | Where-Object label -eq '所有模型 · 每週'
    if ($missingWeekly.usedPercent -ne 40 -or -not $missingWeekly.resetsAt) {
        throw '缺少 used_percentage 的 bridge 不應把現有百分比覆寫成 0。'
    }

    Write-Host 'PASS: Claude source precedence and 24-hour bridge expiry'
}
finally {
    Remove-Item Env:\CODEX_CLAUDE_USAGE_CLAUDE_HISTORY_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:\CODEX_CLAUDE_USAGE_BRIDGE_PATH -ErrorAction SilentlyContinue
    [IO.File]::Delete($historyPath)
    [IO.File]::Delete($bridgePath)
    [IO.File]::Delete($snapshotPath)
}
