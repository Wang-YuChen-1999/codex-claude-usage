[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root 'dist'
$programsRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
$installRoot = [IO.Path]::GetFullPath((Join-Path $programsRoot 'CodexClaudeUsage'))
$executable = Join-Path $installRoot 'CodexClaudeUsage.exe'
$bridge = Join-Path $installRoot 'claude_status_bridge.ps1'

if (-not $installRoot.StartsWith($programsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "安裝路徑不在使用者 Programs 目錄內：$installRoot"
}

if (-not $NoBuild) {
    & (Join-Path $root 'build.ps1') -Clean
}
if (-not (Test-Path -LiteralPath (Join-Path $dist 'CodexClaudeUsage.exe'))) {
    throw '找不到已編譯的 CodexClaudeUsage.exe'
}

$distConfigPath = Join-Path $dist 'config.json'
try {
    $distConfig = Get-Content -LiteralPath $distConfigPath -Raw | ConvertFrom-Json
    if (-not $distConfig.codexExecutable -or -not (Test-Path -LiteralPath $distConfig.codexExecutable -PathType Leaf)) {
        Write-Warning 'config.json 的 codexExecutable 無效；程式將改用本機工作階段後備資料。'
    }
    else {
        $codexVersion = & $distConfig.codexExecutable --version 2>&1
        if ($LASTEXITCODE -ne 0 -or -not ($codexVersion -match '^codex-cli ')) {
            Write-Warning '設定的 codexExecutable 未通過 CLI 版本檢查。'
        }
    }
}
catch {
    Write-Warning "無法驗證 Codex CLI：$($_.Exception.Message)"
}

Get-Process -Name 'CodexClaudeUsage' -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.Path -and [IO.Path]::GetFullPath($_.Path).Equals($executable, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Process -Id $_.Id -Force
        $_.WaitForExit()
    }
}

New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $dist 'CodexClaudeUsage.exe') -Destination $executable -Force
Copy-Item -LiteralPath (Join-Path $dist 'CodexClaudeUsage.exe.config') -Destination "$executable.config" -Force
if (-not (Test-Path -LiteralPath (Join-Path $installRoot 'config.json'))) {
    Copy-Item -LiteralPath (Join-Path $dist 'config.json') -Destination (Join-Path $installRoot 'config.json')
}
Copy-Item -LiteralPath (Join-Path $root 'scripts\claude_status_bridge.ps1') -Destination $bridge -Force
Copy-Item -LiteralPath (Join-Path $root 'uninstall.ps1') -Destination (Join-Path $installRoot 'uninstall.ps1') -Force

$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Set-ItemProperty -Path $runKeyPath -Name 'CodexClaudeUsage' -Value ('"{0}" --background' -f $executable)

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shortcutPath = Join-Path $startMenu 'Codex + Claude 用量.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $executable
$shortcut.WorkingDirectory = $installRoot
$shortcut.Description = 'Codex 與 Claude Code 方案用量'
$shortcut.Save()

try {
    $claudeDirectory = Join-Path $HOME '.claude'
    $settingsPath = Join-Path $claudeDirectory 'settings.json'
    New-Item -ItemType Directory -Force -Path $claudeDirectory | Out-Null

    if (Test-Path -LiteralPath $settingsPath) {
        $rawSettings = Get-Content -LiteralPath $settingsPath -Raw
        $settings = if ([string]::IsNullOrWhiteSpace($rawSettings)) { [pscustomobject]@{} } else { $rawSettings | ConvertFrom-Json }
    }
    else {
        $settings = [pscustomobject]@{}
    }

    $existing = $settings.statusLine
    if (-not $existing) {
        if (Test-Path -LiteralPath $settingsPath) {
            $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
            Copy-Item -LiteralPath $settingsPath -Destination "$settingsPath.backup-$stamp" -Force
        }

        $command = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "{0}"' -f $bridge
        $statusLine = [pscustomobject]@{
            type = 'command'
            command = $command
            padding = 0
        }
        $settings | Add-Member -NotePropertyName 'statusLine' -NotePropertyValue $statusLine -Force
        $json = $settings | ConvertTo-Json -Depth 100
        $temporary = "$settingsPath.tmp"
        [IO.File]::WriteAllText($temporary, $json, (New-Object Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $temporary -Destination $settingsPath -Force
        Write-Host 'Claude Code 狀態列橋接已啟用。'
    }
    elseif ($existing.command -and $existing.command.Contains($bridge)) {
        Write-Host 'Claude Code 狀態列橋接已存在。'
    }
    else {
        Write-Warning '偵測到既有 Claude Code statusLine，為避免覆寫已保留原設定；Claude 重設時間暫不會同步。'
    }
}
catch {
    Write-Warning "無法自動設定 Claude Code statusLine：$($_.Exception.Message)"
}

if (-not $NoLaunch) {
    Start-Process -FilePath $executable -WorkingDirectory $installRoot
}

Write-Host "Installed: $executable"
Write-Host "Startup: enabled"
