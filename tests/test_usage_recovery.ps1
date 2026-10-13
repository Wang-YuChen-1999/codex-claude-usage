[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -STA -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$root = Split-Path -Parent $PSScriptRoot
$assembly = [Reflection.Assembly]::LoadFile((Join-Path $root 'dist\CodexClaudeUsage.exe'))
$policyType = $assembly.GetType('CodexClaudeUsage.UsageRecoveryPolicy', $true)
$policy = [Activator]::CreateInstance($policyType)
$now = [DateTimeOffset]::Parse('2026-10-06T10:00:00+08:00')
if ($policy.ConsecutiveFailures -ne 0 -or $null -ne $policy.LastSuccessAt -or $null -ne $policy.NextRetryAt) {
    throw 'Recovery policy did not start in an idle state.'
}
$delays = @(5, 15, 30, 60, 120, 300, 300, 300)
foreach ($delay in $delays) {
    $policy.ReportFailure($now)
    if ($policy.NextDelay.TotalSeconds -ne $delay -or $policy.NextRetryAt -ne $now.AddSeconds($delay)) {
        throw "Unexpected retry delay after $($policy.ConsecutiveFailures) failures."
    }
}
$policy.ReportSuccess($now)
if ($policy.ConsecutiveFailures -ne 0 -or $null -ne $policy.NextRetryAt -or $policy.LastSuccessAt -ne $now -or $policy.NextDelay -ne [TimeSpan]::Zero) {
    throw 'Success did not reset retry state and record the last success.'
}
$later = $now.AddMinutes(2)
$policy.ReportFailure($later)
if ($policy.NextRetryAt -ne $later.AddSeconds(5) -or $policy.LastSuccessAt -ne $now) {
    throw 'A fresh failure did not preserve last success or restart at five seconds.'
}
$calculate = $policyType.GetMethod('CalculateDelay', [Reflection.BindingFlags]'Static,Public')
foreach ($count in @(-1, 0)) {
    if ($calculate.Invoke($null, @($count)) -ne [TimeSpan]::Zero) { throw 'Inactive retry should have zero delay.' }
}
if ($calculate.Invoke($null, @([int]::MaxValue)).TotalSeconds -ne 300) { throw 'Retry delay exceeded its cap.' }
$policy.ReportFailure([DateTimeOffset]::MaxValue)
if ($policy.NextRetryAt -ne [DateTimeOffset]::MaxValue) { throw 'Retry time overflowed.' }
$policy.ReportFailure([DateTimeOffset]::new([DateTime]::MaxValue, [TimeSpan]::FromHours(8)))
if ($policy.NextRetryAt -ne [DateTimeOffset]::MaxValue) { throw 'Local retry time overflowed with a positive UTC offset.' }

$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$widgetType = $assembly.GetType('CodexClaudeUsage.DesktopWidget', $true)
$stateType = $assembly.GetType('CodexClaudeUsage.WidgetState', $true)
$pathsType = $assembly.GetType('CodexClaudeUsage.AppPaths', $true)
$localDataField = $pathsType.GetField('LocalData', [Reflection.BindingFlags]'Static,Public')
$previousLocalData = $localDataField.GetValue($null)
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('usage-recovery-' + [Guid]::NewGuid().ToString('N'))))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$widget = $null
$bitmap = $null
$graphics = $null
$paintArgs = $null
try {
    $localDataField.SetValue($null, $temporaryRoot)
    if ($localDataField.GetValue($null) -ne $temporaryRoot -or $temporaryRoot -eq $previousLocalData) {
        throw 'Paint failure injection requires an isolated LocalData directory.'
    }
    $state = [Activator]::CreateInstance($stateType)
    $widget = [Activator]::CreateInstance($widgetType, @($state))
    $widget.ClientSize = [Drawing.Size]::new(80, 40)
    $paint = $widgetType.GetMethod('OnPaint', $flags)
    $complete = $widgetType.GetMethod('CompletePaintRecovery', $flags)
    $pending = $widgetType.GetField('_paintRecoveryPending', $flags)
    $timer = $widgetType.GetField('_paintRecoveryTimer', $flags).GetValue($widget)
    $script:RecoveryEvents = 0
    $handler = [EventHandler]{ $script:RecoveryEvents++ }
    $widgetType.GetEvent('RecoveryRequested').AddEventHandler($widget, $handler)

    $bitmap = [Drawing.Bitmap]::new(80, 40)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $paintArgs = [Windows.Forms.PaintEventArgs]::new($graphics, $widget.ClientRectangle)
    $graphics.Dispose()
    $paint.Invoke($widget, @($paintArgs)) | Out-Null
    $paint.Invoke($widget, @($paintArgs)) | Out-Null
    if (!$pending.GetValue($widget) -or $script:RecoveryEvents -ne 0 -or !$timer.Enabled) {
        throw 'Disposed Graphics did not schedule exactly one deferred recovery.'
    }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($script:RecoveryEvents -eq 0 -and $clock.ElapsedMilliseconds -lt 2000) {
        [Windows.Forms.Application]::DoEvents()
        [Threading.Thread]::Sleep(10)
    }
    if ($script:RecoveryEvents -ne 1 -or $pending.GetValue($widget)) {
        throw 'The UI timer did not deliver one recovery request after paint returned.'
    }
    if ($widgetType.GetField('_motionTimer', $flags).GetValue($widget).Enabled -or $widgetType.GetField('_morphStarted', $flags).GetValue($widget) -ge 0) {
        throw 'Recovery did not stop animation.'
    }

    $paint.Invoke($widget, @($paintArgs)) | Out-Null
    $complete.Invoke($widget, @($null, [EventArgs]::Empty)) | Out-Null
    if ($script:RecoveryEvents -ne 1 -or !$pending.GetValue($widget) -or $timer.Interval -lt 28000) {
        throw 'Repeated paint failures bypassed the thirty-second recovery cooldown.'
    }
    $elapsed = $widgetType.GetField('_motionClock', $flags).GetValue($widget).ElapsedMilliseconds
    $widgetType.GetField('_lastPaintRecoveryAt', $flags).SetValue($widget, [long]($elapsed - 30000))
    $complete.Invoke($widget, @($null, [EventArgs]::Empty)) | Out-Null
    if ($script:RecoveryEvents -ne 2) { throw 'Recovery did not resume after the cooldown.' }

    $isRecoverable = $widgetType.GetMethod('IsRecoverableDrawingException', [Reflection.BindingFlags]'Static,NonPublic')
    foreach ($exception in @([ArgumentException]::new('test'), [Runtime.InteropServices.ExternalException]::new('test'), [ObjectDisposedException]::new('test'))) {
        if (!$isRecoverable.Invoke($null, @($exception))) { throw 'Expected GDI failure was not recoverable.' }
    }
    foreach ($exception in @([OutOfMemoryException]::new('test'), [InvalidOperationException]::new('test'))) {
        if ($isRecoverable.Invoke($null, @($exception))) { throw 'Unexpected failures must not be swallowed.' }
    }
    if (!(Test-Path -LiteralPath (Join-Path $temporaryRoot 'error.log'))) { throw 'Recoverable paint failure was not logged.' }
}
finally {
    if ($null -ne $paintArgs) { $paintArgs.Dispose() }
    if ($null -ne $graphics) { $graphics.Dispose() }
    if ($null -ne $bitmap) { $bitmap.Dispose() }
    if ($null -ne $widget) { $widget.Dispose() }
    $localDataField.SetValue($null, $previousLocalData)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\usage-recovery-'
    if ($temporaryRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
Write-Host 'PASS: bounded retry backoff, success reset, retained last success, deferred paint recovery, cooldown, exception filtering, and isolated error log'
