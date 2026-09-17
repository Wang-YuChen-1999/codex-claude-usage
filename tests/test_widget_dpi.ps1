$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$assembly = [Reflection.Assembly]::LoadFile((Join-Path (Split-Path -Parent $root) 'dist\CodexClaudeUsage.exe'))

$stateType = $assembly.GetType('CodexClaudeUsage.WidgetState', $true)
$state = [Activator]::CreateInstance($stateType)
$state.Pinned = $true

$widgetType = $assembly.GetType('CodexClaudeUsage.DesktopWidget', $true)
$widget = [Activator]::CreateInstance($widgetType, @($state))

$width = $widget.Width
$height = $widget.Height
Write-Host "Pinned Widget Dimensions: $width x $height"
if ($width -lt 230 -or $width -gt 280) {
    throw "Widget width ($width) should be compact and responsive (230-280 at 100% scale)"
}

$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$wndProcMethod = $widgetType.GetMethod('WndProc', $flags)
if ($null -eq $wndProcMethod) {
    throw "DesktopWidget must override WndProc to handle WM_DPICHANGED and WM_DISPLAYCHANGE"
}

$displayHandler = $widgetType.GetMethod('OnDisplaySettingsChanged', $flags)
if ($null -eq $displayHandler) {
    throw "DesktopWidget must have OnDisplaySettingsChanged handler"
}

$locChanged = $widgetType.GetMethod('OnLocationChanged', $flags)
if ($null -eq $locChanged) {
    throw "DesktopWidget must override OnLocationChanged to monitor multi-monitor drag transitions"
}

$chromeType = $assembly.GetType('CodexClaudeUsage.WindowChrome', $true)
$pointScaleMethod = $chromeType.GetMethod('GetPointScale', [Reflection.BindingFlags]'Public,Static')
if ($null -eq $pointScaleMethod) {
    throw "WindowChrome must provide GetPointScale(Point) for multi-monitor DPI auto-optimization"
}

$widget.Dispose()
Write-Host "PASS: DesktopWidget has compact responsive width, multi-monitor DPI auto-optimization, and display change handlers"
