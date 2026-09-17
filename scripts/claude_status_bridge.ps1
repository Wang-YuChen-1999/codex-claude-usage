$ErrorActionPreference = 'SilentlyContinue'

$raw = [Console]::In.ReadToEnd()
$payload = $raw | ConvertFrom-Json
$rateLimits = $payload.rate_limits
$modelName = $payload.model.display_name
$contextUsed = $payload.context_window.used_percentage

if ($rateLimits) {
    $cache = if ($env:CODEX_CLAUDE_USAGE_CACHE_PATH) {
        [IO.Path]::GetFullPath($env:CODEX_CLAUDE_USAGE_CACHE_PATH)
    }
    else {
        Join-Path $env:LOCALAPPDATA 'CodexClaudeUsage\claude-status.json'
    }
    $directory = Split-Path -Parent $cache
    $temporary = "$cache.tmp"
    New-Item -ItemType Directory -Force -Path $directory | Out-Null

    $record = [ordered]@{
        version = 1
        capturedAt = [DateTimeOffset]::UtcNow.ToString('o')
        source = 'claude-code-statusline'
        rate_limits = $rateLimits
    }
    $json = $record | ConvertTo-Json -Depth 12 -Compress
    [IO.File]::WriteAllText($temporary, $json, (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporary -Destination $cache -Force
}

$parts = [Collections.Generic.List[string]]::new()
if ($modelName) { $parts.Add("Claude $modelName") }
if ($rateLimits.five_hour) { $parts.Add(('5h {0:0}%' -f $rateLimits.five_hour.used_percentage)) }
if ($rateLimits.seven_day) { $parts.Add(('7d {0:0}%' -f $rateLimits.seven_day.used_percentage)) }
if (-not $rateLimits -and $null -ne $contextUsed) { $parts.Add(('context {0:0}%' -f $contextUsed)) }

if ($parts.Count -gt 0) {
    [Console]::Write(($parts -join ' | '))
}
