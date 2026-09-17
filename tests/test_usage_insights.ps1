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

Write-Host 'PASS: weekly selection (tightest quota), pacing, stale gate, recommendation, and per-bucket reset timeline'
