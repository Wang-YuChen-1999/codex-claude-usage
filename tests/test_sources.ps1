$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$executable = Join-Path $root 'dist\CodexClaudeUsage.exe'
$output = Join-Path $env:TEMP "CodexClaudeUsage-snapshot-$PID.json"

try {
    Start-Process -FilePath $executable -ArgumentList @('--snapshot', $output) -WindowStyle Hidden -Wait
    if (-not (Test-Path -LiteralPath $output)) {
        throw '快照檔未建立。'
    }

    $raw = [IO.File]::ReadAllText($output, [Text.Encoding]::UTF8)
    $snapshot = $raw | ConvertFrom-Json
    if (-not $snapshot.codex.available) { throw "Codex 無法讀取：$($snapshot.codex.error)" }
    if (-not $snapshot.claude.available) { throw "Claude 無法讀取：$($snapshot.claude.error)" }
    if (@($snapshot.codex.buckets).Count -lt 1) { throw 'Codex 沒有用量視窗。' }
    if (@($snapshot.claude.buckets).Count -lt 1) { throw 'Claude 沒有用量視窗。' }
    if ($raw -match 'access[_-]?token|refresh[_-]?token|oauth_token') { throw '快照不應包含憑證欄位。' }

    if ($snapshot.codex.status -like '*官方 app-server*') {
        if ($snapshot.codex.source -ne 'CodexAppServer') {
            throw "官方 Codex 快照來源類型錯誤：$($snapshot.codex.source)"
        }
        foreach ($bucket in @($snapshot.codex.buckets)) {
            if ([string]::IsNullOrWhiteSpace([string]$bucket.stableId)) {
                throw "官方 Codex 額度缺少 stable ID：$($bucket.label)"
            }
            if ([int]$bucket.windowDurationMinutes -le 0) {
                throw "官方 Codex 額度缺少視窗週期：$($bucket.label)"
            }
        }
    }

    foreach ($bucket in @($snapshot.codex.buckets) + @($snapshot.claude.buckets)) {
        if ($bucket.usedPercent -lt 0 -or $bucket.usedPercent -gt 100) {
            throw "百分比超出範圍：$($bucket.label)"
        }
    }

    [pscustomobject]@{
        Codex = ($snapshot.codex.buckets | Select-Object -First 1).usedPercent
        ClaudeWeekly = ($snapshot.claude.buckets | Where-Object { $_.label -like '*每週*' } | Select-Object -First 1).usedPercent
        CodexSource = $snapshot.codex.status
        ClaudeSource = $snapshot.claude.status
    } | Format-List
    Write-Host 'PASS: providers, ranges, stable Codex reset identity, reset payload, and credential exclusion'
}
finally {
    Remove-Item -LiteralPath $output -Force -ErrorAction SilentlyContinue
}
