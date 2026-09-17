[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$installRoot = [IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$programsRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
$expectedRoot = [IO.Path]::GetFullPath((Join-Path $programsRoot 'CodexClaudeUsage'))
$executable = Join-Path $installRoot 'CodexClaudeUsage.exe'
$bridge = Join-Path $installRoot 'claude_status_bridge.ps1'

if (-not $installRoot.Equals($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "拒絕移除非預期目錄：$installRoot"
}

Get-Process -Name 'CodexClaudeUsage' -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.Path -and [IO.Path]::GetFullPath($_.Path).Equals($executable, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Process -Id $_.Id -Force
        $_.WaitForExit()
    }
}

$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -Path $runKeyPath -Name 'CodexClaudeUsage' -ErrorAction SilentlyContinue

$shortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Codex + Claude 用量.lnk'
Remove-Item -LiteralPath $shortcutPath -Force -ErrorAction SilentlyContinue

try {
    $settingsPath = Join-Path $HOME '.claude\settings.json'
    if (Test-Path -LiteralPath $settingsPath) {
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        if ($settings.statusLine.command -and $settings.statusLine.command.Contains($bridge)) {
            $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
            Copy-Item -LiteralPath $settingsPath -Destination "$settingsPath.backup-$stamp" -Force
            $settings.PSObject.Properties.Remove('statusLine')
            $json = $settings | ConvertTo-Json -Depth 100
            $temporary = "$settingsPath.tmp"
            [IO.File]::WriteAllText($temporary, $json, (New-Object Text.UTF8Encoding($false)))
            Move-Item -LiteralPath $temporary -Destination $settingsPath -Force
        }
    }
}
catch {
    Write-Warning "無法移除 Claude Code statusLine：$($_.Exception.Message)"
}

$dataRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'CodexClaudeUsage'))
if ($dataRoot.StartsWith([IO.Path]::GetFullPath($env:LOCALAPPDATA), [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $dataRoot)) {
    Remove-Item -LiteralPath $dataRoot -Recurse -Force
}

Remove-Item -LiteralPath $installRoot -Recurse -Force
Write-Host 'Codex + Claude 用量已解除安裝。'
