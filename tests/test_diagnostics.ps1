$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assembly = [Reflection.Assembly]::LoadFile((Join-Path $root 'dist\CodexClaudeUsage.exe'))
$flags = [Reflection.BindingFlags]'Static,Public,NonPublic'

function New-Instance([string] $TypeName, [object[]] $Args = @()) {
    $type = $assembly.GetType("CodexClaudeUsage.$TypeName", $true)
    return [Activator]::CreateInstance($type, $Args)
}

function Invoke-Static([string] $TypeName, [string] $MethodName, [object[]] $Arguments = @()) {
    $type = $assembly.GetType("CodexClaudeUsage.$TypeName", $true)
    $invokeArguments = [object[]]@($Arguments)
    $method = @($type.GetMethods($flags) | Where-Object {
        $_.Name -eq $MethodName -and $_.GetParameters().Count -eq $invokeArguments.Count
    }) | Select-Object -First 1
    if ($null -eq $method) {
        throw "Static method $TypeName.$MethodName with $($invokeArguments.Count) arguments was not found."
    }
    return $method.Invoke($null, $invokeArguments)
}

function Set-Property([object] $Value, [string] $Name, [object] $PropertyValue) {
    $Value.GetType().GetProperty($Name).SetValue($Value, $PropertyValue, $null)
}

$settings = Join-Path $env:TEMP "CodexClaudeUsage-diagnostics-settings-$PID.json"
$bridge = Join-Path $env:TEMP "CodexClaudeUsage-diagnostics-bridge-$PID.ps1"
$bridgeData = Join-Path $env:TEMP "CodexClaudeUsage-diagnostics-bridge-data-$PID.json"
$local = Join-Path $env:TEMP "CodexClaudeUsage-diagnostics-local-$PID"
$outside = Join-Path $env:TEMP "CodexClaudeUsage-diagnostics-outside-$PID.json"
$env:CODEX_CLAUDE_USAGE_SETTINGS_PATH = $settings
$env:CODEX_CLAUDE_USAGE_BRIDGE_INSTALL_PATH = $bridge
$env:CODEX_CLAUDE_USAGE_BRIDGE_PATH = $bridgeData
$env:CODEX_CLAUDE_USAGE_LOCAL_DATA_DIR = $local

