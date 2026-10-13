$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assembly = [Reflection.Assembly]::LoadFile((Join-Path $root 'dist\CodexClaudeUsage.exe'))
$monitorType = $assembly.GetType('CodexClaudeUsage.TokenBudgetMonitor', $true)
$snapshotType = $assembly.GetType('CodexClaudeUsage.TokenAnalyticsSnapshot', $true)
$entryType = $assembly.GetType('CodexClaudeUsage.TokenUsageEntry', $true)
$preferencesType = $assembly.GetType('CodexClaudeUsage.UserPreferences', $true)
$reportType = $assembly.GetType('CodexClaudeUsage.TokenAnalyticsReport', $true)
$evaluate = $monitorType.GetMethod('Evaluate')
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('CodexClaudeUsage-token-budget-' + [Guid]::NewGuid().ToString('N'))
$previousPreferencesPath = [Environment]::GetEnvironmentVariable('CODEX_CLAUDE_USAGE_PREFERENCES_PATH')

function Assert-Equal([object] $Actual, [object] $Expected, [string] $Message) {
    if ($Actual -cne $Expected) { throw "$Message Expected=[$Expected] Actual=[$Actual]" }
}

function Assert-Null([object] $Actual, [string] $Message) {
    if ($null -ne $Actual) { throw $Message }
}

function New-Entry([DateTime] $Date, [string] $Provider, [string] $Model, [string] $Project,
    [long] $InputTokens, [long] $CacheReadTokens = 0, [long] $CacheWriteTokens = 0, [long] $OutputTokens = 0) {
    $entry = [Activator]::CreateInstance($entryType, $true)
    $entry.Date = $Date
    $entry.Provider = $Provider
    $entry.Model = $Model
    $entry.Project = $Project
    $entry.InputTokens = $InputTokens
    $entry.CacheReadTokens = $CacheReadTokens
    $entry.CacheWriteTokens = $CacheWriteTokens
    $entry.OutputTokens = $OutputTokens
    return $entry
}

function New-Snapshot([DateTimeOffset] $Now, [object[]] $Rows) {
    $snapshot = [Activator]::CreateInstance($snapshotType, $true)
    $snapshot.CapturedAt = $Now
    $snapshot.IsComplete = $true
    $snapshot.IsIndexing = $false
    foreach ($row in $Rows) { $snapshot.Rows.Add($row) }
    return $snapshot
}

function New-Preferences([long] $Budget = 1000) {
    $preferences = [Activator]::CreateInstance($preferencesType, $true)
    $preferences.TokenDailyBudget = $Budget
    return $preferences
}

function New-Monitor([string] $Name) {
    $path = [string](Join-Path $temporaryRoot ($Name + '.json'))
    return $monitorType.GetConstructor([type[]]@([string])).Invoke([object[]]@($path))
}

function Evaluate-Budget([object] $Monitor, [object] $Snapshot, [object] $Preferences,
    [DateTimeOffset] $Now, [bool] $Enabled = $true) {
    return $evaluate.Invoke($Monitor, [object[]]@($Snapshot, $Preferences, $Now, $Enabled))
}

