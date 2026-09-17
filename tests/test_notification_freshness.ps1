$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assemblyPath = Join-Path $root 'dist\CodexClaudeUsage.exe'
$assembly = [Reflection.Assembly]::LoadFile($assemblyPath)
$providerType = $assembly.GetType('CodexClaudeUsage.ProviderSnapshot', $true)
$trayType = $assembly.GetType('CodexClaudeUsage.TrayApplication', $true)
$flags = [Reflection.BindingFlags]'Static,Public,NonPublic'
$isFresh = $trayType.GetMethod('IsFreshForNotifications', $flags)

function New-Provider([DateTimeOffset] $UpdatedAt, [bool] $Available = $true) {
    $provider = [Activator]::CreateInstance($providerType, @('Test'))
    $providerType.GetProperty('IsAvailable').SetValue($provider, $Available, $null)
    $providerType.GetProperty('UpdatedAt').SetValue($provider, $UpdatedAt, $null)
    return $provider
}

function Check([object] $Provider) {
    return [bool]$isFresh.Invoke($null, @($Provider))
}

$now = [DateTimeOffset]::UtcNow
if (-not (Check (New-Provider $now))) { throw 'A current snapshot must be notification-eligible.' }
if (Check (New-Provider $now.AddMinutes(-61))) { throw 'A stale snapshot must not trigger notifications.' }
if (Check (New-Provider $now.AddMinutes(6))) { throw 'A far-future snapshot must not trigger notifications.' }
if (Check (New-Provider $now $false)) { throw 'An unavailable snapshot must not trigger notifications.' }
if (Check (New-Provider ([DateTimeOffset]::MinValue))) { throw 'A snapshot without a timestamp must not trigger notifications.' }

Write-Host 'PASS: notification freshness gating rejects stale, future, unavailable, and untimed snapshots'
