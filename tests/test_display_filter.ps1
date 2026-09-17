$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assembly = [Reflection.Assembly]::LoadFile((Join-Path $root 'dist\CodexClaudeUsage.exe'))
$providerType = $assembly.GetType('CodexClaudeUsage.ProviderSnapshot', $true)
$bucketType = $assembly.GetType('CodexClaudeUsage.UsageBucket', $true)
$snapshotType = $assembly.GetType('CodexClaudeUsage.UsageSnapshot', $true)
$selectionType = $assembly.GetType('CodexClaudeUsage.UsageSelection', $true)
$flags = [Reflection.BindingFlags]'Static,Public,NonPublic'

function New-Provider([string] $Name) {
    $provider = [Activator]::CreateInstance($providerType, @($Name))
    $provider.IsAvailable = $true
    $provider.Status = 'ready'
    $provider.UpdatedAt = [DateTimeOffset]::UtcNow
    $provider.SourceKind = [Enum]::Parse($assembly.GetType('CodexClaudeUsage.UsageSourceKind', $true), 'CodexAppServer')

    $short = [Activator]::CreateInstance($bucketType)
    $short.Label = '5 hour'
    $short.UsedPercent = 75
    $provider.Buckets.Add($short)

    $weekly = [Activator]::CreateInstance($bucketType)
    $weekly.Label = ([string][char]0x6BCF) + ([string][char]0x9031)
    $weekly.UsedPercent = 25
    $provider.Buckets.Add($weekly)
    return $provider
}

$snapshot = [Activator]::CreateInstance($snapshotType)
$snapshot.Codex = New-Provider 'Codex'
$snapshot.Claude = New-Provider 'Claude Code'
$method = $selectionType.GetMethod('ForDisplay', $flags)
$filtered = $method.Invoke($null, @($snapshot, $true))

if ($filtered.Codex.Buckets.Count -ne 1 -or [double]$filtered.Codex.Buckets[0].UsedPercent -ne 25) {
    throw 'Total-only display did not retain only the weekly Codex bucket.'
}
if ($filtered.Claude.Buckets.Count -ne 1 -or $filtered.Codex.Status -ne 'ready' -or $snapshot.Codex.Buckets.Count -ne 2) {
    throw 'Display filtering lost provider metadata or mutated the source snapshot.'
}
$unfiltered = $method.Invoke($null, @($snapshot, $false))
if (-not [object]::ReferenceEquals($snapshot, $unfiltered)) {
    throw 'Detailed display should reuse the unfiltered snapshot.'
}

Write-Host 'PASS: total-only display filtering preserves source data and metadata'
