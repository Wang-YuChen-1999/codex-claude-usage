$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$executable = Join-Path $root 'dist\CodexClaudeUsage.exe'
$historyPath = Join-Path $env:TEMP "CodexClaudeUsage-proj-history-$PID.json"
$codexHistoryPath = Join-Path $env:TEMP "CodexClaudeUsage-proj-codex-$PID.json"
$snapshotPath = Join-Path $env:TEMP "CodexClaudeUsage-proj-snapshot-$PID.json"
$staleSnapshotPath = Join-Path $env:TEMP "CodexClaudeUsage-proj-stale-$PID.json"
$env:CODEX_CLAUDE_USAGE_CLAUDE_HISTORY_PATH = $historyPath
$env:CODEX_CLAUDE_USAGE_CODEX_HISTORY_PATH = $codexHistoryPath
$env:CODEX_CLAUDE_USAGE_BRIDGE_PATH = Join-Path $env:TEMP "CodexClaudeUsage-proj-none-$PID.json"

function Ms([DateTimeOffset]$value) { return $value.ToUnixTimeMilliseconds() }

try {
    $now = [DateTimeOffset]::UtcNow
    # Claude Desktop 歷史：3 天前每週重設，之後以約 1%/h 穩定累積至 68%。
    $samples = @(
        @{ t = Ms $now.AddDays(-3).AddMinutes(-15); u = @{ fh = 0; sd = 70 } },
        @{ t = Ms $now.AddDays(-3);                 u = @{ fh = 0; sd = 2 } },
        @{ t = Ms $now.AddHours(-48);               u = @{ fh = 0; sd = 20 } },
        @{ t = Ms $now.AddHours(-36);               u = @{ fh = 0; sd = 32 } },
        @{ t = Ms $now.AddHours(-24);               u = @{ fh = 0; sd = 44 } },
        @{ t = Ms $now.AddHours(-12);               u = @{ fh = 0; sd = 56 } },
        @{ t = Ms $now.AddMinutes(-10);             u = @{ fh = 5; sd = 68 } }
    )
    $record = @{ version = 2; samples = $samples } | ConvertTo-Json -Depth 8 -Compress
    [IO.File]::WriteAllText($historyPath, $record)

    # Codex 本機歷史：兩筆舊快照，讀取時 append 現值後應足以產生趨勢。
    $codexRecord = @{ version = 1; samples = @(
        @{ t = Ms $now.AddHours(-24); u = 1 },
        @{ t = Ms $now.AddHours(-12); u = 2 }
    ) } | ConvertTo-Json -Depth 6 -Compress
    [IO.File]::WriteAllText($codexHistoryPath, $codexRecord)

    Start-Process -FilePath $executable -ArgumentList @('--snapshot', $snapshotPath) -WindowStyle Hidden -Wait
    $snapshot = [IO.File]::ReadAllText($snapshotPath, [Text.Encoding]::UTF8) | ConvertFrom-Json

    $weekly = $snapshot.claude.buckets | Where-Object label -eq '所有模型 · 每週'
    if ($weekly.trendPointCount -lt 2) { throw "Claude 每週趨勢未生成：$($weekly.trendPointCount)" }
    if (-not $weekly.projectedExhaustAt) { throw 'Claude 每週未產生耗盡預測。' }
    $projected = [DateTimeOffset]::Parse($weekly.projectedExhaustAt)
    $lastT = $now.AddMinutes(-10)
    $slope = (68.0 - 20.0) / ($lastT - $now.AddHours(-48)).TotalMilliseconds
    $expected = $lastT.AddMilliseconds((100.0 - 68.0) / $slope)
    if ([Math]::Abs(($projected - $expected).TotalMinutes) -gt 60) { throw "耗盡預測偏差過大：$projected（預期 $expected）" }
    $resets = [DateTimeOffset]::Parse($weekly.resetsAt)
    if ($projected -ge $resets) { throw '預測時間不應晚於重設時間仍被設定。' }

    $codexWeekly = $snapshot.codex.buckets | Where-Object label -eq '每週額度'
    if ($codexWeekly -and $codexWeekly.trendPointCount -lt 2) { throw "Codex 每週趨勢未生成：$($codexWeekly.trendPointCount)" }
    $stored = [IO.File]::ReadAllText($codexHistoryPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($codexWeekly -and @($stored.samples).Count -lt 3) { throw 'Codex 歷史存放區未寫入新快照。' }

    # A positive trend whose newest sample is stale must not be presented as a current exhaustion forecast.
    $staleSamples = @(
        @{ t = Ms $now.AddHours(-27); u = @{ fh = 0; sd = 40 } },
        @{ t = Ms $now.AddHours(-15); u = @{ fh = 0; sd = 60 } },
        @{ t = Ms $now.AddHours(-3);  u = @{ fh = 0; sd = 80 } }
    )
    $staleRecord = @{ version = 2; samples = $staleSamples } | ConvertTo-Json -Depth 8 -Compress
    [IO.File]::WriteAllText($historyPath, $staleRecord)
    Start-Process -FilePath $executable -ArgumentList @('--snapshot', $staleSnapshotPath) -WindowStyle Hidden -Wait
    $staleSnapshot = [IO.File]::ReadAllText($staleSnapshotPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    $staleWeekly = $staleSnapshot.claude.buckets | Where-Object label -eq '所有模型 · 每週'
    if ($staleWeekly.projectedExhaustAt) {
        throw "陳舊 Claude 樣本不應產生即時耗盡預測：$($staleWeekly.projectedExhaustAt)"
    }

    Write-Host 'PASS: usage trend sparkline data, fresh exhaustion projection, and stale-data rejection'
}
finally {
    Remove-Item Env:\CODEX_CLAUDE_USAGE_CLAUDE_HISTORY_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:\CODEX_CLAUDE_USAGE_CODEX_HISTORY_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:\CODEX_CLAUDE_USAGE_BRIDGE_PATH -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $historyPath, $codexHistoryPath, $snapshotPath, $staleSnapshotPath -Force -ErrorAction SilentlyContinue
}
