$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$executable = Join-Path $root 'dist\CodexClaudeUsage.exe'
$assembly = [Reflection.Assembly]::LoadFile($executable)
$sourceType = $assembly.GetType('CodexClaudeUsage.ClaudeUsageSource', $true)
$source = [Activator]::CreateInstance($sourceType, $true)
$readMethod = $sourceType.GetMethod('Read', [Reflection.BindingFlags]'Instance,NonPublic')
$historyPath = Join-Path $env:TEMP "CodexClaudeUsage-history-$PID.json"
$bridgePath = Join-Path $env:TEMP "CodexClaudeUsage-bridge-source-$PID.json"
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
    return $readMethod.Invoke($source, @())
}

function Assert-Captured($snapshot, [DateTimeOffset]$expected, [string]$message) {
    if ($snapshot.UpdatedAt.ToUnixTimeMilliseconds() -ne $expected.ToUnixTimeMilliseconds()) {
        throw $message
    }
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

    # 無用量或部分更新不能替尚未更新的百分比宣告新鮮。
    $historyTime = [DateTimeOffset]::UtcNow.AddHours(-3)
    $bridgeTime = [DateTimeOffset]::UtcNow
    Write-History $historyTime 10 40
    Write-BridgeRaw @{
        five_hour = @{ resets_at = $bridgeTime.AddHours(3).ToUnixTimeSeconds() }
        seven_day = @{ resets_at = $bridgeTime.AddDays(3).ToUnixTimeSeconds() }
    } $bridgeTime
    $resetOnly = Read-ClaudeSnapshot
    Assert-Captured $resetOnly $historyTime 'Reset-only bridge made old usage look fresh.'
    if (-not $resetOnly.Buckets[0].ResetsAt) { throw 'Reset-only bridge should still supply a known reset.' }

    Write-BridgeRaw @{} $bridgeTime
    Assert-Captured (Read-ClaudeSnapshot) $historyTime 'Empty bridge made old usage look fresh.'

    foreach ($invalid in @($null, $true, $false, -1, 101, 'NaN', 'Infinity', '-Infinity', 'not-a-number')) {
        Write-BridgeRaw @{ five_hour = @{ used_percentage = $invalid } } $bridgeTime
        $invalidSnapshot = Read-ClaudeSnapshot
        Assert-Captured $invalidSnapshot $historyTime "Invalid bridge value advanced timestamp: $invalid"
        if ($invalidSnapshot.Buckets[0].UsedPercent -ne 10) { throw "Invalid bridge value replaced real usage: $invalid" }
    }

    Write-BridgeRaw @{ five_hour = @{ used_percentage = 22 } } $bridgeTime
    $partial = Read-ClaudeSnapshot
    if ($partial.Buckets[0].UsedPercent -ne 22 -or $partial.Buckets[1].UsedPercent -ne 40) {
        throw 'Partial bridge should only update its own bucket.'
    }
    Assert-Captured $partial $historyTime 'Partial bridge refreshed an untouched weekly bucket.'

    Write-Bridge $bridgeTime 0 100
    $complete = Read-ClaudeSnapshot
    Assert-Captured $complete $bridgeTime 'Complete valid bridge did not update the source timestamp.'
    if ($complete.Buckets[0].UsedPercent -ne 0 -or $complete.Buckets[1].UsedPercent -ne 100) {
        throw 'Valid zero and 100 percent values must be accepted.'
    }

    $olderTime = $historyTime.AddHours(-1)
    Write-BridgeRaw @{ seven_day_opus = @{ used_percentage = 32 } } $olderTime
    $olderAdditional = Read-ClaudeSnapshot
    if ($olderAdditional.Buckets.Count -ne 3) { throw 'Older bridge should still supply an absent usage bucket.' }
    Assert-Captured $olderAdditional $olderTime 'Mixed sources must retain the oldest adopted usage timestamp.'

    [IO.File]::Delete($historyPath)
    Write-BridgeRaw @{ five_hour = @{ used_percentage = 'NaN' } } $bridgeTime
    $invalidOnly = Read-ClaudeSnapshot
    if ($invalidOnly.IsAvailable -or $invalidOnly.Buckets.Count -ne 0) { throw 'Invalid bridge alone must not create fake available usage.' }
    Assert-Captured $invalidOnly ([DateTimeOffset]::MinValue) 'Invalid bridge alone must not create a capture timestamp.'

    Write-BridgeRaw @{ five_hour = @{ usedPercent = 12 } } $bridgeTime
    $bridgeOnly = Read-ClaudeSnapshot
    if (-not $bridgeOnly.IsAvailable -or $bridgeOnly.Buckets[0].UsedPercent -ne 12) { throw 'Valid bridge alone should be available.' }
    Assert-Captured $bridgeOnly $bridgeTime 'Valid bridge alone should retain its actual capture timestamp.'

    Write-Host 'PASS: Claude source precedence, conservative freshness, valid percentages and 24-hour expiry (offline fixtures)'
}
finally {
    Remove-Item Env:\CODEX_CLAUDE_USAGE_CLAUDE_HISTORY_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:\CODEX_CLAUDE_USAGE_BRIDGE_PATH -ErrorAction SilentlyContinue
    [IO.File]::Delete($historyPath)
    [IO.File]::Delete($bridgePath)
}
