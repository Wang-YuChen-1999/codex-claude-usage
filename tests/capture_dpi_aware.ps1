param(
    [Parameter(Mandatory = $true)]
    [long]$WindowHandle,
    [string]$Path,
    [switch]$MoveForCapture
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
if (-not ('ScreenshotDpiMode' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ScreenshotDpiMode
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PrintWindow(IntPtr window, IntPtr target, uint flags);
}
'@
}

[ScreenshotDpiMode]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
$window = [IntPtr]$WindowHandle
if (-not [ScreenshotDpiMode]::IsWindow($window)) { throw "Invalid window handle: $WindowHandle" }
if ([ScreenshotDpiMode]::IsIconic($window)) { throw 'Restore the window before capturing it.' }
if ($MoveForCapture) {
    [ScreenshotDpiMode]::SetWindowPos($window, [IntPtr](-1), 100, 100, 0, 0, 0x0055) | Out-Null
}
[ScreenshotDpiMode]::SetCursorPos(10, 10) | Out-Null
for ($index = 0; $index -lt 120; $index++) {
    [Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 50
}

if (-not $Path) {
    $Path = Join-Path ([IO.Path]::GetTempPath()) ('codex-claude-usage-{0}.png' -f [Guid]::NewGuid().ToString('N'))
}
$Path = [IO.Path]::GetFullPath($Path)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
$rect = New-Object ScreenshotDpiMode+Rect
if (-not [ScreenshotDpiMode]::GetWindowRect($window, [ref]$rect)) { throw 'Unable to read the window bounds.' }
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) { throw 'The window has no capture area.' }
$bitmap = New-Object Drawing.Bitmap($width, $height)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
try {
    $deviceContext = $graphics.GetHdc()
    try { $captured = [ScreenshotDpiMode]::PrintWindow($window, $deviceContext, 2) }
    finally { $graphics.ReleaseHdc($deviceContext) }
    if (-not $captured) { throw 'The window did not support native capture.' }
    $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}
Write-Output $Path
