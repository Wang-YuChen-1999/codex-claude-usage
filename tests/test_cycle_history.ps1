$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assembly = [Reflection.Assembly]::LoadFile((Join-Path $root 'dist\CodexClaudeUsage.exe'))
$providerType = $assembly.GetType('CodexClaudeUsage.ProviderSnapshot', $true)
$bucketType = $assembly.GetType('CodexClaudeUsage.UsageBucket', $true)
$storeType = $assembly.GetType('CodexClaudeUsage.CycleHistoryStore', $true)
$instanceFlags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$storeConstructor = $storeType.GetConstructor($instanceFlags, $null, [Type[]]@([string]), $null)
$testRoot = Join-Path $env:TEMP "CodexClaudeUsage-cycle-history-$PID"
$path = Join-Path $testRoot 'cycle-history.json'

function New-WeeklyProvider([double] $Used, [DateTimeOffset] $Updated, [DateTimeOffset] $Reset) {
    $provider = [Activator]::CreateInstance($providerType, @('Codex'))
    $providerType.GetProperty('IsAvailable').SetValue($provider, $true, $null)
    $providerType.GetProperty('UpdatedAt').SetValue($provider, $Updated, $null)
    $bucket = [Activator]::CreateInstance($bucketType)
    $weeklyLabel = ([string][char]0x6BCF) + ([string][char]0x9031)
    $bucketType.GetProperty('Label').SetValue($bucket, $weeklyLabel, $null)
    $bucketType.GetProperty('UsedPercent').SetValue($bucket, $Used, $null)
    $bucketType.GetProperty('WindowDurationMinutes').SetValue($bucket, 10080, $null)
    $bucketType.GetProperty('ResetsAt').SetValue($bucket, [Nullable[DateTimeOffset]]$Reset, $null)
    $providerType.GetProperty('Buckets').GetValue($provider, $null).Add($bucket)
    return $provider
}

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $store = $storeConstructor.Invoke([object[]]@([string]$path))
    $observe = $storeType.GetMethod('Observe', [Type[]]@($providerType, [DateTimeOffset]))
    $now = [DateTimeOffset]::UtcNow
    $oldReset = $now.AddMinutes(1)
    [void]$observe.Invoke($store, @((New-WeeklyProvider 72 $now.AddMinutes(-1) $oldReset), $now))
    [void]$observe.Invoke($store, @((New-WeeklyProvider 4 $now.AddMinutes(2) $oldReset.AddDays(7)), $now.AddMinutes(2)))
    $records = $storeType.GetProperty('Records').GetValue($store, $null)
    if (@($records | Where-Object IsCompleted).Count -ne 1) { throw 'A reset must complete the preceding cycle.' }
    if (-not (Test-Path -LiteralPath $path)) { throw 'History was not persisted.' }

    $reloaded = $storeConstructor.Invoke([object[]]@([string]$path))
    if (@($storeType.GetProperty('Records').GetValue($reloaded, $null)).Count -lt 2) { throw 'Persisted cycle history did not reload.' }
    $json = $storeType.GetMethod('ExportJson', $instanceFlags, $null, [Type[]]@(), $null).Invoke($reloaded, @())
    $csv = $storeType.GetMethod('ExportCsv', $instanceFlags, $null, [Type[]]@(), $null).Invoke($reloaded, @())
    if ($json -match 'prompt|token|device|email|status|label' -or $csv -match 'prompt|token|device|email|status|label') { throw 'Export contains non-private fields.' }
    if ($csv -notmatch '^provider,cycleStartAt') { throw 'CSV export header is incorrect.' }

    [IO.File]::WriteAllText($path, '{broken')
    $recovered = $storeConstructor.Invoke([object[]]@([string]$path))
    if (@($storeType.GetProperty('Records').GetValue($recovered, $null)).Count -ne 0) { throw 'Corrupt history must recover empty.' }
    $storeType.GetMethod('Clear').Invoke($store, @())
    if (@($storeType.GetProperty('Records').GetValue($store, $null)).Count -ne 0) { throw 'Clear did not remove records.' }
    Write-Host 'PASS: reset completion, persistence, corrupt recovery, clear, and private exports'
}
finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
