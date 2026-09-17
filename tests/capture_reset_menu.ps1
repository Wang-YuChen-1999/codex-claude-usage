param(
    [Parameter(Mandatory = $true)]
    [string] $Path
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -STA -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path -Path $Path
    exit $LASTEXITCODE
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ResetMenuDpi
{
    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
'@

[ResetMenuDpi]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assemblyPath = Join-Path $root 'dist\CodexClaudeUsage.exe'
$configPath = Join-Path $root 'config.json'
$context = $null
$showEvent = $null

try {
    [Windows.Forms.Application]::EnableVisualStyles()
    [Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)
    $assembly = [Reflection.Assembly]::LoadFile($assemblyPath)
    $configType = $assembly.GetType('CodexClaudeUsage.AppConfig', $true)
    $config = $configType.GetMethod('Load').Invoke($null, [object[]]@([string]$configPath))
    $showEvent = New-Object Threading.EventWaitHandle($false, [Threading.EventResetMode]::AutoReset)
    $showEvent = $showEvent.PSObject.BaseObject
    $contextType = $assembly.GetType('CodexClaudeUsage.TrayApplication', $true)
    $constructorFlags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
    $constructor = $contextType.GetConstructors($constructorFlags) | Select-Object -First 1
    $context = $constructor.Invoke([object[]]@($config, [bool]$false, $showEvent))

    $deadline = [DateTime]::UtcNow.AddSeconds(7)
    while ([DateTime]::UtcNow -lt $deadline) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 25
    }

    $flags = [Reflection.BindingFlags]'Instance,NonPublic'
    $menu = $contextType.GetField('_menu', $flags).GetValue($context)
    $menu.Show([Drawing.Point]::new(120, 120))
    for ($index = 0; $index -lt 12; $index++) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 25
    }

    $bounds = $menu.Bounds
    $margin = 16
    $capture = [Drawing.Rectangle]::new(
        [Math]::Max(0, $bounds.X - $margin),
        [Math]::Max(0, $bounds.Y - $margin),
        $bounds.Width + ($margin * 2),
        $bounds.Height + ($margin * 2))
    $bitmap = New-Object Drawing.Bitmap($capture.Width, $capture.Height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($capture.Location, [Drawing.Point]::Empty, $capture.Size)
        $directory = Split-Path -Parent ([IO.Path]::GetFullPath($Path))
        if (-not [string]::IsNullOrWhiteSpace($directory)) {
            [IO.Directory]::CreateDirectory($directory) | Out-Null
        }
        $bitmap.Save([IO.Path]::GetFullPath($Path), [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}
finally {
    if ($null -ne $context) { $context.Dispose() }
    if ($null -ne $showEvent) { $showEvent.Dispose() }
}
