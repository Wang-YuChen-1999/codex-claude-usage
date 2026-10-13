$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assemblyPath = Join-Path $root 'dist\CodexClaudeUsage.exe'
$testRoot = Join-Path $env:TEMP "CodexClaudeUsage-local-tokens-$PID"

function Write-JsonLines {
    param([string] $Path, [string[]] $Lines, [bool] $Terminated = $true)
    $content = $Lines -join "`n"
    if ($Terminated) { $content += "`n" }
    [IO.File]::WriteAllText($Path, $content, (New-Object Text.UTF8Encoding($false)))
}

function Assert-Usage {
    param($Usage, [long] $InputTokens, [long] $OutputTokens, [long] $TotalTokens, [string] $Label)
    if ($null -eq $Usage) { throw "$Label did not return token usage." }
    if ($Usage.InputTokens -ne $InputTokens -or $Usage.OutputTokens -ne $OutputTokens -or $Usage.TotalTokens -ne $TotalTokens) {
        throw "$Label expected $InputTokens / $OutputTokens / $TotalTokens; got $($Usage.InputTokens) / $($Usage.OutputTokens) / $($Usage.TotalTokens)."
    }
    if ($Usage.UpdatedAt -eq [DateTimeOffset]::MinValue) { throw "$Label is missing an update time." }
}

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $assembly = [Reflection.Assembly]::LoadFile($assemblyPath)
    $readerType = $assembly.GetType('CodexClaudeUsage.LocalTokenUsageReader', $true)
    $readCodex = $readerType.GetMethod('ReadCodexLatestSession')
    $readClaude = $readerType.GetMethod('ReadClaudeLatestSession')
    if ($null -ne $readCodex.Invoke($null, @([string](Join-Path $testRoot 'missing-codex')))) { throw 'Missing Codex root must return null.' }
    if ($null -ne $readClaude.Invoke($null, @([string](Join-Path $testRoot 'missing-claude')))) { throw 'Missing Claude root must return null.' }

    $codexRoot = Join-Path $testRoot 'codex'
    New-Item -ItemType Directory -Path $codexRoot -Force | Out-Null
    $codexLog = Join-Path $codexRoot 'session.jsonl'
    Write-JsonLines $codexLog @(
        '{"type":"event_msg","timestamp":"2026-09-29T01:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":60,"cached_input_tokens":20,"output_tokens":40,"reasoning_output_tokens":10,"total_tokens":100}}}}',
        '{broken-json',
        '{"type":"event_msg","timestamp":"2026-09-29T02:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":90,"cached_input_tokens":30,"output_tokens":50,"reasoning_output_tokens":20,"total_tokens":140}}}}',
        '{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":-1,"output_tokens":5,"total_tokens":4}}}}',
        '{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":2,"output_tokens":2,"total_tokens":5}}}}',
        '{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":9,"output_tokens":1}}}}'
    )
    [IO.File]::AppendAllText($codexLog, '{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":999,"output_tokens":999,"total_tokens":1998}}}}')
    $codex = $readCodex.Invoke($null, @([string]$codexRoot))
    Assert-Usage $codex 90 50 140 'Codex cumulative usage and malformed row'

    $newerInvalidCodex = Join-Path $codexRoot 'newer-invalid.jsonl'
    Write-JsonLines $newerInvalidCodex @('{"type":"event_msg","payload":{"type":"token_count","info":{}}}')
    [IO.File]::SetLastWriteTimeUtc($newerInvalidCodex, [DateTime]::UtcNow.AddMinutes(1))
    Assert-Usage ($readCodex.Invoke($null, @([string]$codexRoot))) 90 50 140 'Skip newer file without valid usage'
    $lockedCodex = Join-Path $codexRoot 'locked.jsonl'
    Write-JsonLines $lockedCodex @('{"type":"event_msg","timestamp":"2026-10-01T00:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"output_tokens":100,"total_tokens":200}}}}')
    [IO.File]::SetLastWriteTimeUtc($lockedCodex, [DateTime]::UtcNow.AddMinutes(2))
    $lock = [IO.File]::Open($lockedCodex, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        Assert-Usage ($readCodex.Invoke($null, @([string]$codexRoot))) 90 50 140 'Skip locked Codex file'
    }
    finally { $lock.Dispose() }

    $tailRoot = Join-Path $testRoot 'codex-tail'
    New-Item -ItemType Directory -Path $tailRoot -Force | Out-Null
    $tailLog = Join-Path $tailRoot 'large-session.jsonl'
    $tailStream = [IO.File]::Open($tailLog, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
    try { $tailStream.SetLength(16MB) } finally { $tailStream.Dispose() }
    [IO.File]::AppendAllText($tailLog, "`n" + '{"type":"event_msg","timestamp":"2026-10-01T00:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":3,"output_tokens":2,"total_tokens":5}}}}' + "`n")
    $tailWatch = [Diagnostics.Stopwatch]::StartNew()
    Assert-Usage ($readCodex.Invoke($null, @([string]$tailRoot))) 3 2 5 'Codex bounded tail read'
    $tailWatch.Stop()
    if ($tailWatch.Elapsed.TotalSeconds -gt 5) { throw "Codex tail read was too slow: $($tailWatch.Elapsed.TotalSeconds) seconds." }

    $reorderedRoot = Join-Path $testRoot 'codex-reordered'
    New-Item -ItemType Directory -Path $reorderedRoot -Force | Out-Null
    $touchedOld = Join-Path $reorderedRoot 'touched-old.jsonl'
    $actualRecent = Join-Path $reorderedRoot 'actual-recent.jsonl'
    Write-JsonLines $touchedOld @('{"type":"event_msg","timestamp":"2026-10-04T14:16:05Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1222665998,"output_tokens":2638338,"total_tokens":1225304336}}}}')
    Write-JsonLines $actualRecent @('{"type":"event_msg","timestamp":"2026-10-07T03:24:06Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":80,"output_tokens":20,"total_tokens":100}}}}')
    [IO.File]::SetLastWriteTimeUtc($touchedOld, [DateTime]::UtcNow.AddMinutes(5))
    [IO.File]::SetLastWriteTimeUtc($actualRecent, [DateTime]::UtcNow.AddMinutes(-5))
    Assert-Usage ($readCodex.Invoke($null, @([string]$reorderedRoot))) 80 20 100 'Codex latest usage timestamp wins over touched old file'
    Assert-Usage ($readCodex.Invoke($null, @([string]$reorderedRoot))) 80 20 100 'Codex multi-file cache preserves latest usage selection'
    [IO.File]::AppendAllText($actualRecent, '{"type":"event_msg","timestamp":"2026-10-08T03:24:06Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":90,"output_tokens":20,"total_tokens":110}}}}' + "`n")
    Assert-Usage ($readCodex.Invoke($null, @([string]$reorderedRoot))) 90 20 110 'Codex cached tail invalidates after append'
    foreach ($timestampField in @('', '"timestamp":"not-a-date",')) {
        $undated = Join-Path $reorderedRoot ('undated-' + $timestampField.Length + '.jsonl')
        Write-JsonLines $undated @('{"type":"event_msg",' + $timestampField + '"payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":900,"output_tokens":100,"total_tokens":1000}}}}')
        [IO.File]::SetLastWriteTimeUtc($undated, [DateTime]::UtcNow.AddMinutes(10))
        Assert-Usage ($readCodex.Invoke($null, @([string]$reorderedRoot))) 90 20 110 'Codex unknown record date cannot become fresh through file mtime'
    }
    $undatedRoot = Join-Path $testRoot 'codex-undated'
    New-Item -ItemType Directory -Path $undatedRoot -Force | Out-Null
    Write-JsonLines (Join-Path $undatedRoot 'undated.jsonl') @('{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":3,"output_tokens":2,"total_tokens":5}}}}')
    if ($null -ne $readCodex.Invoke($null, @([string]$undatedRoot))) { throw 'Codex without reliable record dates must return null.' }

    $boundaryRoot = Join-Path $testRoot 'codex-tail-boundary'
    New-Item -ItemType Directory -Path $boundaryRoot -Force | Out-Null
    $boundaryLog = Join-Path $boundaryRoot 'boundary.jsonl'
    $boundaryUsage = '{"type":"event_msg","timestamp":"2026-10-09T03:24:06Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":9,"output_tokens":1,"total_tokens":10}}}}' + "`n"
    [IO.File]::WriteAllText($boundaryLog, "`n" + $boundaryUsage + (' ' * (4MB - $boundaryUsage.Length - 1)) + "`n", (New-Object Text.UTF8Encoding($false)))
    Assert-Usage ($readCodex.Invoke($null, @([string]$boundaryRoot))) 9 1 10 'Codex complete first tail line is not skipped'

    $snapshotType = $readerType.GetNestedType('SnapshotReadStream', [Reflection.BindingFlags]'NonPublic')
    $memory = New-Object IO.MemoryStream
    $snapshot = $null
    try {
        $memory.WriteByte(1)
        $memory.WriteByte(2)
        $memory.Position = 0
        $snapshotConstructor = $snapshotType.GetConstructor([Type[]]@([IO.Stream], [long]))
        $snapshot = $snapshotConstructor.Invoke([object[]]@([IO.Stream]$memory, [long]2))
        $memory.Position = $memory.Length
        $memory.WriteByte(3)
        $memory.Position = 0
        $buffer = New-Object byte[] 8
        if ($snapshot.Read($buffer, 0, $buffer.Length) -ne 2 -or $buffer[0] -ne 1 -or $buffer[1] -ne 2) { throw 'Snapshot read must preserve existing bytes while a writer appends.' }
        if ($snapshot.Read($buffer, 0, $buffer.Length) -ne 0) { throw 'Snapshot read must stop at the captured length instead of following appends.' }
    }
    finally {
        if ($null -ne $snapshot) { $snapshot.Dispose() }
        $memory.Dispose()
    }

    $claudeRoot = Join-Path $testRoot 'claude'
    $project = Join-Path $claudeRoot 'project-a'
    New-Item -ItemType Directory -Path $project -Force | Out-Null
    $claudeLog = Join-Path $project 'session-a.jsonl'
    Write-JsonLines $claudeLog @(
        '{"type":"assistant","timestamp":"2026-09-29T01:00:00Z","message":{"id":"msg-a","usage":{"input_tokens":10,"output_tokens":2}}}',
        '{bad-json',
        '{"type":"assistant","timestamp":"2026-09-29T02:00:00Z","message":{"id":"msg-a","usage":{"input_tokens":10,"cache_creation_input_tokens":3,"cache_read_input_tokens":4,"output_tokens":5}}}',
        '{"type":"assistant","timestamp":"2026-09-29T02:01:00Z","message":{"id":"msg-b","usage":{"input_tokens":1,"cache_read_input_tokens":2,"output_tokens":3}}}',
        '{"type":"assistant","message":{"usage":{"input_tokens":999,"output_tokens":999}}}',
        '{"type":"assistant","message":{"id":"msg-b","usage":{"input_tokens":-1,"output_tokens":3}}}'
    )
    [IO.File]::AppendAllText($claudeLog, '{"type":"assistant","message":{"id":"unfinished","usage":{"input_tokens":999')
    $subagents = Join-Path $project 'subagents'
    New-Item -ItemType Directory -Path $subagents -Force | Out-Null
    Write-JsonLines (Join-Path $subagents 'agent-1.jsonl') @('{"type":"assistant","message":{"id":"agent-msg","usage":{"input_tokens":5000,"output_tokens":5000}}}')
    Write-JsonLines (Join-Path $project 'journal.jsonl') @('{"type":"assistant","message":{"id":"journal-msg","usage":{"input_tokens":5000,"output_tokens":5000}}}')
    [IO.File]::SetLastWriteTimeUtc((Join-Path $subagents 'agent-1.jsonl'), [DateTime]::UtcNow.AddMinutes(2))
    [IO.File]::SetLastWriteTimeUtc((Join-Path $project 'journal.jsonl'), [DateTime]::UtcNow.AddMinutes(2))

    Assert-Usage ($readClaude.Invoke($null, @([string]$claudeRoot))) 20 8 28 'Claude primary session deduplication and cache input'
    Assert-Usage ($readClaude.Invoke($null, @([string]$claudeRoot))) 20 8 28 'Claude unchanged-file cache'
    [IO.File]::AppendAllText($claudeLog, "`n" + '{"type":"assistant","timestamp":"2026-09-29T02:02:00Z","message":{"id":"msg-c","usage":{"input_tokens":2,"output_tokens":1}}}' + "`n")
    Assert-Usage ($readClaude.Invoke($null, @([string]$claudeRoot))) 22 9 31 'Claude cache invalidation after append'

    $syntheticLog = Join-Path $project 'newer-synthetic.jsonl'
    Write-JsonLines $syntheticLog @('{"type":"assistant","timestamp":"2026-10-01T12:40:33Z","message":{"id":"synthetic-msg","model":"<synthetic>","usage":{"input_tokens":0,"output_tokens":0}}}')
    [IO.File]::SetLastWriteTimeUtc($syntheticLog, [DateTime]::UtcNow.AddMinutes(3))
    Assert-Usage ($readClaude.Invoke($null, @([string]$claudeRoot))) 22 9 31 'Claude newest synthetic-only session falls back to valid usage'

    $claudeRecentRoot = Join-Path $testRoot 'claude-reordered'
    New-Item -ItemType Directory -Path $claudeRecentRoot -Force | Out-Null
    $claudeOld = Join-Path $claudeRecentRoot 'touched-old.jsonl'
    $claudeNew = Join-Path $claudeRecentRoot 'recent.jsonl'
    Write-JsonLines $claudeOld @('{"type":"assistant","timestamp":"2026-09-12T03:00:00Z","message":{"id":"old-msg","usage":{"input_tokens":1000,"output_tokens":1000}}}')
    Write-JsonLines $claudeNew @(
        '{"type":"assistant","timestamp":"2026-10-09T03:00:00Z","message":{"id":"new-msg","usage":{"input_tokens":15,"output_tokens":5}}}',
        '{"type":"assistant","timestamp":"2026-10-09T03:00:01Z","message":{"id":"new-msg","usage":{"input_tokens":15,"output_tokens":1}}}'
    )
    [IO.File]::SetLastWriteTimeUtc($claudeOld, [DateTime]::UtcNow.AddMinutes(5))
    [IO.File]::SetLastWriteTimeUtc($claudeNew, [DateTime]::UtcNow.AddMinutes(-5))
    Assert-Usage ($readClaude.Invoke($null, @([string]$claudeRecentRoot))) 15 5 20 'Claude latest usage beats touched old file and partial duplicate'
    Assert-Usage ($readClaude.Invoke($null, @([string]$claudeRecentRoot))) 15 5 20 'Claude multi-file cache preserves latest usage selection'
    foreach ($timestampField in @('', '"timestamp":"not-a-date",')) {
        $undatedClaude = Join-Path $claudeRecentRoot ('undated-' + $timestampField.Length + '.jsonl')
        Write-JsonLines $undatedClaude @('{"type":"assistant",' + $timestampField + '"message":{"id":"unknown-date","usage":{"input_tokens":900,"output_tokens":100}}}')
        [IO.File]::SetLastWriteTimeUtc($undatedClaude, [DateTime]::UtcNow.AddMinutes(10))
        Assert-Usage ($readClaude.Invoke($null, @([string]$claudeRecentRoot))) 15 5 20 'Claude unknown record date cannot become fresh through file mtime'
    }
    $mixedDateRoot = Join-Path $testRoot 'claude-mixed-dates'
    New-Item -ItemType Directory -Path $mixedDateRoot -Force | Out-Null
    Write-JsonLines (Join-Path $mixedDateRoot 'mixed.jsonl') @(
        '{"type":"assistant","timestamp":"2026-10-09T03:00:00Z","message":{"id":"known-date","usage":{"input_tokens":15,"output_tokens":5}}}',
        '{"type":"assistant","message":{"id":"unknown-date","usage":{"input_tokens":15,"output_tokens":5}}}'
    )
    if ($null -ne $readClaude.Invoke($null, @([string]$mixedDateRoot))) { throw 'Claude mixed known and unknown dates must not return a partial session total.' }

    $errorRoot = Join-Path $testRoot 'claude-errors'
    New-Item -ItemType Directory -Path $errorRoot -Force | Out-Null
    Write-JsonLines (Join-Path $errorRoot 'errors.jsonl') @(
        '{"type":"assistant","isApiErrorMessage":true,"message":{"id":"api-error-msg","model":"claude-test","usage":{"input_tokens":0,"output_tokens":0}}}',
        '{"type":"assistant","message":{"id":"synthetic-error-msg","model":"<synthetic>","usage":{"input_tokens":0,"output_tokens":0}}}'
    )
    if ($null -ne $readClaude.Invoke($null, @([string]$errorRoot))) { throw 'Claude error-only session must return null.' }

    $zeroRoot = Join-Path $testRoot 'claude-zero'
    New-Item -ItemType Directory -Path $zeroRoot -Force | Out-Null
    Write-JsonLines (Join-Path $zeroRoot 'valid-zero.jsonl') @('{"type":"assistant","timestamp":"2026-10-01T00:00:00Z","isApiErrorMessage":false,"message":{"id":"valid-zero-msg","model":"claude-test","usage":{"input_tokens":0,"output_tokens":0}}}')
    Assert-Usage ($readClaude.Invoke($null, @([string]$zeroRoot))) 0 0 0 'Claude legitimate zero usage is preserved'

    $hugeRoot = Join-Path $testRoot 'huge'
    New-Item -ItemType Directory -Path $hugeRoot -Force | Out-Null
    $olderLog = Join-Path $hugeRoot 'older.jsonl'
    Write-JsonLines $olderLog @('{"type":"assistant","timestamp":"2026-09-12T00:00:00Z","message":{"id":"old-msg","usage":{"input_tokens":1,"output_tokens":1}}}')
    [IO.File]::SetLastWriteTimeUtc($olderLog, [DateTime]::UtcNow.AddMinutes(-1))
    $hugeLog = Join-Path $hugeRoot 'session.jsonl'
    $stream = [IO.File]::Open($hugeLog, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
    try { $stream.SetLength(64MB + 1) } finally { $stream.Dispose() }
    [IO.File]::SetLastWriteTimeUtc($hugeLog, [DateTime]::UtcNow.AddMinutes(1))
    $watch = [Diagnostics.Stopwatch]::StartNew()
    if ($null -ne $readClaude.Invoke($null, @([string]$hugeRoot))) { throw 'Oversized Claude file must not return a partial total.' }
    $watch.Stop()
    if ($watch.Elapsed.TotalSeconds -gt 5) { throw "Large-file guard was too slow: $($watch.Elapsed.TotalSeconds) seconds." }

    $newerSmallLog = Join-Path $hugeRoot 'newer-small.jsonl'
    Write-JsonLines $newerSmallLog @('{"type":"assistant","timestamp":"2026-10-09T00:00:00Z","message":{"id":"new-msg","usage":{"input_tokens":2,"output_tokens":3}}}')
    [IO.File]::SetLastWriteTimeUtc($newerSmallLog, [DateTime]::UtcNow.AddMinutes(2))
    Assert-Usage ($readClaude.Invoke($null, @([string]$hugeRoot))) 2 3 5 'Older oversized Claude candidate must not erase a newer valid result'

    Write-Host 'PASS: local Token latest-record selection, bounded snapshots and exact tail boundaries, Codex/Claude multi-file cache invalidation, Claude primary-session deduplication, malformed/incomplete rows, synthetic/API error exclusions, valid zero usage, and size guard'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolved = [IO.Path]::GetFullPath($testRoot)
        $tempRoot = [IO.Path]::GetFullPath($env:TEMP)
        if ($resolved.StartsWith($tempRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
