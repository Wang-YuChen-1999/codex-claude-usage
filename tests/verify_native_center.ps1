param([string] $Path)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = @('-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-File', $MyInvocation.MyCommand.Path)
    if ($Path) { $arguments += @('-Path', $Path) }
    & $windowsPowerShell @arguments
    exit $LASTEXITCODE
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NativeCenterDpi
{
    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
'@

[NativeCenterDpi]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
[Windows.Forms.Application]::EnableVisualStyles()
[Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assembly = [Reflection.Assembly]::LoadFile((Join-Path $root 'dist\CodexClaudeUsage.exe'))
$configType = $assembly.GetType('CodexClaudeUsage.AppConfig', $true)
$config = $configType.GetMethod('Load').Invoke($null, [object[]]@([string](Join-Path $root 'config.json')))
$preferencesType = $assembly.GetType('CodexClaudeUsage.UserPreferences', $true)
$preferences = $preferencesType.GetMethod('Load').Invoke($null, @())
$formType = $assembly.GetType('CodexClaudeUsage.UsageCenterForm', $true)
$constructor = $formType.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic') | Select-Object -First 1
$form = $constructor.Invoke([object[]]@($config, $preferences))

try {
    $formType.GetMethod('SelectTab', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($form, @([int]3)) | Out-Null
    $form.Show()
    for ($index = 0; $index -lt 20; $index++) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 25
    }

    $retention = $formType.GetField('_retention', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($form)
    if ($retention.SelectedItem -ne '30') {
        throw "Retention selection mismatch: '$($retention.SelectedItem)'"
    }

    if ($Path) {
        & 'C:\Users\user\.codex\skills\screenshot\scripts\take_screenshot.ps1' -Path $Path -WindowHandle $form.Handle
    }
    Write-Host "PASS: native usage center rendered with retention=$($retention.SelectedItem)"
}
finally {
    $form.Dispose()
}
