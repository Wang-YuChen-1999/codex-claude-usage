param([switch]$SourceOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ($SourceOnly) {
    $type = Add-Type -Path (Join-Path $root 'src\ClaudeUsageMonitor.cs') -PassThru |
        Where-Object FullName -eq 'CodexClaudeUsage.ClaudeUsageMonitor'
} else {
    $assembly = [Reflection.Assembly]::LoadFrom((Join-Path $root 'dist\CodexClaudeUsage.exe'))
    $type = $assembly.GetType('CodexClaudeUsage.ClaudeUsageMonitor', $true)
}
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$queue = $type.GetMethod('QueueChange', $flags)
$pending = $type.GetField('_pending', $flags)
$files = $type.GetField('_files', $flags)
$take = $type.GetMethod('TryTakeChange', $flags)
$update = $type.GetMethod('UpdatePaths', $flags)
$dispose = $type.GetMethod('Dispose', $flags)
$folder = Join-Path $env:TEMP ('CodexClaudeUsage-live-' + [Guid]::NewGuid().ToString('N'))
$history = Join-Path $folder 'plan-usage-history.json'
$bridge = Join-Path $folder 'claude-status.json'
$monitor = $null

function New-Monitor {
    $historyPath = $history
    $resolver = [Func[string]]({ return $historyPath }.GetNewClosure())
    $constructor = $type.GetConstructor([type[]]@([Func[string]], [string]))
    return $constructor.Invoke([object[]]@($resolver, [string]$bridge))
}

function Signal([DateTimeOffset]$at) {
    [void]$queue.Invoke($monitor, [object[]]@($at))
}

function Take([DateTimeOffset]$at, [bool]$busy = $false) {
    return [bool]$take.Invoke($monitor, [object[]]@($at, $busy))
}

function Wait-Pending {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (-not $pending.GetValue($monitor) -and $clock.ElapsedMilliseconds -lt 3000) {
        Start-Sleep -Milliseconds 25
    }
    if (-not $pending.GetValue($monitor)) { throw 'A file change was not observed.' }
}

function Reset-Monitor {
    if ($null -ne $script:monitor) { [void]$dispose.Invoke($script:monitor, @()) }
    $script:monitor = New-Monitor
    [void]$update.Invoke($script:monitor, @())
}

try {
    [void][IO.Directory]::CreateDirectory($folder)
    $monitor = New-Monitor
    $start = [DateTimeOffset]::UtcNow
    Signal $start
    if (Take $start.AddMilliseconds(500)) { throw 'Changes must be debounced.' }
    Signal $start.AddMilliseconds(600)
    if (Take $start.AddSeconds(1)) { throw 'A second write must extend the quiet period.' }
    if (-not (Take $start.AddMilliseconds(1400))) { throw 'A settled change was not released.' }
    if (Take $start.AddSeconds(2)) { throw 'A consumed change was dispatched twice.' }
    Signal $start.AddSeconds(2)
    if (Take $start.AddSeconds(3)) { throw 'Refresh requests must be rate limited.' }
    if (Take $start.AddSeconds(10) $true) { throw 'A refresh must not overlap an active refresh.' }
    if (-not (Take $start.AddSeconds(10))) { throw 'A pending change was lost during a refresh.' }
    Signal $start.AddSeconds(20)
    Signal $start.AddMilliseconds(21800)
    if (-not (Take $start.AddMilliseconds(22100))) { throw 'Continuous writes must not postpone refresh indefinitely.' }

    Reset-Monitor
    [IO.File]::WriteAllText((Join-Path $folder 'unrelated.json'), '{}')
    Start-Sleep -Milliseconds 200
    if ($pending.GetValue($monitor)) { throw 'Unrelated files must not trigger a refresh.' }
    [IO.File]::WriteAllText($history, '{}')
    Wait-Pending

    Reset-Monitor
    [IO.File]::AppendAllText($history, ' ')
    Wait-Pending

    Reset-Monitor
    $temporary = Join-Path $folder 'status-new.tmp'
    [IO.File]::WriteAllText($temporary, '{}')
    [IO.File]::Move($temporary, $bridge)
    Wait-Pending

    Reset-Monitor
    $before = @($files.GetValue($monitor).Values)[0].Watcher
    [IO.File]::Delete($history)
    [IO.File]::Delete($bridge)
    [IO.File]::Delete((Join-Path $folder 'unrelated.json'))
    [IO.Directory]::Delete($folder)
    Start-Sleep -Milliseconds 100
    [void][IO.Directory]::CreateDirectory($folder)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        [void]$update.Invoke($monitor, @())
        $after = @($files.GetValue($monitor).Values)[0].Watcher
        if (-not [object]::ReferenceEquals($before, $after)) { break }
        Start-Sleep -Milliseconds 50
    } while ($clock.ElapsedMilliseconds -lt 3000)
    if ([object]::ReferenceEquals($before, $after)) { throw 'A recreated directory must receive a new watcher.' }
    [IO.File]::WriteAllText($history, '{}')
    Wait-Pending

    [void]$dispose.Invoke($monitor, @())
    if ($files.GetValue($monitor).Count -ne 0) { throw 'Dispose must release all watchers.' }
    Signal $start.AddMinutes(2)
    if (Take $start.AddMinutes(3)) { throw 'A disposed monitor must not dispatch changes.' }
    [IO.File]::AppendAllText($history, ' ')
    Start-Sleep -Milliseconds 100
    if ($pending.GetValue($monitor)) { throw 'File events must stop after disposal.' }
    Write-Host 'PASS: Claude file monitoring, debounce, pending refresh, rename, directory recovery and disposal'
}
finally {
    if ($null -ne $monitor) { [void]$dispose.Invoke($monitor, @()) }
    if ([IO.Directory]::Exists($folder)) {
        foreach ($path in [IO.Directory]::GetFiles($folder)) { [IO.File]::Delete($path) }
        [IO.Directory]::Delete($folder)
    }
}
