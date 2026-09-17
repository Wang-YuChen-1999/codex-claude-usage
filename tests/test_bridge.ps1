$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$bridge = Join-Path $root 'scripts\claude_status_bridge.ps1'
$output = Join-Path $env:TEMP "CodexClaudeUsage-bridge-$PID.json"
$env:CODEX_CLAUDE_USAGE_CACHE_PATH = $output

$payload = @{
    model = @{ display_name = 'Test Model' }
    context_window = @{ used_percentage = 17 }
    rate_limits = @{
        five_hour = @{ used_percentage = 12; resets_at = 1787558400 }
        seven_day = @{ used_percentage = 52; resets_at = 1788163200 }
    }
    sensitive_test_value = 'must-not-be-written'
} | ConvertTo-Json -Depth 8 -Compress

try {
    $status = $payload | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $bridge
    $raw = Get-Content -LiteralPath $output -Raw
    $cache = $raw | ConvertFrom-Json
    if ($cache.rate_limits.seven_day.used_percentage -ne 52) { throw '七日用量未正確保存。' }
    if ($raw.Contains('sensitive_test_value') -or $raw.Contains('must-not-be-written')) { throw '橋接器保存了不必要的輸入欄位。' }
    if ($status -notmatch '5h 12%' -or $status -notmatch '7d 52%') { throw '狀態列文字不正確。' }
    Write-Host "Status: $status"
    Write-Host 'PASS: rate limit bridge and data minimization'
}
finally {
    Remove-Item Env:\CODEX_CLAUDE_USAGE_CACHE_PATH -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $output -Force -ErrorAction SilentlyContinue
}
