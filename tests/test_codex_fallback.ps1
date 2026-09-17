$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$testRoot = Join-Path $env:TEMP "CodexClaudeUsage-fallback-$PID"
$snapshotPath = Join-Path $testRoot 'snapshot.json'
$executable = Join-Path $testRoot 'CodexClaudeUsage.exe'

try {
    New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'dist\CodexClaudeUsage.exe') -Destination $executable
    Copy-Item -LiteralPath (Join-Path $root 'dist\CodexClaudeUsage.exe.config') -Destination "$executable.config"
    [IO.File]::WriteAllText((Join-Path $testRoot 'config.json'), '{"codexExecutable":"Z:\\missing\\codex.exe","refreshSeconds":60}')

    Start-Process -FilePath $executable -ArgumentList @('--snapshot', $snapshotPath) -WindowStyle Hidden -Wait
    $snapshot = [IO.File]::ReadAllText($snapshotPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    if (-not $snapshot.codex.available) { throw "Codex fallback 無法讀取：$($snapshot.codex.error)" }
    if ($snapshot.codex.status -notlike '本機工作階段*') { throw "未使用 JSONL fallback：$($snapshot.codex.status)" }
    if ($snapshot.codex.source -ne 'CodexSessionLog') { throw "Fallback 來源類型錯誤：$($snapshot.codex.source)" }
    Write-Host "Fallback: $($snapshot.codex.status)"
    Write-Host 'PASS: bounded Codex JSONL fallback'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolved = [IO.Path]::GetFullPath($testRoot)
        $tempRoot = [IO.Path]::GetFullPath($env:TEMP)
        if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $testRoot -Recurse -Force
        }
    }
}
