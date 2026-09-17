$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$executable = Join-Path $root 'dist\CodexClaudeUsage.exe'
$output = Join-Path $env:TEMP "Antigravity-snapshot-$PID.json"

try {
    Start-Process -FilePath $executable -ArgumentList @('--snapshot', $output) -WindowStyle Hidden -Wait
    if (-not (Test-Path -LiteralPath $output)) {
        throw 'Snapshot file was not created.'
    }

    $raw = [IO.File]::ReadAllText($output, [Text.Encoding]::UTF8)
    $snapshot = $raw | ConvertFrom-Json
    if (-not $snapshot.antigravity) {
        throw 'Antigravity section missing from snapshot.'
    }
    if (-not $snapshot.antigravity.available) {
        throw "Antigravity not available: $($snapshot.antigravity.error)"
    }
    if ($snapshot.antigravity.source -ne 'AntigravityServer') {
        throw "Antigravity source kind incorrect: $($snapshot.antigravity.source)"
    }
    if (@($snapshot.antigravity.buckets).Count -lt 4) {
        throw 'Antigravity must expose all four quota buckets (Gemini weekly/5h, third-party weekly/5h).'
    }
    if ($raw -match 'csrf[_-]?token|password|secret|authorization') {
        throw 'Snapshot contains leaked credentials or CSRF token.'
    }

    foreach ($bucket in @($snapshot.antigravity.buckets)) {
        if ([string]::IsNullOrWhiteSpace([string]$bucket.stableId)) {
            throw "Antigravity bucket missing stable ID: $($bucket.label)"
        }
        if (-not ($bucket.stableId -like 'antigravity:*')) {
            throw "Antigravity bucket stable ID format incorrect: $($bucket.stableId)"
        }
        if ([int]$bucket.windowDurationMinutes -le 0) {
            throw "Antigravity bucket windowDurationMinutes invalid: $($bucket.label)"
        }
        if ($bucket.usedPercent -lt 0 -or $bucket.usedPercent -gt 100) {
            throw "Antigravity bucket usedPercent out of range: $($bucket.label)"
        }
        if ($bucket.remainingPercent -lt 0 -or $bucket.remainingPercent -gt 100) {
            throw "Antigravity bucket remainingPercent out of range: $($bucket.label)"
        }
        if ([string]::IsNullOrWhiteSpace([string]$bucket.resetsAt)) {
            throw "Antigravity bucket resetsAt missing: $($bucket.label)"
        }
        if ([string]::IsNullOrWhiteSpace([string]$bucket.detail)) {
            throw "Antigravity bucket detail (covered models) missing: $($bucket.label)"
        }
        if (@('None', 'UpperBound', 'Cycle') -notcontains [string]$bucket.resetEstimate) {
            throw "Antigravity bucket resetEstimate invalid: $($bucket.resetEstimate)"
        }
        # 尚未使用的額度，官方回報的是「現在起算」的視窗結束點，每次採樣都會往後滑動，
        # 必須標記為推估上限，面板才不會顯示一個永遠不會到來的重設時刻。
        if ([double]$bucket.usedPercent -le 0.05 -and [string]$bucket.resetEstimate -ne 'UpperBound') {
            throw "Idle Antigravity bucket must be flagged as an upper-bound estimate: $($bucket.label)"
        }
        if ([double]$bucket.usedPercent -gt 0.05 -and [string]$bucket.resetEstimate -ne 'None') {
            throw "Active Antigravity bucket must use the official reset time: $($bucket.label)"
        }
        if ($null -eq $bucket.trendPointCount) {
            throw "Antigravity bucket trendPointCount missing: $($bucket.label)"
        }
    }

    $expected = @('antigravity:gemini-weekly', 'antigravity:gemini-5h', 'antigravity:3p-weekly', 'antigravity:3p-5h')
    foreach ($id in $expected) {
        if (-not (@($snapshot.antigravity.buckets) | Where-Object { $_.stableId -eq $id })) {
            throw "Antigravity snapshot missing expected bucket: $id"
        }
    }

    $historyPath = Join-Path $env:LOCALAPPDATA 'CodexClaudeUsage\antigravity-usage-history.json'
    if (Test-Path -LiteralPath $historyPath) {
        $history = [IO.File]::ReadAllText($historyPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
        if ([int]$history.version -lt 2) {
            throw "Antigravity history store must use the per-bucket v2 format, got version $($history.version)."
        }
        if ($null -eq $history.series) {
            throw 'Antigravity history store is missing the per-bucket series map.'
        }
    }

    [pscustomobject]@{
        AntigravityAvailable = $snapshot.antigravity.available
        AntigravitySource    = $snapshot.antigravity.source
        AntigravityStatus    = $snapshot.antigravity.status
        GeminiWeeklyUsed     = ($snapshot.antigravity.buckets | Where-Object { $_.stableId -eq 'antigravity:gemini-weekly' }).usedPercent
        Gemini5hUsed         = ($snapshot.antigravity.buckets | Where-Object { $_.stableId -eq 'antigravity:gemini-5h' }).usedPercent
        ThreePWeeklyUsed     = ($snapshot.antigravity.buckets | Where-Object { $_.stableId -eq 'antigravity:3p-weekly' }).usedPercent
        ThreeP5hUsed         = ($snapshot.antigravity.buckets | Where-Object { $_.stableId -eq 'antigravity:3p-5h' }).usedPercent
    } | Format-List

    Write-Host 'PASS: Antigravity connectivity, four RPC quotas, stable IDs, covered models, reset classification, per-bucket history, and credential isolation'
}
finally {
    Remove-Item -LiteralPath $output -Force -ErrorAction SilentlyContinue
}
