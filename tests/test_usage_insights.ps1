$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assembly = [Reflection.Assembly]::LoadFile((Join-Path $root 'dist\CodexClaudeUsage.exe'))
$providerType = $assembly.GetType('CodexClaudeUsage.ProviderSnapshot', $true)
$bucketType = $assembly.GetType('CodexClaudeUsage.UsageBucket', $true)
$snapshotType = $assembly.GetType('CodexClaudeUsage.UsageSnapshot', $true)
$selectionType = $assembly.GetType('CodexClaudeUsage.UsageSelection', $true)
$insightsType = $assembly.GetType('CodexClaudeUsage.UsageInsights', $true)
$sourceKindType = $assembly.GetType('CodexClaudeUsage.UsageSourceKind', $true)
$resetEstimateType = $assembly.GetType('CodexClaudeUsage.ResetEstimateKind', $true)
$flags = [Reflection.BindingFlags]'Static,Public,NonPublic'

function New-Provider([string] $Name, [double] $Current, [double] $Weekly, [DateTimeOffset] $Now, [int] $AgeMinutes = 0) {
    $provider = [Activator]::CreateInstance($providerType, @($Name))
    $providerType.GetProperty('IsAvailable').SetValue($provider, $true, $null)
    $providerType.GetProperty('UpdatedAt').SetValue($provider, $Now.AddMinutes(-$AgeMinutes), $null)
    $weeklyLabel = ([string][char]0x6BCF) + ([string][char]0x9031)
    foreach ($pair in @(@('current', $Current, 300), @($weeklyLabel, $Weekly, 10080))) {
        $bucket = [Activator]::CreateInstance($bucketType)
        $bucketType.GetProperty('Label').SetValue($bucket, $pair[0], $null)
        $bucketType.GetProperty('UsedPercent').SetValue($bucket, [double]$pair[1], $null)
        $bucketType.GetProperty('WindowDurationMinutes').SetValue($bucket, [int]$pair[2], $null)
        $bucketType.GetProperty('ResetsAt').SetValue($bucket, [Nullable[DateTimeOffset]]$Now.AddDays(3.5), $null)
        $providerType.GetProperty('Buckets').GetValue($provider, $null).Add($bucket)
    }
    return $provider
}

$now = [DateTimeOffset]::UtcNow
$codex = New-Provider 'Codex' 10 20 $now
$claude = New-Provider 'Claude Code' 15 80 $now
$selection = $selectionType.GetMethod('SummaryBucket', $flags).Invoke($null, @($codex))
if ([double]$selection.UsedPercent -ne 20) { throw 'Summary selection did not prefer the weekly bucket.' }

$snapshot = [Activator]::CreateInstance($snapshotType)
$snapshotType.GetProperty('Codex').SetValue($snapshot, $codex, $null)
$snapshotType.GetProperty('Claude').SetValue($snapshot, $claude, $null)
$report = $insightsType.GetMethod('Build', $flags, $null, [Type[]]@($snapshotType, [DateTimeOffset]), $null).Invoke($null, @($snapshot, $now))
if (-not $report.Codex.IsFresh -or [Math]::Abs([double]$report.Codex.ExpectedUsedPercent - 50) -gt 0.2) { throw 'Weekly pacing was not calculated from the reset boundary.' }
if ($report.Recommendation -notmatch 'Codex') { throw "Expected Codex recommendation, got: $($report.Recommendation)" }
# The reset timeline lists every quota bucket: 2 providers x (5h + weekly) = 4 rows.
if (@($report.Timeline).Count -ne 4) { throw 'The reset timeline must list every quota bucket.' }
$weeklyLabel = ([string][char]0x6BCF) + ([string][char]0x9031)
$timelineNames = @($report.Timeline | ForEach-Object { $_.Provider })
if (-not ($timelineNames | Where-Object { $_ -like "Codex*$weeklyLabel*" })) {
    throw 'Multi-bucket providers must name the quota in the reset timeline.'
}

# With several weekly quotas (Antigravity Gemini vs third-party), the summary must pick the tightest one.
$multi = [Activator]::CreateInstance($providerType, @('Antigravity'))
$providerType.GetProperty('IsAvailable').SetValue($multi, $true, $null)
$providerType.GetProperty('UpdatedAt').SetValue($multi, $now, $null)
foreach ($pair in @(@(($weeklyLabel + ' A'), 12), @(($weeklyLabel + ' B'), 67))) {
    $bucket = [Activator]::CreateInstance($bucketType)
    $bucketType.GetProperty('Label').SetValue($bucket, $pair[0], $null)
    $bucketType.GetProperty('UsedPercent').SetValue($bucket, [double]$pair[1], $null)
    $bucketType.GetProperty('WindowDurationMinutes').SetValue($bucket, 10080, $null)
    $bucketType.GetProperty('ResetsAt').SetValue($bucket, [Nullable[DateTimeOffset]]$now.AddDays(3.5), $null)
    $providerType.GetProperty('Buckets').GetValue($multi, $null).Add($bucket)
}
$multiSummary = $selectionType.GetMethod('SummaryBucket', $flags).Invoke($null, @($multi))
if ([double]$multiSummary.UsedPercent -ne 67) {
    throw "Summary selection must pick the tightest weekly quota, got: $($multiSummary.UsedPercent)"
}

