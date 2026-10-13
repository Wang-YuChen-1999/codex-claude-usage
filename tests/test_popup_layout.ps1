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

# 三張卡各一列，供應商標頭含獨立的 Token 指標列。
if ($compactHeight -ne 582) {
    throw "Unexpected compact popup height: $compactHeight"
}
if ($detailHeight -le $compactHeight) {
    throw "Detailed popup did not expand for extra usage buckets: $detailHeight"
}
if ($detailHeight -ne 822) {
    throw "Unexpected three-bucket popup height: $detailHeight"
}
# 每張卡最多四列，第五列以後不再增加高度。
if ($capHeight -ne $overflowHeight) {
    throw "Popup must render at most four buckets per provider (cap=$capHeight, overflow=$overflowHeight)."
}
if ($capHeight -le $detailHeight) {
    throw "The fourth bucket row must be visible (cap=$capHeight, detail=$detailHeight)."
}
# Antigravity 的四條限額（Gemini 每週/5h、3P 每週/5h）必須完整佔位。
if ($antigravityHeight -ne 762) {
    throw "Antigravity card did not reserve all four quota rows: $antigravityHeight"
}

# 密度：九列在標準間距下常超出工作區，緊湊節奏必須明顯較矮但仍完整列出九列。
$cardsMethod = $popupType.GetMethod('CardsLogicalHeight', [Reflection.BindingFlags]'Static,NonPublic')
if ($null -eq $cardsMethod) { throw 'CardsLogicalHeight(rows, compact) must remain available for density verification.' }
$standardNine = [int]$cardsMethod.Invoke($null, @(3, 2, 4, $false))
$compactNine = [int]$cardsMethod.Invoke($null, @(3, 2, 4, $true))
if ($standardNine -ne 942) { throw "Unexpected standard nine-row height: $standardNine" }
if ($compactNine -ne 808) { throw "Unexpected compact nine-row height: $compactNine" }
if ($compactNine -ge $standardNine - 100) {
    throw "Compact density must reclaim meaningful height (standard=$standardNine, compact=$compactNine)."
}

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$detailLayoutMethod = $popupType.GetMethod('CalculateBucketDetailLayout', $flags)
$drawBucketMethod = $popupType.GetMethod('DrawBucket', $flags)
$scaleField = $popupType.GetField('_scale', $flags)
$compactField = $popupType.GetField('_compact', $flags)
$hintsField = $popupType.GetField('_hoverHints', $flags)
$resetDetailMethod = $assembly.GetType('CodexClaudeUsage.TimeUtil', $true).GetMethod('ResetDetail')
if ($null -eq $detailLayoutMethod -or $null -eq $resetDetailMethod) {
    throw 'Popup detail layout and truthful reset tooltips must remain available.'
}

$popup = [Activator]::CreateInstance($popupType)
$layoutCases = 0
try {
    # 重設時間、耗盡預測與趨勢圖的區域在窄寬度、高 DPI、兩種密度皆不可交疊。
    foreach ($scale in @([single]1, [single]1.25, [single]1.5, [single]2, [single]2.5, [single]3)) {
        $scaleField.SetValue($popup, $scale)
        foreach ($width in @(120, 320, 420, 520, 760)) {
            foreach ($compact in @($false, $true)) {
                $compactField.SetValue($popup, $compact)
                $left = [int][Math]::Round(20 * $scale)
                $right = [int][Math]::Round(($width - 20) * $scale)
                foreach ($hasTrend in @($false, $true)) {
                    foreach ($exhaustWidth in @([single]0, [single](110 * $scale), [single](260 * $scale))) {
                        $layout = $detailLayoutMethod.Invoke($popup, @($left, $right, 0, $hasTrend, $exhaustWidth))
                        $reset = $layout.ResetBounds
                        $exhaust = $layout.ExhaustBounds
                        $spark = $layout.SparkBounds
                        $areas = @($reset, $exhaust, $spark) | Where-Object { $_.Width -gt 0 }
                        foreach ($area in $areas) {
                            if ($area.Left -lt $left -or $area.Right -gt $right + 0.01 -or $area.Width -lt 0) {
                                throw "Detail area escaped row (scale=$scale, width=$width, compact=$compact): $area"
                            }
                        }
                        if ($exhaust.Width -gt 0 -and $reset.Right -gt $exhaust.Left) {
                            throw "Reset text overlaps exhaustion forecast (scale=$scale, width=$width)."
                        }
                        if ($spark.Width -gt 0 -and ($reset.Right -gt $spark.Left -or ($exhaust.Width -gt 0 -and $exhaust.Right -gt $spark.Left))) {
                            throw "Detail text overlaps trend (scale=$scale, width=$width)."
                        }
                        if (-not $hasTrend -and $spark.Width -gt 0) { throw 'Unexpected reserved trend region.' }
                        if ($exhaustWidth -eq 0 -and $exhaust.Width -gt 0) { throw 'Unexpected reserved forecast region.' }
                        $layoutCases++
                    }
                }
            }
        }
    }

    # 實際繪製包含未知時間與推估值的額度列，完整提示不因文字截斷而消失。
    $scaleField.SetValue($popup, [single]1.5)
    $compactField.SetValue($popup, $false)
    $popup.ClientSize = New-Object Drawing.Size(480, 150)
    $bitmap = New-Object Drawing.Bitmap(480, 150)
    $bitmap.SetResolution(144, 144)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        foreach ($hasReset in @($false, $true)) {
            $bucket = [Activator]::CreateInstance($bucketType)
            $bucket.Label = 'Test quota'
            $bucket.Detail = 'Covered models remain in the tooltip'
            $bucket.UsedPercent = 4
            $bucket.TrendPoints = [double[]]@(5, 10, 8, 12)
            $bucket.ProjectedExhaustAt = [DateTimeOffset]::Now.AddHours(3)
            if ($hasReset) {
                $bucket.ResetsAt = [DateTimeOffset]::Now.AddDays(7)
                $bucket.ResetEstimate = [Enum]::Parse($assembly.GetType('CodexClaudeUsage.ResetEstimateKind'), 'UpperBound')
            }
            $graphics.Clear([Drawing.Color]::Black)
            $null = $drawBucketMethod.Invoke($popup, @($graphics, $bucket, 0, [Drawing.Color]::Turquoise, [double]4))
            $hint = $hintsField.GetValue($popup)[0]
            $expected = [string]$resetDetailMethod.Invoke($null, @($bucket))
            if (-not $hint.Value.Contains($expected) -or -not $hint.Value.Contains($bucket.Detail)) {
                throw "Missing full reset detail or covered models tooltip (hasReset=$hasReset)."
            }
            if ($hint.Key.Right -gt $popup.ClientSize.Width -or $hint.Key.Width -le 0) {
                throw 'Reset tooltip hit area escaped the popup.'
            }
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}
finally {
    $popup.Dispose()
}

Write-Host "PASS: popup height follows visible usage rows (compact=$compactHeight, detail=$detailHeight, cap=$capHeight, antigravity=$antigravityHeight, nine-row standard=$standardNine compact=$compactNine)"
Write-Host "PASS: $layoutCases reset/forecast/trend layouts across narrow widths, high DPI, and compact density; full reset tooltips preserved"
