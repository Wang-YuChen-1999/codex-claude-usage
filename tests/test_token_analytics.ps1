param([switch]$ProbeLocal, [int]$ProbeSeconds = 120, [switch]$RecheckCounterFaults)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $MyInvocation.MyCommand.Path)
    if ($ProbeLocal) { $arguments += @('-ProbeLocal', '-ProbeSeconds', $ProbeSeconds) }
    if ($RecheckCounterFaults) { $arguments += '-RecheckCounterFaults' }
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') @arguments
    exit $LASTEXITCODE
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assembly = [Reflection.Assembly]::LoadFile((Join-Path $root 'dist\CodexClaudeUsage.exe'))
$readerType = $assembly.GetType('CodexClaudeUsage.TokenAnalyticsReader', $true)
$constructor = $readerType.GetConstructor([type[]]@([string], [string], [string]))
$readMethod = $readerType.GetMethod('Read', [type[]]@())
$testRoot = Join-Path $env:TEMP ('CodexClaudeUsage-analytics-' + [Guid]::NewGuid().ToString('N'))
$utf8 = New-Object Text.UTF8Encoding($false)
$day1 = [DateTime]::Today.AddDays(-3)
$day2 = $day1.AddDays(1)
$day3 = $day2.AddDays(1)

function Assert-Equal($actual, $expected, [string]$label) {
    if ($actual -ne $expected) { throw "$label expected $expected; got $actual" }
}
function Json-Line($value) { return ConvertTo-Json -InputObject $value -Depth 12 -Compress }
function Stamp([DateTime]$value) { return $value.ToUniversalTime().ToString('o') }
function New-Reader([string]$codex, [string]$claude, [string]$cache = $null) {
    return $constructor.Invoke([object[]]@($codex, $claude, $cache))
}
function Read-Complete($reader) {
    for ($i = 0; $i -lt 30; $i++) {
        $snapshot = $readMethod.Invoke($reader, @())
        if (-not $snapshot.IsIndexing) { return $snapshot }
    }
    throw 'Small fixture did not finish indexing within bounded rounds.'
}
function Write-Lines([string]$path, [string[]]$lines, [bool]$terminated = $true) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
    $text = $lines -join "`n"
    if ($terminated) { $text += "`n" }
    [IO.File]::WriteAllText($path, $text, $utf8)
}
function Codex-Meta([string]$id, [string]$cwd, [string]$parent = '') {
    $sessionId = if ($parent) { $parent } else { $id }
    return Json-Line @{ type='session_meta'; payload=@{ id=$id; session_id=$sessionId; cwd=$cwd; forked_from_id=$parent } }
}
function Codex-Turn([string]$model, [string]$turn, [string]$cwd = 'C:\one\app') {
    return Json-Line @{ type='turn_context'; payload=@{ model=$model; turn_id=$turn; cwd=$cwd } }
}
function Codex-Tokens([DateTime]$time, [long]$inputCount, [long]$readCount, [long]$outputCount, [Nullable[long]]$lastInput = $null, [long]$lastRead = 0, [long]$lastOutput = 0) {
    $details = @{ total_token_usage=@{ input_tokens=$inputCount; cached_input_tokens=$readCount; output_tokens=$outputCount; total_tokens=($inputCount+$outputCount) } }
    if ($null -ne $lastInput) { $details.last_token_usage = @{ input_tokens=$lastInput; cached_input_tokens=$lastRead; output_tokens=$lastOutput; total_tokens=($lastInput+$lastOutput) } }
    return Json-Line @{ type='event_msg'; timestamp=(Stamp $time); payload=@{ type='token_count'; info=$details } }
}
function Claude-Tokens([DateTime]$time, [string]$id, [long]$inputCount, [long]$readCount, [long]$writeCount, [long]$outputCount, [string]$cwd = 'C:\one\app', [string]$model = 'claude-fixture') {
    return Json-Line @{ type='assistant'; timestamp=(Stamp $time); cwd=$cwd; message=@{ id=$id; model=$model; usage=@{ input_tokens=$inputCount; cache_read_input_tokens=$readCount; cache_creation_input_tokens=$writeCount; output_tokens=$outputCount } } }
}
function Total($snapshot, [string]$provider = '') {
    $sum = [long]0
    foreach ($row in $snapshot.Rows) { if (-not $provider -or $row.Provider -eq $provider) { $sum += $row.TotalTokens } }
    return $sum
}

