$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assembly = [Reflection.Assembly]::LoadFile((Join-Path $root 'dist\CodexClaudeUsage.exe'))
$bucketType = $assembly.GetType('CodexClaudeUsage.UsageBucket', $true)
$kindType = $assembly.GetType('CodexClaudeUsage.ResetEstimateKind', $true)
$timeType = $assembly.GetType('CodexClaudeUsage.TimeUtil', $true)
$labelMethod = $timeType.GetMethod('ResetLabel', [type[]]@($bucketType))
$detailMethod = $timeType.GetMethod('ResetDetail', [type[]]@($bucketType))
$culture = [Threading.Thread]::CurrentThread.CurrentCulture

function Text([string]$escaped) { return [regex]::Unescape($escaped) }
function Assert-Contains([string]$value, [string]$expected, [string]$name) {
    if (-not $value.Contains($expected)) { throw "$name failed: $value" }
}
function Label($bucket) { return [string]$labelMethod.Invoke($null, [object[]]@($bucket)) }
function Detail($bucket) { return [string]$detailMethod.Invoke($null, [object[]]@($bucket)) }
function Bucket([double]$used, [string]$kind, $reset) {
    $bucket = [Activator]::CreateInstance($bucketType)
    $bucket.UsedPercent = $used
    $bucket.ResetEstimate = [Enum]::Parse($kindType, $kind)
    if ($null -ne $reset) { $bucket.ResetsAt = $reset }
    return $bucket
}

try {
    [Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('zh-TW')
    $now = [DateTimeOffset]::Now
    $nextWeek = $now.AddDays(7)
    $idleText = Text '\u5c1a\u672a\u958b\u59cb\u8a08\u6642'
    $estimateText = Text '\u63a8\u4f30'
    $pendingText = Text '\u7b49\u5f85\u540c\u6b65'

    $idle = Bucket 0 'UpperBound' $nextWeek
    if ((Label $idle) -ne $idleText) { throw 'An unused rolling upper bound must not be a fixed countdown.' }
    Assert-Contains (Detail $idle) ($nextWeek.ToString('yyyy/MM/dd')) 'Idle source upper bound'
    Assert-Contains (Detail $idle) (Text '\u4e0d\u662f') 'Idle disclaimer'

    $active = Bucket 4 'UpperBound' $nextWeek
    Assert-Contains (Label $active) ($nextWeek.ToString('MM/dd')) 'Next week must include a date'
    Assert-Contains (Label $active) $estimateText 'Upper bound must remain estimated'
    Assert-Contains (Detail $active) (Text '\u6700\u665a\u65bc') 'Upper-bound tooltip'
    if ($active.UsedPercent -ne 4 -or $active.ResetsAt -ne $nextWeek) { throw 'Formatting must not change the source data.' }

    $fixed = Bucket 0 'None' $nextWeek
    if ((Label $fixed) -eq $idleText) { throw 'A zero-usage fixed deadline must remain visible.' }
    Assert-Contains (Label $fixed) ($nextWeek.ToString('MM/dd')) 'Fixed deadline date'

    foreach ($kind in @('None', 'UpperBound', 'Cycle')) {
        $expired = Bucket 25 $kind ($now.AddHours(-1))
        Assert-Contains (Label $expired) $pendingText "Expired $kind"
        if ((Label $expired).Contains((Text '\u5373\u5c07'))) { throw 'An expired reset must not be described as upcoming.' }
        Assert-Contains (Detail $expired) (Text '\u4fdd\u7559\u6700\u5f8c\u56de\u5831') 'Expired source value preservation'
        if ($expired.UsedPercent -ne 25) { throw 'Expired data must not reset usage to zero.' }
    }
    $futureCycle = Bucket 25 'Cycle' $nextWeek
    Assert-Contains (Label $futureCycle) $estimateText 'Cycle estimate'

    foreach ($kind in @('None', 'UpperBound')) {
        $crossYear = $now.AddYears(1)
        Assert-Contains (Label (Bucket 5 $kind $crossYear)) ($crossYear.ToString('yyyy/MM/dd')) 'Cross-year date'
    }
    $missing = Bucket 4 'None' $null
    Assert-Contains (Label $missing) (Text '\u672a\u77e5') 'Missing reset'
    Assert-Contains (Detail $missing) (Text '\u672a\u63d0\u4f9b') 'Missing reset source limitation'
    Assert-Contains (Label (Bucket 0 'None' $null)) (Text '\u6c92\u6709\u9032\u884c\u4e2d') 'Inactive window'
    Write-Host 'PASS: reset dates, idle upper bounds, expired deadlines, source preservation, and cross-year labels'
}
finally { [Threading.Thread]::CurrentThread.CurrentCulture = $culture }
