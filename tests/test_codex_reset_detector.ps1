$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assemblyPath = Join-Path $root 'dist\CodexClaudeUsage.exe'
$testRoot = Join-Path $env:TEMP "CodexClaudeUsage-reset-detector-$PID"
$statePath = Join-Path $testRoot 'codex-reset-state.json'
$variable = 'CODEX_CLAUDE_USAGE_CODEX_RESET_STATE_PATH'
$previous = [Environment]::GetEnvironmentVariable($variable, 'Process')

function New-CodexSnapshot {
    param(
        [Parameter(Mandatory = $true)][Reflection.Assembly] $Assembly,
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][double] $UsedPercent,
        [Parameter(Mandatory = $true)][DateTimeOffset] $ResetsAt,
        [Parameter(Mandatory = $true)][DateTimeOffset] $UpdatedAt,
        [string] $Status = 'official app-server',
        [string] $StableId = '',
        [int] $WindowDurationMinutes = 300,
        [string] $SourceKind = 'CodexAppServer'
    )

    if ([string]::IsNullOrWhiteSpace($StableId)) {
        $StableId = 'test:' + $Label + ':' + $WindowDurationMinutes
    }

    $providerType = $Assembly.GetType('CodexClaudeUsage.ProviderSnapshot', $true)
    $bucketType = $Assembly.GetType('CodexClaudeUsage.UsageBucket', $true)
    $sourceKindType = $Assembly.GetType('CodexClaudeUsage.UsageSourceKind', $true)
    $provider = [Activator]::CreateInstance($providerType, @('Codex'))
    $providerType.GetProperty('IsAvailable').SetValue($provider, $true, $null)
    $providerType.GetProperty('Status').SetValue($provider, $Status, $null)
    $providerType.GetProperty('SourceKind').SetValue(
        $provider,
        [Enum]::Parse($sourceKindType, $SourceKind),
        $null)
    $providerType.GetProperty('UpdatedAt').SetValue($provider, $UpdatedAt, $null)

    $bucket = [Activator]::CreateInstance($bucketType)
    $bucketType.GetProperty('Label').SetValue($bucket, $Label, $null)
    $bucketType.GetProperty('Detail').SetValue($bucket, 'used', $null)
    $bucketType.GetProperty('StableId').SetValue($bucket, $StableId, $null)
    $bucketType.GetProperty('WindowDurationMinutes').SetValue($bucket, $WindowDurationMinutes, $null)
    $bucketType.GetProperty('UsedPercent').SetValue($bucket, $UsedPercent, $null)
    $bucketType.GetProperty('ResetsAt').SetValue($bucket, [Nullable[DateTimeOffset]]$ResetsAt, $null)
    $providerType.GetProperty('Buckets').GetValue($provider, $null).Add($bucket)
    return $provider
}

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    [Environment]::SetEnvironmentVariable($variable, $statePath, 'Process')
    $assembly = [Reflection.Assembly]::LoadFile($assemblyPath)
    $detectorType = $assembly.GetType('CodexClaudeUsage.CodexResetDetector', $true)
    $detector = [Activator]::CreateInstance($detectorType)
    $observe = $detectorType.GetMethod('Observe')
    $latestProperty = $detectorType.GetProperty('Latest')

    $base = [DateTimeOffset]::UtcNow.AddMinutes(-4)
    $boundary = $base.AddMinutes(2)
    $nextBoundary = $base.AddHours(5).AddMinutes(3)

    $baseline = New-CodexSnapshot $assembly 'primary-5h' 78 $boundary $base
    if (@($observe.Invoke($detector, @($baseline))).Count -ne 0) { throw 'The first observation must only establish a baseline.' }

    $normal = New-CodexSnapshot $assembly 'primary-5h' 79 $boundary $base.AddMinutes(1)
    if (@($observe.Invoke($detector, @($normal))).Count -ne 0) { throw 'Normal usage growth must not trigger a reset.' }

    $reset = New-CodexSnapshot $assembly 'primary-5h' 1 $nextBoundary $base.AddMinutes(3)
    $events = @($observe.Invoke($detector, @($reset)))
    if ($events.Count -ne 1) { throw "A confirmed reset must trigger once; actual count: $($events.Count)" }
    $event = $events[0]
    if ($event.BucketLabel -ne 'primary-5h') { throw 'The reset event label is incorrect.' }
    if ([Math]::Abs([double]$event.PreviousUsedPercent - 79) -gt 0.01) { throw 'The pre-reset percentage is incorrect.' }
    if ([Math]::Abs([double]$event.CurrentUsedPercent - 1) -gt 0.01) { throw 'The post-reset percentage is incorrect.' }

    $duplicate = New-CodexSnapshot $assembly 'primary-5h' 1 $nextBoundary $base.AddMinutes(3).AddSeconds(20)
    if (@($observe.Invoke($detector, @($duplicate))).Count -ne 0) { throw 'The same reset snapshot must not trigger twice.' }
    $beforeRestartState = Get-Content -LiteralPath $statePath -Raw
    if ($beforeRestartState -notmatch 'latestEvent[^}]+primary-5h') {
        throw "The latest reset was not saved: $beforeRestartState"
    }

    $restarted = [Activator]::CreateInstance($detectorType)
    $afterRestart = New-CodexSnapshot $assembly 'primary-5h' 2 $nextBoundary $base.AddMinutes(3).AddSeconds(40)
    if (@($observe.Invoke($restarted, @($afterRestart))).Count -ne 0) { throw 'Restarting the detector must not replay a reset.' }
    if ($null -eq $latestProperty.GetValue($restarted, $null)) {
        throw 'The latest reset event did not survive a restart.'
    }

    $weeklyReset = [DateTimeOffset]::UtcNow.AddDays(5)
    $weekly1 = New-CodexSnapshot $assembly 'weekly' 40 $weeklyReset $base.AddMinutes(3).AddSeconds(50)
    [void]$observe.Invoke($restarted, @($weekly1))
    $weekly2 = New-CodexSnapshot $assembly 'weekly' 39 $weeklyReset $base.AddMinutes(3).AddSeconds(55)
    if (@($observe.Invoke($restarted, @($weekly2))).Count -ne 0) { throw 'A small decline before the boundary must not trigger.' }

    $correctionBase = [DateTimeOffset]::UtcNow.AddMinutes(-2)
    $correctionBoundary = [DateTimeOffset]::UtcNow.AddMinutes(4)
    $correction1 = New-CodexSnapshot $assembly 'correction' 60 $correctionBoundary $correctionBase `
        -StableId 'correction:primary:300'
    [void]$observe.Invoke($restarted, @($correction1))
    $correction2 = New-CodexSnapshot $assembly 'correction' 2 $correctionBoundary.AddHours(5) $correctionBase.AddMinutes(1) `
        -StableId 'correction:primary:300'
    if (@($observe.Invoke($restarted, @($correction2))).Count -ne 0) {
        throw 'A schedule correction before the old boundary must not trigger.'
    }

    $outOfOrder = New-CodexSnapshot $assembly 'primary-5h' 0 $nextBoundary.AddHours(5) $base.AddSeconds(30)
    if (@($observe.Invoke($restarted, @($outOfOrder))).Count -ne 0) {
        throw 'An out-of-order observation must not trigger.'
    }

    $staleBase = [DateTimeOffset]::UtcNow.AddMinutes(-40)
    $staleBoundary = $staleBase.AddMinutes(5)
    $staleNext = $staleBase.AddHours(5).AddMinutes(6)
    $stale1 = New-CodexSnapshot $assembly 'stale-gap' 70 $staleBoundary $staleBase `
        -StableId 'stale-gap:primary:300'
    [void]$observe.Invoke($restarted, @($stale1))
    $stale2 = New-CodexSnapshot $assembly 'stale-gap' 2 $staleNext $staleBase.AddMinutes(25) `
        -StableId 'stale-gap:primary:300'
    if (@($observe.Invoke($restarted, @($stale2))).Count -ne 0) {
        throw 'A long offline gap must rebaseline instead of inferring a reset.'
    }

    $collisionBase = [DateTimeOffset]::UtcNow.AddMinutes(-3)
    $collisionBoundary = $collisionBase.AddMinutes(1)
    $collisionNext = $collisionBase.AddHours(5).AddMinutes(2)
    $collisionA = New-CodexSnapshot $assembly 'same-label' 80 $collisionBoundary $collisionBase `
        -StableId 'limit-a:primary:300'
    [void]$observe.Invoke($restarted, @($collisionA))
    $collisionB = New-CodexSnapshot $assembly 'same-label' 20 ([DateTimeOffset]::UtcNow.AddDays(4)) $collisionBase.AddSeconds(30) `
        -StableId 'limit-b:primary:300'
    [void]$observe.Invoke($restarted, @($collisionB))
    $collisionReset = New-CodexSnapshot $assembly 'same-label' 1 $collisionNext $collisionBase.AddMinutes(2) `
        -StableId 'limit-a:primary:300'
    if (@($observe.Invoke($restarted, @($collisionReset))).Count -ne 1) {
        throw 'Buckets with the same display label must remain independent by stable ID.'
    }

    $fallback = New-CodexSnapshot $assembly 'fallback' 1 $nextBoundary $base.AddMinutes(3) `
        -Status 'local session' -SourceKind 'CodexSessionLog'
    if (@($observe.Invoke($restarted, @($fallback))).Count -ne 0) { throw 'Fallback data must not trigger an official reset.' }

    if (-not (Test-Path -LiteralPath $statePath)) { throw 'The reset state file was not created.' }
    $raw = Get-Content -LiteralPath $statePath -Raw
    if ($raw -match 'prompt|content|session|token|localIpv4|deviceId|email|access[_-]?token|refresh[_-]?token') {
        throw 'The reset state contains sensitive or device data.'
    }

    $legacyObservedAt = [DateTimeOffset]::UtcNow.AddMinutes(-2)
    $legacyResetAt = [DateTimeOffset]::UtcNow.AddMinutes(-1)
    $legacyState = @"
{"version":1,"observations":[{"bucketLabel":"legacy-weekly","windowDurationMinutes":10080,"observedAt":"$($legacyObservedAt.ToString('o'))","usedPercent":44,"resetsAt":"$($legacyResetAt.ToString('o'))"}],"latestEvent":{"bucketLabel":"legacy-weekly","resetAt":"$($legacyResetAt.ToString('o'))","detectedAt":"$($legacyObservedAt.ToString('o'))","previousUsedPercent":55,"currentUsedPercent":44}}
"@
    Set-Content -LiteralPath $statePath -Value $legacyState -Encoding UTF8
    $legacyDetector = [Activator]::CreateInstance($detectorType)
    if ($null -eq $latestProperty.GetValue($legacyDetector, $null)) {
        throw 'Legacy v1 latestEvent did not load.'
    }
    $legacySnapshot = New-CodexSnapshot $assembly 'legacy-weekly' 45 ([DateTimeOffset]::UtcNow.AddDays(7)) ([DateTimeOffset]::UtcNow) `
        -StableId 'legacy-weekly'
    if (@($observe.Invoke($legacyDetector, @($legacySnapshot))).Count -ne 0) {
        throw 'Legacy v1 observations must load as baseline state, not replay a reset.'
    }

    $damagedStatePath = Join-Path $testRoot 'damaged-state.json'
    [IO.File]::WriteAllText($damagedStatePath, '{not-json', [Text.Encoding]::ASCII)
    [Environment]::SetEnvironmentVariable($variable, $damagedStatePath, 'Process')
    $recovered = [Activator]::CreateInstance($detectorType)
    $recoveryBaseline = New-CodexSnapshot $assembly 'recovery' 25 ([DateTimeOffset]::UtcNow.AddMinutes(5)) ([DateTimeOffset]::UtcNow)
    if (@($observe.Invoke($recovered, @($recoveryBaseline))).Count -ne 0) {
        throw 'A damaged state file must recover by establishing a new baseline.'
    }

    Write-Host 'PASS: Codex reset baseline, boundary detection, correction and ordering guards, stale-gap handling, stable identity, persistence recovery, source gating, privacy, and v1 compatibility'
}
finally {
    [Environment]::SetEnvironmentVariable($variable, $previous, 'Process')
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
