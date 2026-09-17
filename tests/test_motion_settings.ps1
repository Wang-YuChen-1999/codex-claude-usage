$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class MotionSettingsProbe
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value,
        uint update);
}
'@

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assemblyPath = Join-Path $root 'dist\CodexClaudeUsage.exe'
if (-not (Test-Path -LiteralPath $assemblyPath)) {
    throw "Build output not found: $assemblyPath"
}

$clientAreaAnimation = $false
if (-not [MotionSettingsProbe]::SystemParametersInfo(0x1042, 0, [ref]$clientAreaAnimation, 0)) {
    $clientAreaAnimation = [System.Windows.Forms.SystemInformation]::UIEffectsEnabled
}

$expected = [System.Windows.Forms.SystemInformation]::UIEffectsEnabled `
    -and $clientAreaAnimation `
    -and -not [System.Windows.Forms.SystemInformation]::HighContrast

$assembly = [Reflection.Assembly]::LoadFile($assemblyPath)
$motionType = $assembly.GetType('CodexClaudeUsage.Motion', $true)
$property = $motionType.GetProperty('IsEnabled', [Reflection.BindingFlags]'Public,Static')
$actual = [bool]$property.GetValue($null, $null)

if ($actual -ne $expected) {
    throw "Motion preference mismatch. Expected=$expected Actual=$actual"
}

Write-Host "PASS: client-area animation preference is respected (enabled=$actual)"
