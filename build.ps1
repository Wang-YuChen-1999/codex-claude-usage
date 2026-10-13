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
if (-not (Test-Path -LiteralPath $codexIcon) -or -not (Test-Path -LiteralPath $claudeIcon) -or -not (Test-Path -LiteralPath $antigravityIcon)) {
    throw '缺少 Codex、Claude 或 Antigravity 原生圖示資產。'
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
    "/win32icon:$codexIcon",
    "/win32manifest:$(Join-Path $root 'app.manifest')",
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Management.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll',
    "/resource:$codexIcon,CodexClaudeUsage.Assets.codex-native.ico",
    "/resource:$claudeIcon,CodexClaudeUsage.Assets.claude-native.ico",
    "/resource:$antigravityIcon,CodexClaudeUsage.Assets.antigravity-native.ico"
) + $sources

& $compiler @arguments
if ($LASTEXITCODE -ne 0) {
    throw "編譯失敗，結束碼：$LASTEXITCODE"
}

Copy-Item -LiteralPath (Join-Path $root 'config.json') -Destination (Join-Path $outputDirectory 'config.json') -Force
Copy-Item -LiteralPath (Join-Path $root 'app.config') -Destination "$output.config" -Force

$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
Write-Host "Built: $output"
Write-Host "SHA256: $hash"
