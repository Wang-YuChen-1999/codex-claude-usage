param(
    [Parameter(Mandatory = $true)]
    [long]$WindowHandle,
    [string]$Path,
    [switch]$MoveForCapture
)

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ScreenshotDpiMode
{
    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
'@

[ScreenshotDpiMode]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
if ($MoveForCapture) {
    [ScreenshotDpiMode]::SetWindowPos([IntPtr]$WindowHandle, [IntPtr](-1), 100, 100, 0, 0, 0x0055) | Out-Null
}
[ScreenshotDpiMode]::SetCursorPos(10, 10) | Out-Null
Start-Sleep -Seconds 6
if ($Path) {
    & 'C:\Users\user\.codex\skills\screenshot\scripts\take_screenshot.ps1' -Path $Path -WindowHandle $WindowHandle
}
else {
    & 'C:\Users\user\.codex\skills\screenshot\scripts\take_screenshot.ps1' -Mode temp -WindowHandle $WindowHandle
}
