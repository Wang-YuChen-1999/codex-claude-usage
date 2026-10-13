param([string] $OutputDirectory)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = @('-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-File', $MyInvocation.MyCommand.Path)
    if ($OutputDirectory) { $arguments += @('-OutputDirectory', $OutputDirectory) }
    & $powershell @arguments
    exit $LASTEXITCODE
}

$root = Split-Path -Parent $PSScriptRoot
$assemblyPath = Join-Path $root 'dist\CodexClaudeUsage.exe'
if (-not (Test-Path -LiteralPath $assemblyPath)) { throw 'Run .\build.ps1 before rendering the demo.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'docs' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DemoDpiMode
{
    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
'@
[DemoDpiMode]::SetProcessDpiAwarenessContext([IntPtr](-1)) | Out-Null
[Windows.Forms.Application]::EnableVisualStyles()
[Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)
$assembly = [Reflection.Assembly]::LoadFile($assemblyPath)
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$now = [DateTimeOffset]::Now
$today = [DateTime]::Today

function New-DemoObject([string] $typeName) {
    return [Activator]::CreateInstance($assembly.GetType("CodexClaudeUsage.$typeName", $true), $true)
}

function Read-Field($instance, [string] $name) {
    return $instance.GetType().GetField($name, $flags).GetValue($instance)
}

function Pump-Events {
    for ($index = 0; $index -lt 12; $index++) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 15
    }
}

function Save-Demo($form, [string] $name) {
    $form.PerformLayout()
    Pump-Events
    $bitmap = New-Object Drawing.Bitmap($form.Width, $form.Height)
    try {
        $form.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height))
        $path = Join-Path $OutputDirectory $name
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        Write-Output $path
    }
    finally { $bitmap.Dispose() }
}

function Set-DemoProvider($provider, [double] $used, [int] $resetDays, [long] $inputCount, [long] $outputCount) {
    $provider.Status = 'Demo data'
    $provider.IsAvailable = $true
    $provider.UpdatedAt = $now
    $bucket = New-DemoObject 'UsageBucket'
    $bucket.Label = 'Weekly'
    $bucket.StableId = 'demo-weekly'
    $bucket.Detail = 'Synthetic quota for the public demo.'
    $bucket.WindowDurationMinutes = 10080
    $bucket.UsedPercent = $used
    $bucket.ResetsAt = $now.AddDays($resetDays)
    $bucket.TrendPoints = [double[]]@(5, 8, 12, 18, 21, ($used - 3), $used)
    $provider.Buckets.Add($bucket)
    if ($inputCount -gt 0) {
        $tokens = New-DemoObject 'LocalTokenUsage'
        $tokens.InputTokens = $inputCount
        $tokens.OutputTokens = $outputCount
        $tokens.TotalTokens = $inputCount + $outputCount
        $tokens.UpdatedAt = $now
        $provider.Tokens = $tokens
    }
}

function Add-DemoEntry($snapshot, [DateTime] $date, [string] $provider, [string] $model, [string] $project, [long] $factor) {
    $entry = New-DemoObject 'TokenUsageEntry'
    $entry.Date = $date
    $entry.Provider = $provider
    $entry.Model = $model
    $entry.Project = $project
    $entry.InputTokens = 7200 * $factor
    $entry.CacheReadTokens = 3400 * $factor
    $entry.CacheWriteTokens = 900 * $factor
    $entry.OutputTokens = 2500 * $factor
    $snapshot.Rows.Add($entry)
}

$popup = $null
$center = $null
try {
    $snapshot = New-DemoObject 'UsageSnapshot'
    $snapshot.UpdatedAt = $now
    Set-DemoProvider $snapshot.Codex 34 3 126400 23600
    Set-DemoProvider $snapshot.Claude 58 2 84200 15800
    Set-DemoProvider $snapshot.Antigravity 23 5 0 0
    $config = New-DemoObject 'AppConfig'
    $preferences = New-DemoObject 'UserPreferences'
    $preferences.TokenDailyBudget = 250000

    $popup = New-DemoObject 'UsagePopup'
    $popup.TopMost = $false
    $popup.GetType().GetField('_pinnedOpen', $flags).SetValue($popup, $true)
    $popup.SetSnapshot($snapshot)
    $popup.Location = [Drawing.Point]::new(-20000, -20000)
    $popup.Show()
    Save-Demo $popup 'demo-popup.png'
    $popup.Hide()

    $centerType = $assembly.GetType('CodexClaudeUsage.UsageCenterForm', $true)
    $constructor = $centerType.GetConstructors($flags) | Select-Object -First 1
    $center = $constructor.Invoke([object[]]@($config, $preferences))
    $center.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $center.Location = [Drawing.Point]::new(-20000, -20000)
    $center.ShowInTaskbar = $false
    $center.ClientSize = [Drawing.Size]::new(1100, 730)
    $insightsType = $assembly.GetType('CodexClaudeUsage.UsageInsights', $true)
    $buildInsights = $insightsType.GetMethods() | Where-Object { $_.Name -eq 'Build' -and $_.GetParameters().Count -eq 2 }
    $insights = $buildInsights.Invoke($null, [object[]]@($snapshot, $now))
    $centerType.GetMethod('SetSnapshot').Invoke($center, [object[]]@($snapshot, $insights, $null, $null, $null)) | Out-Null
    $center.SetStatus('Demo: synthetic data only')
    $center.Show()
    Save-Demo $center 'demo-center.png'

    $analytics = New-DemoObject 'TokenAnalyticsSnapshot'
    $analytics.CapturedAt = $now
    $analytics.LastRecordAt = $now
    $analytics.CoverageStart = $today.AddDays(-60)
    $analytics.IsComplete = $true
    $analytics.Status = 'Demo: synthetic data only'
    for ($day = 0; $day -lt 60; $day++) {
        $date = $today.AddDays(-$day)
        Add-DemoEntry $analytics $date 'Codex' 'codex-demo' 'sample-web-app' (2 + ($day % 5))
        Add-DemoEntry $analytics $date 'Claude' 'claude-demo' 'sample-api' (1 + (($day * 3) % 4))
    }
    $center.SetTokenAnalytics($analytics)
    $centerType.GetMethod('SelectTab', $flags).Invoke($center, [object[]]@(4)) | Out-Null
    $panel = Read-Field $center '_tokenAnalyticsPage'
    (Read-Field $panel '_period').SelectedIndex = 3
    $center.ClientSize = [Drawing.Size]::new(1100, 1450)
    Save-Demo $center 'demo-token-analytics.png'
}
finally {
    if ($null -ne $popup) { $popup.Dispose() }
    if ($null -ne $center) { $center.Dispose() }
}