function Convert-Report([object] $Rows) {
    $arguments = New-Object object[] 1
    $arguments[0] = $Rows
    return [string]$reportType.GetMethod('ToCsv').Invoke($null, $arguments)
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    [Environment]::SetEnvironmentVariable('CODEX_CLAUDE_USAGE_PREFERENCES_PATH', (Join-Path $temporaryRoot 'preferences.json'))
    $now = [DateTimeOffset]::Now
    $today = $now.LocalDateTime.Date
    $preferences = New-Preferences
    $monitor = New-Monitor 'thresholds'
    $snapshot = New-Snapshot $now @(
        (New-Entry $today 'Codex' 'model-a' 'project-a' 300 100 50 149),
        (New-Entry $today 'Claude' 'model-b' 'project-b' 200),
        (New-Entry ($today.AddDays(-1)) 'Codex' 'model-a' 'project-a' 99999)
    )
    Assert-Null (Evaluate-Budget $monitor $snapshot $preferences $now) '79.9% must not notify or include yesterday.'
    $snapshot.Rows[1].OutputTokens = 1
    $alert = Evaluate-Budget $monitor $snapshot $preferences $now
    Assert-Equal $alert.Level 80 '80% must notify.'
    Assert-Equal $alert.Used ([decimal]800) 'Daily usage must include all providers and cache categories.'
    Assert-Equal $alert.Budget ([long]1000) 'Alert must contain the configured budget.'
    Assert-Null (Evaluate-Budget $monitor $snapshot $preferences $now) 'Repeated refresh must not repeat an 80% alert.'
    $monitor = New-Monitor 'thresholds'
    Assert-Null (Evaluate-Budget $monitor $snapshot $preferences $now) 'Restart must preserve the delivered 80% alert.'
    $snapshot.Rows[1].OutputTokens = 201
    Assert-Equal (Evaluate-Budget $monitor $snapshot $preferences $now).Level 100 '100% must notify after 80%.'
    $monitor = New-Monitor 'thresholds'
    Assert-Null (Evaluate-Budget $monitor $snapshot $preferences $now) 'Restart must preserve the delivered 100% alert.'
    $snapshot.Rows[1].OutputTokens = 1
    Assert-Null (Evaluate-Budget $monitor $snapshot $preferences $now) 'A lower corrected total must not repeat 80%.'

    $directSnapshot = New-Snapshot $now @((New-Entry $today 'Codex' 'model' 'project' 1200))
    Assert-Equal (Evaluate-Budget (New-Monitor 'direct-100') $directSnapshot $preferences $now).Level 100 'Crossing both thresholds must produce only the 100% level.'

    $recentSnapshot = New-Snapshot $now @((New-Entry $today 'Codex' 'model' 'project' 1000))
    $recentSnapshot.IsComplete = $false
    $recentSnapshot.CompleteFrom = $today
    Assert-Equal (Evaluate-Budget (New-Monitor 'recent-complete') $recentSnapshot $preferences $now).Level 100 'Older incomplete history must not block a verified complete current day.'
    $recentSnapshot.CompleteFrom = $today.AddDays(1)
    Assert-Null (Evaluate-Budget (New-Monitor 'today-incomplete') $recentSnapshot $preferences $now) 'An incomplete current day must not notify.'
    $recentSnapshot.CompleteFrom = $null
    Assert-Null (Evaluate-Budget (New-Monitor 'unknown-range') $recentSnapshot $preferences $now) 'Unknown completeness must not notify.'

    $beforeMidnight = [DateTimeOffset]::new([DateTime]::SpecifyKind($today.AddHours(23).AddMinutes(59), [DateTimeKind]::Local))
    $afterMidnight = $beforeMidnight.AddMinutes(2)
    $midnightMonitor = New-Monitor 'midnight'
    $midnightSnapshot = New-Snapshot $beforeMidnight @((New-Entry $beforeMidnight.LocalDateTime.Date 'Codex' 'model' 'project' 800))
    Assert-Equal (Evaluate-Budget $midnightMonitor $midnightSnapshot $preferences $beforeMidnight).Level 80 'First local day must notify.'
    $midnightSnapshot.CapturedAt = $afterMidnight
    Assert-Null (Evaluate-Budget $midnightMonitor $midnightSnapshot $preferences $afterMidnight) 'Yesterday usage must not carry into the new local day.'
    $midnightSnapshot.Rows.Add((New-Entry $afterMidnight.LocalDateTime.Date 'Claude' 'model' 'project' 800))
    Assert-Equal (Evaluate-Budget $midnightMonitor $midnightSnapshot $preferences $afterMidnight).Level 80 'The new local day must get its own threshold alert.'

    foreach ($condition in @('null', 'incomplete', 'indexing', 'stale', 'future', 'quiet', 'global-disabled', 'budget-disabled', 'zero-budget')) {
        $gatedMonitor = New-Monitor $condition
        $gatedPreferences = New-Preferences
        $gatedSnapshot = New-Snapshot $now @((New-Entry $today 'Codex' 'model' 'project' 1000))
        $enabled = $true
        switch ($condition) {
            'null' { $gatedSnapshot = $null }
            'incomplete' { $gatedSnapshot.IsComplete = $false }
            'indexing' { $gatedSnapshot.IsIndexing = $true }
            'stale' { $gatedSnapshot.CapturedAt = $now.AddMinutes(-6) }
            'future' { $gatedSnapshot.CapturedAt = $now.AddMinutes(2) }
            'quiet' {
                $gatedPreferences.QuietHoursEnabled = $true
                $gatedPreferences.QuietStartMinutes = $now.Hour * 60 + $now.Minute
                $gatedPreferences.QuietEndMinutes = ($gatedPreferences.QuietStartMinutes + 1) % 1440
            }
            'global-disabled' { $enabled = $false }
            'budget-disabled' { $gatedPreferences.NotifyTokenBudget = $false }
            'zero-budget' { $gatedPreferences.TokenDailyBudget = 0 }
        }
        Assert-Null (Evaluate-Budget $gatedMonitor $gatedSnapshot $gatedPreferences $now $enabled) "$condition must not notify."
        Assert-Equal (Test-Path -LiteralPath (Join-Path $temporaryRoot ($condition + '.json'))) $false "$condition must not consume a threshold."
        $restoredSnapshot = New-Snapshot $now @((New-Entry $today 'Codex' 'model' 'project' 1000))
        Assert-Equal (Evaluate-Budget $gatedMonitor $restoredSnapshot (New-Preferences) $now).Level 100 "$condition must permit a later valid notification."
    }

    $largeBudget = [long]4000000000000001
    $savedPreferences = New-Preferences $largeBudget
    $savedPreferences.NotifyTokenBudget = $false
    $preferencesType.GetMethod('Save').Invoke($savedPreferences, @()) | Out-Null
    $loadedPreferences = $preferencesType.GetMethod('Load').Invoke($null, @())
    Assert-Equal $loadedPreferences.TokenDailyBudget $largeBudget 'Large integer budgets must survive JSON persistence exactly.'
    Assert-Equal $loadedPreferences.NotifyTokenBudget $false 'Token budget notification preference must persist.'
    $loadedPreferences.TokenDailyBudget = [long]::MinValue
    Assert-Equal $loadedPreferences.TokenDailyBudget ([long]0) 'Negative budget must clamp to zero.'
    $loadedPreferences.TokenDailyBudget = [long]::MaxValue
    Assert-Equal $loadedPreferences.TokenDailyBudget ([long]9000000000000000) 'Oversized budget must clamp to the supported maximum.'
    $preferencesType.GetMethod('Save').Invoke($loadedPreferences, @()) | Out-Null
    $reloadedPreferences = $preferencesType.GetMethod('Load').Invoke($null, @())
    Assert-Equal $reloadedPreferences.TokenDailyBudget ([long]9000000000000000) 'Clamped maximum must survive a second atomic save.'

    $csvRows = New-Snapshot $now @(
        (New-Entry $today 'Beta' 'A' 'A' 1 2 3 4),
        (New-Entry $today 'Alpha' 'B' 'A' 5 6 7 8),
        (New-Entry $today 'Alpha' 'A' 'B' 9 10 11 12),
        (New-Entry $today 'Alpha' 'A' 'A' 13 14 15 16),
        (New-Entry ($today.AddDays(-1)) 'Zeta' 'Z' 'Z' 17 18 19 20)
    )
    $csv = Convert-Report $csvRows.Rows
    $parsed = @($csv | ConvertFrom-Csv)
    Assert-Equal $parsed.Count 5 'CSV must preserve every input row.'
    Assert-Equal (($parsed | ForEach-Object { $_.Provider + '/' + $_.Model + '/' + $_.Project }) -join '|') 'Zeta/Z/Z|Alpha/A/A|Alpha/A/B|Alpha/B/A|Beta/A/A' 'CSV must sort by date, provider, model, then project.'
    Assert-Equal $parsed[0].Date ($today.AddDays(-1).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)) 'CSV date must use an invariant calendar format.'
    $exportTotal = [decimal]0
    foreach ($row in $parsed) {
        $componentSum = [decimal]$row.InputTokens + [decimal]$row.CacheReadTokens + [decimal]$row.CacheWriteTokens + [decimal]$row.OutputTokens
        Assert-Equal ([decimal]$row.TotalTokens) $componentSum 'Each CSV total must equal its four nonoverlapping categories.'
        $exportTotal += [decimal]$row.TotalTokens
    }
    Assert-Equal $exportTotal ([decimal]210) 'CSV component totals must be preserved across all rows.'

    $specialProject = "專案,含`"引號`"`r`n下一行"
    $specialRows = New-Snapshot $now @((New-Entry $today 'Codex' '測試模型' $specialProject 123))
    $specialCsv = Convert-Report $specialRows.Rows
    $utf8Roundtrip = [Text.Encoding]::UTF8.GetString([Text.Encoding]::UTF8.GetBytes($specialCsv))
    $specialParsed = @($utf8Roundtrip | ConvertFrom-Csv)
    Assert-Equal $specialParsed.Count 1 'Embedded newline must not create another CSV record.'
    Assert-Equal $specialParsed[0].Project "專案,含`"引號`"  下一行" 'CSV must preserve commas and quotes while normalizing embedded line breaks.'
    Assert-Equal $specialParsed[0].Model '測試模型' 'CSV must preserve Unicode under UTF-8.'

    foreach ($formula in @('=1+1', '+1', '-1', '@SUM(A1)', '  =1+1', "`t+1", '  -1', "`t@SUM(A1)")) {
        $formulaRows = New-Snapshot $now @((New-Entry $today $formula $formula $formula 1))
        $formulaParsed = @((Convert-Report $formulaRows.Rows) | ConvertFrom-Csv)
        foreach ($column in @('Provider', 'Model', 'Project')) {
            Assert-Equal $formulaParsed[0].$column ("'" + $formula) "Formula-like $column must be neutralized, including leading whitespace."
        }
    }
    $emptySnapshot = New-Snapshot $now @()
    Assert-Equal @((Convert-Report $emptySnapshot.Rows) | ConvertFrom-Csv).Count 0 'An empty report must retain its header without phantom rows.'

    Write-Host 'PASS: Token budget thresholds, durable deduplication, local midnight, suppression recovery, integer preferences, and safe CSV export'
}
finally {
    [Environment]::SetEnvironmentVariable('CODEX_CLAUDE_USAGE_PREFERENCES_PATH', $previousPreferencesPath)
    $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $expectedTemporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedTemporaryRoot.StartsWith($expectedTemporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a test directory outside the temporary root.'
    }
    if (Test-Path -LiteralPath $resolvedTemporaryRoot) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
