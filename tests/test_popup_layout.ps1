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
$popupType = $assembly.GetType('CodexClaudeUsage.UsagePopup', $true)
$heightMethod = $popupType.GetMethod('CalculateLogicalHeight', [Reflection.BindingFlags]'Static,NonPublic')
if ($null -eq $heightMethod) { throw 'CalculateLogicalHeight(UsageSnapshot) must remain a static helper for layout verification.' }

function New-Provider([string] $name, [int] $bucketCount) {
    $provider = [Activator]::CreateInstance($providerType, @($name))
    $provider.IsAvailable = $true
    for ($index = 0; $index -lt $bucketCount; $index++) {
        $bucket = [Activator]::CreateInstance($bucketType)
        $bucket.Label = "Bucket $index"
        $bucket.UsedPercent = 10 + $index
        $provider.Buckets.Add($bucket)
    }
    return $provider
}

function New-Snapshot([int] $codexBuckets, [int] $claudeBuckets, [int] $antigravityBuckets = 0) {
    $snapshot = [Activator]::CreateInstance($snapshotType)
    $snapshot.Codex = New-Provider 'Codex' $codexBuckets
    $snapshot.Claude = New-Provider 'Claude Code' $claudeBuckets
    if ($antigravityBuckets -gt 0) {
        $snapshot.Antigravity = New-Provider 'Antigravity' $antigravityBuckets
    }
    return $snapshot
}

$compactHeight = [int]$heightMethod.Invoke($null, @((New-Snapshot 1 1)))
$detailHeight = [int]$heightMethod.Invoke($null, @((New-Snapshot 3 3)))
$capHeight = [int]$heightMethod.Invoke($null, @((New-Snapshot 4 4)))
$overflowHeight = [int]$heightMethod.Invoke($null, @((New-Snapshot 6 6)))
$antigravityHeight = [int]$heightMethod.Invoke($null, @((New-Snapshot 1 1 4)))

# 三張卡各一列（Codex、Claude、Antigravity 空卡）：70 + 136 x 3 + 10 + 46
if ($compactHeight -ne 534) {
    throw "Unexpected compact popup height: $compactHeight"
}
if ($detailHeight -le $compactHeight) {
    throw "Detailed popup did not expand for extra usage buckets: $detailHeight"
}
if ($detailHeight -ne 774) {
    throw "Unexpected three-bucket popup height: $detailHeight"
}
# 每張卡最多四列，第五列以後不再增加高度。
if ($capHeight -ne $overflowHeight) {
    throw "Popup must render at most four buckets per provider (cap=$capHeight, overflow=$overflowHeight)."
}
if ($capHeight -le $detailHeight) {
    throw "The fourth bucket row must be visible (cap=$capHeight, detail=$detailHeight)."
}
# Antigravity 的四條限額（Gemini 每週/5h、3P 每週/5h）必須完整佔位：70 + 136 + 136 + 316 + 56
if ($antigravityHeight -ne 714) {
    throw "Antigravity card did not reserve all four quota rows: $antigravityHeight"
}

# 密度：九列在標準間距下常超出工作區，緊湊節奏必須明顯較矮但仍完整列出九列。
$cardsMethod = $popupType.GetMethod('CardsLogicalHeight', [Reflection.BindingFlags]'Static,NonPublic')
if ($null -eq $cardsMethod) { throw 'CardsLogicalHeight(rows, compact) must remain available for density verification.' }
$standardNine = [int]$cardsMethod.Invoke($null, @(3, 2, 4, $false))
$compactNine = [int]$cardsMethod.Invoke($null, @(3, 2, 4, $true))
if ($standardNine -ne 894) { throw "Unexpected standard nine-row height: $standardNine" }
if ($compactNine -ne 760) { throw "Unexpected compact nine-row height: $compactNine" }
if ($compactNine -ge $standardNine - 100) {
    throw "Compact density must reclaim meaningful height (standard=$standardNine, compact=$compactNine)."
}

Write-Host "PASS: popup height follows visible usage rows (compact=$compactHeight, detail=$detailHeight, cap=$capHeight, antigravity=$antigravityHeight, nine-row standard=$standardNine compact=$compactNine)"