# An unused rolling upper bound is not a fixed reset deadline or a daily allowance.
$idle = New-Provider 'Antigravity' 0 0 $now
$idleWeekly = $selectionType.GetMethod('SummaryBucket', $flags).Invoke($null, @($idle))
$idleWeekly.ResetEstimate = [Enum]::Parse($resetEstimateType, 'UpperBound')
$idleSnapshot = [Activator]::CreateInstance($snapshotType)
$idleSnapshot.Antigravity = $idle
$build = $insightsType.GetMethod('Build', $flags, $null, [Type[]]@($snapshotType, [DateTimeOffset]), $null)
$idleReport = $build.Invoke($null, @($idleSnapshot, $now))
$notStarted = [Regex]::Unescape('\u5c1a\u672a\u958b\u59cb\u8a08\u6642')
if ($idleReport.Antigravity.Status -ne $notStarted -or $idleReport.Antigravity.HasPacing -or
    $idleReport.Antigravity.ExpectedUsedPercent -ne 0 -or $idleReport.Antigravity.DailyAllowancePercent -ne 0) {
    throw 'An idle rolling window must not calculate pacing or a daily quota.'
}
if (-not [string]::IsNullOrEmpty($idleReport.Recommendation)) { throw 'Idle windows cannot produce a pacing recommendation.' }
$idleTimeline = @($idleReport.Timeline | Where-Object { $_.Provider -like "*$weeklyLabel*" })[0]
if ($idleTimeline.Status -ne $notStarted -or -not [object]::ReferenceEquals($idleTimeline.Bucket, $idleWeekly)) {
    throw 'The timeline must preserve the bucket estimate metadata and idle status.'
}

# A real fixed deadline at 0% remains eligible for pacing.
$idleWeekly.ResetEstimate = [Enum]::Parse($resetEstimateType, 'None')
$fixedReport = $build.Invoke($null, @($idleSnapshot, $now))
if (-not $fixedReport.Antigravity.HasPacing -or $fixedReport.Antigravity.DailyAllowancePercent -le 0) {
    throw 'Zero usage at a real fixed reset boundary must retain its daily allowance.'
}
$idleWeekly.ResetEstimate = [Enum]::Parse($resetEstimateType, 'UpperBound')
$idleWeekly.UsedPercent = [double]2
$activeReport = $build.Invoke($null, @($idleSnapshot, $now))
if (-not $activeReport.Antigravity.HasPacing -or $activeReport.Antigravity.DailyAllowancePercent -le 0) {
    throw 'A used rolling window must retain its estimated pacing.'
}

$idleWeekly.ResetsAt = $now.AddMinutes(-1)
$expiredReport = $build.Invoke($null, @($idleSnapshot, $now))
$waitingReset = [Regex]::Unescape('\u7b49\u5f85\u91cd\u8a2d\u540c\u6b65')
if ($expiredReport.Antigravity.HasPacing -or $expiredReport.Antigravity.Status -ne $waitingReset -or
    $expiredReport.Antigravity.UsedPercent -ne 2) {
    throw 'An elapsed reset boundary must wait for synchronization without faking a pace or resetting usage.'
}
$expiredTimeline = @($expiredReport.Timeline | Where-Object { $_.Provider -like "*$weeklyLabel*" })[0]
if ($expiredTimeline.Status -ne $waitingReset) { throw 'An elapsed timeline must wait for reset synchronization.' }
$idleWeekly.ResetsAt = $null
$unknownReport = $build.Invoke($null, @($idleSnapshot, $now))
if ($unknownReport.Antigravity.HasPacing -or $unknownReport.Antigravity.DailyAllowancePercent -ne 0) {
    throw 'An unknown reset boundary must not produce a daily allowance.'
}
if (-not [string]::IsNullOrEmpty($unknownReport.Recommendation)) { throw 'An unknown window cannot produce a pacing recommendation.' }
$idleSnapshot.Codex = New-Provider 'Codex' 10 95 $now
$mixedReport = $build.Invoke($null, @($idleSnapshot, $now))
if ($mixedReport.Recommendation -notmatch 'Codex' -or $mixedReport.Recommendation -match 'Antigravity') {
    throw 'A provider without pacing evidence must not outrank a measured fixed window.'
}

$stale = New-Provider 'Codex' 1 40 $now -AgeMinutes 61
$staleClaude = New-Provider 'Claude Code' 1 40 $now -AgeMinutes (22 * 60)
$providerType.GetProperty('SourceKind').SetValue(
    $staleClaude,
    [Enum]::Parse($sourceKindType, 'ClaudeDesktop'),
    $null)
$snapshotType.GetProperty('Codex').SetValue($snapshot, $stale, $null)
$snapshotType.GetProperty('Claude').SetValue($snapshot, $staleClaude, $null)
$staleReport = $insightsType.GetMethod('Build', $flags, $null, [Type[]]@($snapshotType, [DateTimeOffset]), $null).Invoke($null, @($snapshot, $now))
if ($staleReport.Codex.IsFresh -or -not [string]::IsNullOrEmpty($staleReport.Recommendation)) { throw 'Stale providers must not produce a recommendation.' }
$waitingForClaude = ([string][char]0x7B49) + ([string][char]0x5F85) + ' Claude ' +
    ([string][char]0x540C) + ([string][char]0x6B65)
if ($staleReport.Claude.IsFresh -or $staleReport.Claude.Status -ne $waitingForClaude) {
    throw "Stale Claude cache must remain excluded from live calculations and use a sync status: $($staleReport.Claude.Status)"
}
if ($staleReport.Codex.HasPacing -or $staleReport.Claude.HasPacing) { throw 'Stale records must not expose current pacing.' }

Write-Host 'PASS: weekly selection (tightest quota), pacing, stale gate, recommendation, and per-bucket reset timeline'
