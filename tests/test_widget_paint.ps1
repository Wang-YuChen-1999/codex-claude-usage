[CmdletBinding()]
param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $forwardedArguments = @()
    if ($OutputDirectory) { $forwardedArguments = @('-OutputDirectory', $OutputDirectory) }
    & $windowsPowerShell -NoProfile -STA -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path @forwardedArguments
    exit $LASTEXITCODE
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$root = Split-Path -Parent $PSScriptRoot
$assembly = [Reflection.Assembly]::LoadFile((Join-Path $root 'dist\CodexClaudeUsage.exe'))
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$widgetType = $assembly.GetType('CodexClaudeUsage.DesktopWidget', $true)
$stateType = $assembly.GetType('CodexClaudeUsage.WidgetState', $true)
$state = [Activator]::CreateInstance($stateType)
$widget = [Activator]::CreateInstance($widgetType, @($state))
$paint = $widgetType.GetMethod('OnPaint', $flags)
$surface = $widgetType.GetMethod('DrawCapsuleSurfaceCore', $flags)
$layout = $widgetType.GetMethod('ApplyScaledLayout', $flags)
$failures = New-Object 'System.Collections.Generic.List[string]'
$checks = 0

function Record-Failure([string]$CaseName, $ErrorRecord) {
    $exception = $ErrorRecord.Exception
    while ($null -ne $exception.InnerException) { $exception = $exception.InnerException }
    $failures.Add("${CaseName}: $($exception.GetType().Name): $($exception.Message)")
}

function Assert-WidgetPixels([Drawing.Bitmap]$Bitmap, [string]$CaseName) {
    $visible = 0
    $light = 0
    $red = 0
    $accent = 0
    for ($y = 0; $y -lt $Bitmap.Height; $y += 2) {
        for ($x = 0; $x -lt $Bitmap.Width; $x += 2) {
            $pixel = $Bitmap.GetPixel($x, $y)
            if ($pixel.A -lt 64) { continue }
            $visible++
            if ($pixel.R -gt 215 -and $pixel.G -gt 215 -and $pixel.B -gt 215) { $light++ }
            if ($pixel.R -gt 200 -and $pixel.G -lt 80 -and $pixel.B -lt 80) { $red++ }
            if ($pixel.G -gt 100 -and $pixel.G -gt ($pixel.R * 1.3)) { $accent++ }
        }
    }
    if ($visible -lt 100 -or $accent -lt 3) { throw "$CaseName is blank or missing the usage gauge" }
    if ($light -gt ($visible * 0.45) -or $red -gt [Math]::Max(4, $visible * 0.01)) {
        throw "$CaseName resembles the WinForms white/red paint-error surface"
    }
}

try {
    foreach ($name in @('codex', 'claude', 'antigravity')) {
        $widgetType.GetField("_${name}HasValue", $flags).SetValue($widget, $true)
        $animation = $widgetType.GetField("_${name}Animation", $flags).GetValue($widget)
        $animation.Jump(35.0)
    }

    $probe = New-Object Drawing.Bitmap(64, 64)
    $graphics = [Drawing.Graphics]::FromImage($probe)
    try {
        foreach ($dimensions in @(@(0, 0), @(0, 40), @(240, 0), @(1, 1), @(1, 40), @(240, 1))) {
            $width = [int]$dimensions[0]
            $height = [int]$dimensions[1]
            $caseName = "surface ${width}x${height}"
            try {
                $surface.Invoke($widget, @($graphics, $width, $height)) | Out-Null
                $checks++
            }
            catch { Record-Failure $caseName $_ }
        }

        foreach ($dimensions in @(@(0, 0), @(0, 40), @(40, 0), @(1, 1), @(1, 40), @(240, 1))) {
            $width = [int]$dimensions[0]
            $height = [int]$dimensions[1]
            $caseName = "OnPaint ${width}x${height}"
            $widget.ClientSize = New-Object Drawing.Size($width, $height)
            if ($widget.ClientSize.Width -ne $width -or $widget.ClientSize.Height -ne $height) {
                throw "Cannot exercise $caseName because WinForms changed the requested client size"
            }
            $paintGraphics = [Drawing.Graphics]::FromImage($probe)
            $paintArgs = [Windows.Forms.PaintEventArgs]::new($paintGraphics, [Drawing.Rectangle]::new(0, 0, 64, 64))
            try {
                $paint.Invoke($widget, @($paintArgs)) | Out-Null
                $checks++
            }
            catch { Record-Failure $caseName $_ }
            finally {
                $paintArgs.Dispose()
                $paintGraphics.Dispose()
            }
        }
    }
    finally {
        $graphics.Dispose()
        $probe.Dispose()
    }

    if ($OutputDirectory) {
        $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
        [IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
    }

    foreach ($scale in @(1.0, 1.5, 2.0)) {
        $widgetType.GetField('_scale', $flags).SetValue($widget, [single]$scale)
        $logicalWidth = $widgetType.GetMethod('MeasureRequiredExpandedLogicalWidth', $flags).Invoke($widget, @())
        $widgetType.GetField('_dynamicWidgetWidth', $flags).SetValue($widget, [int]$logicalWidth)
        foreach ($collapsed in @($true, $false)) {
            $widgetType.GetField('_collapsed', $flags).SetValue($widget, $collapsed)
            foreach ($mirrored in @($false, $true)) {
                $widgetType.GetField('_mirrored', $flags).SetValue($widget, $mirrored)
                $layout.Invoke($widget, @()) | Out-Null
                $shape = if ($collapsed) { 'circle' } else { 'capsule' }
                $side = if ($mirrored) { 'right' } else { 'left' }
                $caseName = 'widget-{0}-{1}-{2}' -f $shape, [int]($scale * 100), $side
                $bitmap = New-Object Drawing.Bitmap($widget.ClientSize.Width, $widget.ClientSize.Height)
                $bitmap.SetResolution([single](96 * $scale), [single](96 * $scale))
                $graphics = [Drawing.Graphics]::FromImage($bitmap)
                $paintArgs = [Windows.Forms.PaintEventArgs]::new($graphics, $widget.ClientRectangle)
                try {
                    $graphics.Clear([Drawing.Color]::Transparent)
                    $paint.Invoke($widget, @($paintArgs)) | Out-Null
                    Assert-WidgetPixels $bitmap $caseName
                    if ($OutputDirectory) {
                        $bitmap.Save((Join-Path $OutputDirectory "$caseName.png"), [Drawing.Imaging.ImageFormat]::Png)
                    }
                    $checks++
                }
                catch { Record-Failure $caseName $_ }
                finally {
                    $paintArgs.Dispose()
                    $graphics.Dispose()
                    $bitmap.Dispose()
                }
            }
        }
    }
}
finally { $widget.Dispose() }

if ($failures.Count -gt 0) {
    throw ($failures -join [Environment]::NewLine)
}
Write-Host "PASS: $checks widget paint checks (empty/tiny recovery, 100/150/200% DPI, circle/capsule, both directions, no error surface)"