if ($ProbeLocal) {
    $reader = [Activator]::CreateInstance($readerType, $true)
    if ($RecheckCounterFaults) {
        $fileIndexes = $readerType.GetField('_files', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($reader)
        foreach ($key in @($fileIndexes.Keys)) {
            if ($fileIndexes[$key].FaultReasons -band 8) { $fileIndexes.Remove($key) | Out-Null }
        }
    }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $maxMs = 0
    $round = 0
    do {
        $lap = [Diagnostics.Stopwatch]::StartNew()
        $snapshot = $readMethod.Invoke($reader, @())
        $lap.Stop()
        $maxMs = [Math]::Max($maxMs, $lap.ElapsedMilliseconds)
        $round++
        if ($round % 10 -eq 0 -or -not $snapshot.IsIndexing) {
            [pscustomobject]@{ Round=$round; Seconds=[Math]::Round($watch.Elapsed.TotalSeconds,1); Complete=$snapshot.IsComplete; Indexing=$snapshot.IsIndexing; Scanned=$snapshot.FilesScanned; Skipped=$snapshot.FilesSkipped; Rows=$snapshot.Rows.Count; MaxReadMs=$maxMs; MemoryMiB=[Math]::Round([GC]::GetTotalMemory($false)/1MB,1); Status=$snapshot.Status } | ConvertTo-Json -Compress
        }
    } while ($snapshot.IsIndexing -and $watch.Elapsed.TotalSeconds -lt $ProbeSeconds)
    $states = $readerType.GetField('_files', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($reader).Values
    $sampleCount = 0
    foreach ($state in $states) { $sampleCount += $state.Samples.Count }
    $completeFrom = if ($snapshot.CompleteFrom) { ([DateTime]$snapshot.CompleteFrom).ToString('yyyy-MM-dd') } else { $null }
    [pscustomobject]@{ Final=$true; Round=$round; Seconds=[Math]::Round($watch.Elapsed.TotalSeconds,1); Complete=$snapshot.IsComplete; CompleteFrom=$completeFrom; TodayReliable=($snapshot.CompleteFrom -and $snapshot.CompleteFrom -le [DateTime]::Today); Indexing=$snapshot.IsIndexing; Scanned=$snapshot.FilesScanned; Skipped=$snapshot.FilesSkipped; Rows=$snapshot.Rows.Count; MaxReadMs=$maxMs; MemoryMiB=[Math]::Round([GC]::GetTotalMemory($true)/1MB,1); FaultFiles=@($states | Where-Object Fault).Count; MalformedFiles=@($states | Where-Object { $_.FaultReasons -band 1 }).Count; OversizeFiles=@($states | Where-Object { $_.FaultReasons -band 2 }).Count; MissingDateFiles=@($states | Where-Object { $_.FaultReasons -band 4 }).Count; InvalidCounterFiles=@($states | Where-Object { $_.FaultReasons -band 8 }).Count; InvalidInput=@($states | Where-Object { $_.FaultReasons -band 32 }).Count; InvalidCache=@($states | Where-Object { $_.FaultReasons -band 64 }).Count; InvalidOutput=@($states | Where-Object { $_.FaultReasons -band 128 }).Count; InvalidTotal=@($states | Where-Object { $_.FaultReasons -band 256 }).Count; TotalMismatch=@($states | Where-Object { $_.FaultReasons -band 512 }).Count; MissingMessageId=@($states | Where-Object { $_.FaultReasons -band 1024 }).Count; Overflow=@($states | Where-Object { $_.FaultReasons -band 2048 }).Count; CacheAboveInput=@($states | Where-Object { $_.FaultReasons -band 4096 }).Count; LastCounterFaultUtc=($states | Measure-Object LastCounterFaultUtc -Maximum).Maximum; PendingFiles=@($states | Where-Object { $_.Pending.Length -gt 0 -or $_.SkipLine }).Count; Samples=$sampleCount; Status=$snapshot.Status } | ConvertTo-Json -Compress
    exit 0
}

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $codex = Join-Path $testRoot 'codex'
    $claude = Join-Path $testRoot 'claude'
    $main = Join-Path $codex 'main.jsonl'
    $original = @(
        (Codex-Meta 'session-a' 'C:\one\app'),
        (Codex-Turn 'model-a' 'turn-a'),
        (Codex-Tokens $day1.AddHours(23).AddMinutes(58) 100 40 20),
        (Codex-Tokens $day1.AddHours(23).AddMinutes(59) 200 80 40),
        (Codex-Tokens $day2.AddMinutes(1) 200 80 40),
        (Codex-Turn 'model-b' 'turn-b'),
        (Codex-Tokens $day2.AddMinutes(2) 260 100 70)
    )
    Write-Lines $main $original
    Write-Lines (Join-Path $codex 'duplicate.jsonl') $original
    $fork = @((Codex-Meta 'session-child' 'C:\one\app' 'session-a')) + $original + @(
        (Codex-Turn 'model-b' 'turn-child'),
        (Codex-Tokens $day2.AddMinutes(3) 300 120 80)
    )
    Write-Lines (Join-Path $codex 'fork.jsonl') $fork
    $reader = New-Reader $codex $claude
    $snapshot = Read-Complete $reader
    Assert-Equal (Total $snapshot 'Codex') 380 'Codex duplicate files and inherited fork usage'
    Assert-Equal (($snapshot.Rows | Where-Object { $_.Date -eq $day1 } | Measure-Object TotalTokens -Sum).Sum) 240 'Cross-midnight day one'
    Assert-Equal (($snapshot.Rows | Where-Object { $_.Model -eq 'model-b' } | Measure-Object TotalTokens -Sum).Sum) 140 'Model switches use current turn'
    Assert-Equal (($snapshot.Rows | Measure-Object InputTokens -Sum).Sum) 180 'Codex excludes cache from plain input'
    Assert-Equal (($snapshot.Rows | Measure-Object CacheReadTokens -Sum).Sum) 120 'Codex cache input counted once'
    Assert-Equal (Total (Read-Complete $reader)) 380 'Unchanged cache does not duplicate'

    $interleavedRoot = Join-Path $testRoot 'interleaved-forks'
    $sharedHistory = @(
        (Codex-Meta 'big-parent' 'C:\streams'),
        (Codex-Turn 'parent-model' 'parent-before' 'C:\streams'),
        (Codex-Tokens $day1.AddHours(9) 232000000 100000000 0)
    )
    Write-Lines (Join-Path $interleavedRoot 'parent.jsonl') ($sharedHistory + @(
        (Codex-Turn 'parent-model' 'parent-today' 'C:\streams'),
        (Codex-Tokens $day2.AddHours(10) 232100000 100050000 0 100000 50000 0),
        (Codex-Turn 'parent-model' 'repeat-turn' 'C:\streams'),
        (Codex-Tokens $day2.AddHours(10).AddSeconds(15) 232100000 100050000 0 100000 50000 0),
        (Codex-Tokens $day2.AddHours(10).AddMinutes(1) 232200000 100100000 0 100000 50000 0)
    ))
    Write-Lines (Join-Path $interleavedRoot 'child.jsonl') (@((Codex-Meta 'small-child' 'C:\streams' 'big-parent')) + $sharedHistory + @(
        (Codex-Turn 'child-model' 'child-before' 'C:\streams'),
        (Codex-Tokens $day1.AddHours(10) 6000000 2000000 0 6000000 2000000 0),
        (Codex-Turn 'child-model' 'child-today' 'C:\streams'),
        (Codex-Tokens $day2.AddHours(10).AddSeconds(30) 6100000 2050000 0 100000 50000 0),
        (Codex-Tokens $day2.AddHours(10).AddMinutes(1).AddSeconds(30) 6200000 2100000 0 100000 50000 0)
    ))
    $interleaved = Read-Complete (New-Reader $interleavedRoot $null)
    Assert-Equal (($interleaved.Rows | Where-Object Date -eq $day2 | Measure-Object TotalTokens -Sum).Sum) 400000 'Parent and child cumulative streams remain separate after copied parent metadata'
    Assert-Equal (($interleaved.Rows | Where-Object { $_.Date -eq $day2 -and $_.Model -eq 'parent-model' } | Measure-Object TotalTokens -Sum).Sum) 200000 'Parent retransmission across turn contexts is not counted twice'
    Assert-Equal (($interleaved.Rows | Where-Object { $_.Date -eq $day2 -and $_.Model -eq 'child-model' } | Measure-Object TotalTokens -Sum).Sum) 200000 'Child model remains attributed after inherited history'
    Assert-Equal $interleaved.IsComplete $true 'Independent valid fork counters retain completeness'

    [IO.File]::AppendAllText($main, (Codex-Tokens $day3.AddHours(1) 320 130 100) + "`n", $utf8)
    $snapshot = Read-Complete $reader
    Assert-Equal (Total $snapshot) 470 'Appended cumulative event contributes only delta'

    $claudeMain = Join-Path $claude 'project\main.jsonl'
    $messageA = Claude-Tokens $day1.AddHours(8) 'msg-a' 10 20 30 5
    Write-Lines $claudeMain @(
        $messageA,
        (Claude-Tokens $day1.AddHours(8).AddMinutes(1) 'msg-a' 10 20 30 8),
        (Claude-Tokens $day2.AddHours(8) 'msg-b' 2 0 0 3),
        (Claude-Tokens $day2.AddHours(9) 'zero' 0 0 0 0),
        (Claude-Tokens $day2.AddHours(10) 'synthetic' 999 0 0 0 'C:\one\app' '<synthetic>'),
        (Json-Line @{type='assistant'; isApiErrorMessage=$true; timestamp=(Stamp $day2); message=@{id='error';usage=@{input_tokens=999;output_tokens=999}}})
    )
    Write-Lines (Join-Path $claude 'project\subagents\agent.jsonl') @(
        $messageA,
        (Claude-Tokens $day2.AddHours(11) 'msg-c' 1 0 0 2 'C:\two\app'),
        (Claude-Tokens $day2.AddHours(12) 'msg-d' 1 0 0 1 'C:\two\app' '')
    )
    $snapshot = Read-Complete $reader
    Assert-Equal (Total $snapshot 'Claude Code') 78 'Claude global message deduplication and subagents'
    Assert-Equal (($snapshot.Rows | Where-Object Provider -eq 'Claude Code' | Measure-Object CacheWriteTokens -Sum).Sum) 30 'Cache creation remains distinct'
    Assert-Equal (@($snapshot.Rows | Where-Object Provider -eq 'Claude Code' | Select-Object -ExpandProperty Project -Unique).Count) 2 'Same folder names remain different projects'
    if (@($snapshot.Rows | Where-Object { $_.Project.Contains('\') -or $_.Project.Contains('/') }).Count) { throw 'Project labels expose full paths.' }
    if (-not $snapshot.IsComplete) { throw 'Valid and terminated fixtures must be complete.' }

    $pending = Claude-Tokens $day3.AddHours(2) 'msg-e' 7 0 0 4
    [IO.File]::AppendAllText($claudeMain, $pending.Substring(0, 40), $utf8)
    $snapshot = Read-Complete $reader
    Assert-Equal $snapshot.IsComplete $false 'Unterminated record is incomplete'
    Assert-Equal (Total $snapshot 'Claude Code') 78 'Unterminated record contributes nothing'
    [IO.File]::AppendAllText($claudeMain, $pending.Substring(40) + "`n", $utf8)
    $snapshot = Read-Complete $reader
    Assert-Equal (Total $snapshot 'Claude Code') 89 'Completed pending record counted exactly once'
    Assert-Equal $snapshot.IsComplete $true 'Pending record heals after completion'

    $cache = Join-Path $testRoot 'index.bin'
    $persisted = New-Reader $codex $claude $cache
    $saved = Read-Complete $persisted
    if (-not [IO.File]::Exists($cache)) { throw 'Index checkpoint was not written.' }
    $restored = New-Reader $codex $claude $cache
    Assert-Equal (Total (Read-Complete $restored)) (Total $saved) 'Persistent index restores exact total'
    $cacheText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($cache))
    if ($cacheText.Contains('C:\one\app') -or $cacheText.Contains('msg-a') -or $cacheText.Contains('session-a')) { throw 'Persistent index exposes raw identifiers or paths.' }
    [IO.File]::WriteAllBytes($cache, [byte[]]@(1,2,3))
    Assert-Equal (Total (Read-Complete (New-Reader $codex $claude $cache))) (Total $saved) 'Corrupted index rebuilds from local data'
    $cacheBytes = [IO.File]::ReadAllBytes($cache)
    $cacheBytes[4] = 255
    [IO.File]::WriteAllBytes($cache, $cacheBytes)
    Assert-Equal (Total (Read-Complete (New-Reader $codex $claude $cache))) (Total $saved) 'Incompatible index version rebuilds from local data'

    Write-Lines $main @((Codex-Meta 'replacement' 'C:\one\app'), (Codex-Tokens $day3.AddHours(3) 5 0 2))
    $snapshot = Read-Complete $reader
    Assert-Equal (Total $snapshot 'Codex') 387 'Truncated file removes old contributions, preserving other copies'

    $invalidRoot = Join-Path $testRoot 'invalid'
    Write-Lines (Join-Path $invalidRoot 'invalid.jsonl') @(
        (Json-Line @{type='assistant';message=@{id='no-date';usage=@{input_tokens=1;output_tokens=1}}}),
        (Json-Line @{type='assistant';timestamp=(Stamp $day3);message=@{id='overflow';usage=@{input_tokens=[long]::MaxValue;output_tokens=1}}}),
        (Json-Line @{type='assistant';timestamp=(Stamp $day3);message=@{id='negative';usage=@{input_tokens=-1;output_tokens=1}}})
    )
    $invalid = Read-Complete (New-Reader $null $invalidRoot)
    Assert-Equal (Total $invalid) 0 'Invalid counts never leak into totals'
    Assert-Equal $invalid.IsComplete $false 'Missing timestamps and overflows are marked partial'
    Assert-Equal $invalid.CompleteFrom $null 'Unknown dates cannot establish a reliable period'

    $faultRoot = Join-Path $testRoot 'dated-fault'
    $oldFault = Json-Line @{ type='event_msg'; timestamp=(Stamp $day1.AddHours(10)); payload=@{type='token_count';info=@{total_token_usage=@{input_tokens=20;output_tokens=2;total_tokens=99}}} }
    $faultMain = Join-Path $faultRoot 'main.jsonl'
    Write-Lines $faultMain @(
        (Codex-Meta 'dated-fault' 'C:\dated-fault'),
        (Codex-Tokens $day1.AddHours(8) 10 0 1),
        $oldFault,
        (Codex-Tokens $day2.AddHours(8) 30 0 3)
    )
    $faultCache = Join-Path $testRoot 'fault-index.bin'
    $datedReader = New-Reader $faultRoot $null $faultCache
    $dated = Read-Complete $datedReader
    Assert-Equal $dated.IsComplete $false 'Known historical counter error remains partial'
    Assert-Equal $dated.CompleteFrom $day3 'Cross-day recovery also excludes the receiving date'
    if ($dated.CompleteFrom -gt [DateTime]::Today) { throw 'Old dated fault incorrectly blocks today.' }
    $restoredFault = Read-Complete (New-Reader $faultRoot $null $faultCache)
    Assert-Equal $restoredFault.CompleteFrom $day3 'Reliability boundary persists across restart'

    $todayFault = Json-Line @{ type='event_msg'; timestamp=(Stamp ([DateTime]::Today.AddHours(1))); payload=@{type='token_count';info=@{total_token_usage=@{input_tokens=40;output_tokens=4;total_tokens=99}}} }
    [IO.File]::AppendAllText($faultMain, $todayFault + "`n", $utf8)
    $dated = Read-Complete $datedReader
    Assert-Equal $dated.CompleteFrom ([DateTime]::Today.AddDays(1)) 'Today counter error blocks today budget'

    $splitRoot = Join-Path $testRoot 'split-fault'
    Write-Lines (Join-Path $splitRoot 'part1.jsonl') @((Codex-Meta 'dated-fault' 'C:\dated-fault'), (Codex-Tokens $day1.AddHours(8) 10 0 1), $oldFault)
    Write-Lines (Join-Path $splitRoot 'part2.jsonl') @((Codex-Meta 'dated-fault' 'C:\dated-fault'), (Codex-Tokens $day2.AddHours(8) 30 0 3))
    Assert-Equal (Read-Complete (New-Reader $splitRoot $null)).CompleteFrom $day3 'Counter gaps carried to a continuation file still exclude its date'

    $oldRoot = Join-Path $testRoot 'old'
    Write-Lines (Join-Path $oldRoot 'old.jsonl') @((Claude-Tokens ([DateTime]::Today.AddDays(-95)) 'old-msg' 7 0 0 8))
    Assert-Equal (Total (Read-Complete (New-Reader $null $oldRoot))) 0 'Old usage is never reassigned to today'

    $baselineRoot = Join-Path $testRoot 'baseline'
    Write-Lines (Join-Path $baselineRoot 'baseline.jsonl') @(
        (Codex-Meta 'baseline-session' 'C:\baseline'),
        (Codex-Tokens ([DateTime]::Today.AddDays(-95)) 1000 500 100),
        (Codex-Tokens $day3 1100 550 110)
    )
    $baseline = Read-Complete (New-Reader $baselineRoot $null)
    Assert-Equal (Total $baseline) 110 'Old cumulative baseline is retained without entering current totals'
    Assert-Equal $baseline.IsComplete $true 'Old baseline does not create a false completeness failure'

    $largeRoot = Join-Path $testRoot 'large-irrelevant'
    $largeLine = '{"type":"event_msg","payload":{"type":"agent_message","message":"' + ('x' * 9MB) + '"}}'
    Write-Lines (Join-Path $largeRoot 'large.jsonl') @($largeLine, (Codex-Tokens $day3 10 3 2))
    $large = Read-Complete (New-Reader $largeRoot $null)
    Assert-Equal (Total $large) 12 'Large irrelevant content is skipped without losing following usage'
    Assert-Equal $large.IsComplete $true 'Large non-usage content is not an incomplete token record'

    $source = New-Object Threading.CancellationTokenSource
    $source.Cancel()
    $cancelMethod = $readerType.GetMethod('Read', [type[]]@([Threading.CancellationToken]))
    $cancelled = $false
    try { $cancelMethod.Invoke($reader, @($source.Token)) | Out-Null }
    catch { $cancelled = $_.Exception.InnerException -is [OperationCanceledException] }
    finally { $source.Dispose() }
    Assert-Equal $cancelled $true 'Background index supports cancellation'

    $overflowRoot = Join-Path $testRoot 'sum-overflow'
    Write-Lines (Join-Path $overflowRoot 'sum.jsonl') @(
        (Claude-Tokens $day3 'large-1' ([long]::MaxValue-1) 0 0 0),
        (Claude-Tokens $day3 'large-2' 3 0 0 0)
    )
    $overflow = Read-Complete (New-Reader $null $overflowRoot)
    Assert-Equal $overflow.IsComplete $false 'Aggregate overflow is explicit'
    if (@($overflow.Rows | Where-Object { $_.TotalTokens -lt 0 }).Count) { throw 'Aggregate overflow wrapped around.' }
    Write-Host 'Token analytics tests passed: daily deltas, duplicates, forks, cache, models, subagents, incremental reads, persistence, and invalid records.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $allowed = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\CodexClaudeUsage-analytics-'
    if ($resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
