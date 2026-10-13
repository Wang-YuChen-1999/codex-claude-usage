$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assembly = [Reflection.Assembly]::LoadFile((Join-Path $root 'dist\CodexClaudeUsage.exe'))
$tokenType = $assembly.GetType('CodexClaudeUsage.LocalTokenUsage', $true)
$textType = $assembly.GetType('CodexClaudeUsage.TokenUsageText', $true)
$flags = [Reflection.BindingFlags]'Static,Public,NonPublic'
$methods = @{}
foreach ($name in @('Scope', 'Value', 'Detail')) {
    $methods[$name] = $textType.GetMethod($name, $flags)
    if ($null -eq $methods[$name]) { throw "TokenUsageText.$name is missing." }
}

function New-TokenUsage([long] $InputTokens, [long] $OutputTokens, [long] $TotalTokens, [DateTimeOffset] $UpdatedAt) {
    $usage = [Activator]::CreateInstance($tokenType)
    $usage.InputTokens = $InputTokens
    $usage.OutputTokens = $OutputTokens
    $usage.TotalTokens = $TotalTokens
    $usage.UpdatedAt = $UpdatedAt
    return $usage
}

function Token-Text([string] $Name, [object] $Usage, [bool] $Supported) {
    return [string]$methods[$Name].Invoke($null, [object[]]@($Usage, $Supported))
}

function Assert-Contains([string] $Actual, [string] $Expected, [string] $Description) {
    if (-not $Actual.Contains($Expected)) {
        throw "$Description expected '$Expected'; got '$Actual'."
    }
}

$unsupportedScope = Token-Text 'Scope' $null $false
$unsupportedValue = Token-Text 'Value' $null $false
$unsupportedDetail = Token-Text 'Detail' $null $false
Assert-Contains $unsupportedScope 'Token 計數' 'Unsupported scope'
Assert-Contains $unsupportedValue '來源未提供' 'Unsupported value'
Assert-Contains $unsupportedDetail '未提供可驗證' 'Unsupported detail'
if ($unsupportedValue -match '\d' -or $unsupportedValue -match 'Token$') {
    throw 'An unsupported provider must not appear to report a Token total.'
}

$missingValue = Token-Text 'Value' $null $true
$missingDetail = Token-Text 'Detail' $null $true
Assert-Contains $missingValue '暫無完整紀錄' 'Missing local usage'
Assert-Contains $missingDetail '本機最近工作階段' 'Missing local usage scope'
if ($missingValue -match '\d') { throw 'Missing local usage must not be presented as zero.' }

$today = [DateTimeOffset]::Now
$zero = New-TokenUsage 0 0 0 $today
if ((Token-Text 'Value' $zero $true) -ne '0 Token') {
    throw 'A recorded zero must be distinguishable from missing Token data.'
}
Assert-Contains (Token-Text 'Scope' $zero $true) '今日更新' 'Today label'
Assert-Contains (Token-Text 'Detail' $zero $true) '合計 0 Token' 'Recorded zero detail'
if ((Token-Text 'Value' $zero $false) -ne $unsupportedValue) {
    throw 'Unsupported source must not display Token data even if a stale value exists.'
}

$nineDigits = New-TokenUsage 100000000 23456789 123456789 $today
$expectedNineDigits = 123456789L.ToString('N0', [Globalization.CultureInfo]::CurrentCulture) + ' Token'
if ((Token-Text 'Value' $nineDigits $true) -ne $expectedNineDigits) {
    throw 'A nine-digit Token value must be displayed in full, with culture-aware separators.'
}

$maximum = [long]::MaxValue
$maximumUsage = New-TokenUsage ($maximum - [long]1) 1 $maximum $today
$expectedMaximum = $maximum.ToString('N0', [Globalization.CultureInfo]::CurrentCulture) + ' Token'
if ((Token-Text 'Value' $maximumUsage $true) -ne $expectedMaximum) {
    throw 'Int64 maximum Token value must not be abbreviated or converted to floating point.'
}
$maximumDetail = Token-Text 'Detail' $maximumUsage $true
Assert-Contains $maximumDetail $expectedMaximum 'Int64 maximum in detail'
Assert-Contains $maximumDetail '輸入（含快取）' 'Input and cache definition'
Assert-Contains $maximumDetail '不含其他工作階段與裝置使用量' 'Local-only scope disclaimer'
Assert-Contains $maximumDetail '不代表帳號額度' 'Account quota disclaimer'
Assert-Contains $maximumDetail '不是今日或本週消耗' 'Session total is not a period total'
Assert-Contains $maximumDetail 'Token 紀錄時間與方案額度更新時間分開計算' 'Independent source timestamps'

$oldDate = [DateTimeOffset]::Now.AddYears(-2)
$oldUsage = New-TokenUsage 10 2 12 $oldDate
$expectedOldDate = $oldDate.ToLocalTime().ToString('yyyy/MM/dd', [Globalization.CultureInfo]::CurrentCulture)
$oldScope = Token-Text 'Scope' $oldUsage $true
Assert-Contains $oldScope $expectedOldDate 'Old update date'
Assert-Contains $oldScope '歷史工作階段' 'Historical usage must be explicit'
if ($oldScope.Contains('今日')) { throw 'An old Token update must not be labelled today.' }

$popupSource = [IO.File]::ReadAllText((Join-Path $root 'src\UsagePopup.cs'))
$popupStart = $popupSource.IndexOf('private void DrawProvider(', [StringComparison]::Ordinal)
$popupEnd = $popupSource.IndexOf('private void DrawBucket(', $popupStart, [StringComparison]::Ordinal)
if ($popupStart -lt 0 -or $popupEnd -le $popupStart) { throw 'Cannot locate popup provider drawing block.' }
$popupPaint = $popupSource.Substring($popupStart, $popupEnd - $popupStart)

$centerSource = [IO.File]::ReadAllText((Join-Path $root 'src\UsageCenterForm.cs'))
$centerStart = $centerSource.IndexOf('private sealed class ProviderUsageRow', [StringComparison]::Ordinal)
$centerEnd = $centerSource.IndexOf('private int Px(', $centerStart, [StringComparison]::Ordinal)
if ($centerStart -lt 0 -or $centerEnd -le $centerStart) { throw 'Cannot locate usage-center provider drawing block.' }
$centerPaint = $centerSource.Substring($centerStart, $centerEnd - $centerStart)

foreach ($surface in @(@{ Name = 'Popup'; Source = $popupPaint }, @{ Name = 'Usage center'; Source = $centerPaint })) {
    if ($surface.Source -match 'TokenUsageText\.(Short|Compact)\s*\(') {
        throw "$($surface.Name) still renders the old one-line Token text."
    }
    if ($surface.Source -notmatch 'TokenUsageText\.Scope\s*\(' -or $surface.Source -notmatch 'TokenUsageText\.Value\s*\(') {
        throw "$($surface.Name) must render Token scope and full value as distinct visual elements."
    }
}

Write-Host 'PASS: Token presentation preserves full values, source/recency semantics, and distinct visual scope/value layout'
