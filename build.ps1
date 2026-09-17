[CmdletBinding()]
param(
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceDirectory = Join-Path $root 'src'
$outputDirectory = Join-Path $root 'dist'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw "找不到 .NET Framework C# 編譯器：$compiler"
}

if ($Clean -and (Test-Path -LiteralPath $outputDirectory)) {
    Get-ChildItem -LiteralPath $outputDirectory -File | ForEach-Object {
        try {
            Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop
        }
        catch [System.IO.IOException], [System.UnauthorizedAccessException] {
            Write-Warning "略過鎖定中的檔案：$($_.FullName)"
        }
    }
}
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

$output = Join-Path $outputDirectory 'CodexClaudeUsage.exe'
$codexIcon = Join-Path $root 'assets\codex-native.ico'
$claudeIcon = Join-Path $root 'assets\claude-native.ico'
$antigravityIcon = Join-Path $root 'assets\antigravity-native.ico'
# 第三方品牌圖示不隨開源版本散佈；assets\ 內有對應檔案才嵌入，
# 否則執行時改用本機已安裝 App 的圖示，或以品牌色圓形字母繪製。
$iconArguments = @()
if (Test-Path -LiteralPath $codexIcon) {
    $iconArguments += "/win32icon:$codexIcon"
    $iconArguments += "/resource:$codexIcon,CodexClaudeUsage.Assets.codex-native.ico"
}
if (Test-Path -LiteralPath $claudeIcon) {
    $iconArguments += "/resource:$claudeIcon,CodexClaudeUsage.Assets.claude-native.ico"
}
if (Test-Path -LiteralPath $antigravityIcon) {
    $iconArguments += "/resource:$antigravityIcon,CodexClaudeUsage.Assets.antigravity-native.ico"
}
$sources = Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' -File |
    Sort-Object Name |
    ForEach-Object { $_.FullName }

$arguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/debug:pdbonly',
    "/out:$output",
    "/win32manifest:$(Join-Path $root 'app.manifest')",
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll'
) + $iconArguments + $sources

& $compiler @arguments
if ($LASTEXITCODE -ne 0) {
    throw "編譯失敗，結束碼：$LASTEXITCODE"
}

Copy-Item -LiteralPath (Join-Path $root 'config.json') -Destination (Join-Path $outputDirectory 'config.json') -Force
Copy-Item -LiteralPath (Join-Path $root 'app.config') -Destination "$output.config" -Force

$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
Write-Host "Built: $output"
Write-Host "SHA256: $hash"
