$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$executable = Join-Path $root 'dist\CodexClaudeUsage.exe'
$historyPath = Join-Path $env:TEMP "CodexClaudeUsage-estimate-history-$PID.json"
$snapshotPath = Join-Path $env:TEMP "CodexClaudeUsage-estimate-snapshot-$PID.json"
$env:CODEX_CLAUDE_USAGE_CLAUDE_HISTORY_PATH = $historyPath
$env:CODEX_CLAUDE_USAGE_BRIDGE_PATH = Join-Path $env:TEMP "CodexClaudeUsage-estimate-none-$PID.json"

function Ms([DateTimeOffset]$value) { return $value.ToUnixTimeMilliseconds() }

try {
    $now = [DateTimeOffset]::UtcNow
    $samples = @(
        @{ t = Ms $now.AddDays(-2).AddMinutes(-20); u = @{ fh = 0; sd = 60 } },
        @{ t = Ms $now.AddDays(-2);                 u = @{ fh = 0; sd = 3 } },
        @{ t = Ms $now.AddMinutes(-40);             u = @{ fh = 3; sd = 8 } },
        @{ t = Ms $now.AddMinutes(-25);             u = @{ fh = 6; sd = 9 } },
        @{ t = Ms $now.AddMinutes(-10);             u = @{ fh = 8; sd = 10 } }
    )
    $record = @{ version = 2; samples = $samples } | ConvertTo-Json -Depth 8 -Compress
    [IO.File]::WriteAllText($historyPath, $record)

    Start-Process -FilePath $executable -ArgumentList @('--snapshot', $snapshotPath) -WindowStyle Hidden -Wait
    $snapshot = [IO.File]::ReadAllText($snapshotPath, [Text.Encoding]::UTF8) | ConvertFrom-Json

    $session = $snapshot.claude.buckets | Where-Object label -eq '目前工作階段'
    if ($session.resetEstimate -ne 'UpperBound') { throw "5 小時視窗未產生上界推估：$($session.resetEstimate)" }
    $sessionReset = [DateTimeOffset]::Parse($session.resetsAt)
    $expectedSession = $now.AddMinutes(-40).AddHours(5)
    if ([Math]::Abs(($sessionReset - $expectedSession).TotalMinutes) -gt 3) { throw "5 小時上界時間偏差過大：$sessionReset" }

    $weekly = $snapshot.claude.buckets | Where-Object label -eq '所有模型 · 每週'
    if ($weekly.resetEstimate -ne 'Cycle') { throw "每週視窗未產生週期推估：$($weekly.resetEstimate)" }
    $weeklyReset = [DateTimeOffset]::Parse($weekly.resetsAt)
    $expectedWeekly = $now.AddDays(-2).AddMinutes(-10).AddDays(7)
    if ([Math]::Abs(($weeklyReset - $expectedWeekly).TotalMinutes) -gt 10) { throw "每週推估時間偏差過大：$weeklyReset" }

    Write-Host 'PASS: desktop history reset estimation (five-hour upper bound + weekly cycle)'
}
finally {
    Remove-Item Env:\CODEX_CLAUDE_USAGE_CLAUDE_HISTORY_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:\CODEX_CLAUDE_USAGE_BRIDGE_PATH -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $historyPath, $snapshotPath -Force -ErrorAction SilentlyContinue
}