try {
    Remove-Item -LiteralPath $settings, $bridge, $bridgeData, $outside -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $local -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $local | Out-Null

    # Diagnostics only include sanitized health, total bucket data, and safe configuration values.
    $snapshot = New-Instance 'UsageSnapshot'
    $codex = $snapshot.Codex
    Set-Property $codex 'IsAvailable' $true
    Set-Property $codex 'UpdatedAt' ([DateTimeOffset]::UtcNow)
    Set-Property $codex 'Status' 'token=super-secret-value C:\private\codex.exe 10.0.0.8'
    Set-Property $codex 'Error' 'response: private prompt payload user@example.test'
    $bucket = New-Instance 'UsageBucket'
    $weeklyLabel = ([string][char]0x6BCF) + ([string][char]0x9031)
    Set-Property $bucket 'Label' $weeklyLabel
    Set-Property $bucket 'WindowDurationMinutes' 10080
    Set-Property $bucket 'UsedPercent' 44
    Set-Property $bucket 'ResetsAt' ([Nullable[DateTimeOffset]]([DateTimeOffset]::UtcNow.AddDays(2)))
    $null = $codex.Buckets.Add($bucket)
    $diagnostics = Invoke-Static 'UsageDiagnostics' 'BuildRedactedJson' @($snapshot, (New-Instance 'UserPreferences'), (New-Instance 'AppConfig'))
    foreach ($forbidden in @('super-secret-value', 'private\codex.exe', '10.0.0.8', 'private prompt payload', 'user@example.test')) {
        if ($diagnostics.Contains($forbidden)) { throw "Diagnostics leaked forbidden value: $forbidden" }
    }
    if ((($diagnostics | ConvertFrom-Json).sources[0].modelTotal.usedPercent) -ne 44) { throw 'Model-total percentage missing from diagnostics.' }
    $health = Invoke-Static 'UsageDiagnostics' 'Evaluate' @($snapshot)
    if ($health[0].State -ne 'healthy' -or $health[0].Source -ne 'Unknown') { throw 'Source health did not classify a fresh available provider.' }
    $claude = $snapshot.Claude
    Set-Property $claude 'IsAvailable' $true
    Set-Property $claude 'UpdatedAt' ([DateTimeOffset]::UtcNow.AddHours(-22))
    $sourceKindType = $assembly.GetType('CodexClaudeUsage.UsageSourceKind', $true)
    Set-Property $claude 'SourceKind' ([Enum]::Parse($sourceKindType, 'ClaudeDesktop'))
    $health = Invoke-Static 'UsageDiagnostics' 'Evaluate' @($snapshot)
    if ($health[1].State -ne 'waiting-sync') { throw "Stale Claude cache must be presented as waiting for sync: $($health[1].State)" }

    # Missing settings can be installed, missing bridge can be repaired, and old managed command is updated.
    if ((Invoke-Static 'ClaudeBridgeHealth' 'Check').State.ToString() -ne 'MissingSettings') { throw 'Missing settings state was not detected.' }
    $repair = Invoke-Static 'ClaudeBridgeHealth' 'Repair'
    if ($repair.State.ToString() -ne 'WaitingForData' -or -not (Test-Path -LiteralPath $bridge)) { throw 'Bridge installation failed or did not report its waiting state.' }
    Remove-Item -LiteralPath $bridge -Force
    $missingBridge = Invoke-Static 'ClaudeBridgeHealth' 'Check'
    if ($missingBridge.State.ToString() -ne 'MissingBridge') { throw "Missing bridge state was not detected: $($missingBridge.State)" }
    $null = Invoke-Static 'ClaudeBridgeHealth' 'Repair'
    $managed = @{ statusLine = @{ type = 'command'; command = 'powershell.exe -File "old\\claude_status_bridge.ps1"'; padding = 0 } } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($settings, $managed)
    $null = Invoke-Static 'ClaudeBridgeHealth' 'Repair'
    if (-not ((Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json).statusLine.command.Contains($bridge))) { throw 'Managed statusLine was not updated.' }
    $custom = @{ statusLine = @{ type = 'command'; command = 'my-custom-status.ps1' } } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($settings, $custom)
    $customResult = Invoke-Static 'ClaudeBridgeHealth' 'Repair'
    if ($customResult.State.ToString() -ne 'CustomStatusLine' -or -not (Get-Content -LiteralPath $settings -Raw).Contains('my-custom-status.ps1')) { throw 'Custom statusLine was overwritten.' }

    # Only listed app files within the configured local-data root can be cleared or trimmed.
    [IO.File]::WriteAllText((Join-Path $local 'codex-usage-history.json'), '{}')
    [IO.File]::WriteAllText((Join-Path $local 'notification-state.json'), '{}')
    [IO.File]::WriteAllText((Join-Path $local 'unrelated.json'), '{}')
    [IO.File]::WriteAllText($outside, '{}')
    $owned = Invoke-Static 'LocalDataManager' 'ListOwnedFiles'
    if (($owned.Name -contains 'unrelated.json') -or -not ($owned.Name -contains 'codex-usage-history.json')) { throw 'Owned-file boundary is incorrect.' }
    $removed = Invoke-Static 'LocalDataManager' 'ClearOperationalData'
    if ($removed -lt 2 -or (Test-Path -LiteralPath (Join-Path $local 'codex-usage-history.json')) -or -not (Test-Path -LiteralPath (Join-Path $local 'unrelated.json')) -or -not (Test-Path -LiteralPath $outside)) { throw 'Clear operation crossed the owned-file boundary.' }
    $oldHistory = Join-Path $local 'codex-history-20000101.json'
    [IO.File]::WriteAllText($oldHistory, '{}')
    (Get-Item -LiteralPath $oldHistory).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-20)
    if ((Invoke-Static 'LocalDataManager' 'TrimTimestampedHistory' @(7)) -ne 1 -or (Test-Path -LiteralPath $oldHistory)) { throw 'Old app timestamped history was not trimmed.' }

    Write-Host 'PASS: diagnostics redaction, source health, bridge repair, and local-data boundaries'
}
finally {
    Remove-Item Env:\CODEX_CLAUDE_USAGE_SETTINGS_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:\CODEX_CLAUDE_USAGE_BRIDGE_INSTALL_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:\CODEX_CLAUDE_USAGE_BRIDGE_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:\CODEX_CLAUDE_USAGE_LOCAL_DATA_DIR -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $settings, $bridge, $bridgeData, $outside -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $local -Recurse -Force -ErrorAction SilentlyContinue
}
