$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assemblyPath = Join-Path $root 'dist\CodexClaudeUsage.exe'
if (-not (Test-Path -LiteralPath $assemblyPath)) {
    throw "Build output not found: $assemblyPath"
}

$preferencesPath = Join-Path $env:TEMP "CodexClaudeUsage-preferences-$PID.json"
$env:CODEX_CLAUDE_USAGE_PREFERENCES_PATH = $preferencesPath
$assembly = [Reflection.Assembly]::LoadFile($assemblyPath)
$type = $assembly.GetType('CodexClaudeUsage.UserPreferences', $true)
$kindType = $assembly.GetType('CodexClaudeUsage.UsageNotificationKind', $true)

function New-Preferences {
    return [Activator]::CreateInstance($type, $true)
}

function Set-Property([object]$target, [string]$name, [object]$value) {
    $type.GetProperty($name).SetValue($target, $value, $null)
}

function Get-Property([object]$target, [string]$name) {
    return $type.GetProperty($name).GetValue($target, $null)
}

function Assert-Equal([object]$actual, [object]$expected, [string]$message) {
    if ($actual -ne $expected) { throw "$message Expected=$expected Actual=$actual" }
}

try {
    $defaults = $type.GetMethod('Load').Invoke($null, @())
    foreach ($name in 'TotalOnly', 'NotifyThresholds', 'NotifyExhaustion', 'NotifyReset', 'NotifyCodex', 'NotifyClaude', 'GlobalHotkeysEnabled') {
        Assert-Equal (Get-Property $defaults $name) $true "Default $name is incorrect."
    }
    Assert-Equal (Get-Property $defaults 'QuietHoursEnabled') $false 'Quiet hours must be disabled by default.'
    Assert-Equal (Get-Property $defaults 'QuietStartMinutes') 1320 'Default quiet start is incorrect.'
    Assert-Equal (Get-Property $defaults 'QuietEndMinutes') 480 'Default quiet end is incorrect.'
    Assert-Equal (Get-Property $defaults 'HistoryRetentionDays') 30 'Default history retention is incorrect.'

    Set-Property $defaults 'TotalOnly' $false
    Set-Property $defaults 'NotifyReset' $false
    Set-Property $defaults 'NotifyClaude' $false
    Set-Property $defaults 'QuietHoursEnabled' $true
    Set-Property $defaults 'QuietStartMinutes' 1380
    Set-Property $defaults 'QuietEndMinutes' 360
    Set-Property $defaults 'HistoryRetentionDays' 7
    $type.GetMethod('Save').Invoke($defaults, @())
    $loaded = $type.GetMethod('Load').Invoke($null, @())
    Assert-Equal (Get-Property $loaded 'TotalOnly') $false 'Saved TotalOnly did not persist.'
    Assert-Equal (Get-Property $loaded 'NotifyReset') $false 'Saved reset preference did not persist.'
    Assert-Equal (Get-Property $loaded 'NotifyClaude') $false 'Saved Claude preference did not persist.'
    Assert-Equal (Get-Property $loaded 'HistoryRetentionDays') 7 'Saved retention did not persist.'

    [IO.File]::WriteAllText($preferencesPath, '{not valid json')
    $corrupt = $type.GetMethod('Load').Invoke($null, @())
    Assert-Equal (Get-Property $corrupt 'TotalOnly') $true 'Corrupt preferences must return defaults.'

    $quiet = New-Preferences
    Set-Property $quiet 'QuietHoursEnabled' $true
    Set-Property $quiet 'QuietStartMinutes' 1320
    Set-Property $quiet 'QuietEndMinutes' 480
    $isQuiet = $type.GetMethod('IsQuietTime')
    if (-not [bool]$isQuiet.Invoke($quiet, @([DateTimeOffset]::new(2026, 8, 26, 23, 0, 0, [TimeSpan]::Zero)))) { throw '23:00 must be quiet for an overnight range.' }
    if (-not [bool]$isQuiet.Invoke($quiet, @([DateTimeOffset]::new(2026, 8, 27, 7, 59, 0, [TimeSpan]::Zero)))) { throw '07:59 must be quiet for an overnight range.' }
    if ([bool]$isQuiet.Invoke($quiet, @([DateTimeOffset]::new(2026, 8, 27, 8, 0, 0, [TimeSpan]::Zero)))) { throw '08:00 must be outside the quiet range.' }

    $allows = $type.GetMethod('Allows')
    $now = [DateTimeOffset]::new(2026, 8, 26, 12, 0, 0, [TimeSpan]::Zero)
    $threshold = [Enum]::Parse($kindType, 'Threshold')
    $exhaustion = [Enum]::Parse($kindType, 'Exhaustion')
    $reset = [Enum]::Parse($kindType, 'Reset')
    Set-Property $quiet 'QuietHoursEnabled' $false
    if (-not [bool]$allows.Invoke($quiet, @($threshold, 'Codex', $now, $true))) { throw 'Enabled Codex threshold must be allowed.' }
    Set-Property $quiet 'NotifyCodex' $false
    if ([bool]$allows.Invoke($quiet, @($threshold, 'Codex', $now, $true))) { throw 'Disabled Codex provider must be blocked.' }
    Set-Property $quiet 'NotifyCodex' $true
    Set-Property $quiet 'NotifyClaude' $false
    if ([bool]$allows.Invoke($quiet, @($exhaustion, 'Claude Code', $now, $true))) { throw 'Disabled Claude provider must be blocked.' }
    Set-Property $quiet 'NotifyClaude' $true
    Set-Property $quiet 'NotifyReset' $false
    if ([bool]$allows.Invoke($quiet, @($reset, 'Claude Code', $now, $true))) { throw 'Disabled notification kind must be blocked.' }
    if ([bool]$allows.Invoke($quiet, @($threshold, 'Codex', $now, $false))) { throw 'Global notifications must gate all notifications.' }
    Set-Property $quiet 'HistoryRetentionDays' 5
    Assert-Equal (Get-Property $quiet 'HistoryRetentionDays') 30 'Unsupported history retention must reset to 30 days.'

    Write-Host 'PASS: user preference persistence, corruption recovery, quiet hours, and notification gating'
}
finally {
    Remove-Item Env:\CODEX_CLAUDE_USAGE_PREFERENCES_PATH -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $preferencesPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath "$preferencesPath.tmp" -Force -ErrorAction SilentlyContinue
}
