$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assembly = [Reflection.Assembly]::LoadFile((Join-Path $root 'dist\CodexClaudeUsage.exe'))
$providerType = $assembly.GetType('CodexClaudeUsage.ProviderSnapshot', $true)
$bucketType = $assembly.GetType('CodexClaudeUsage.UsageBucket', $true)
$factoryType = $assembly.GetType('CodexClaudeUsage.TrayIconFactory', $true)
$flags = [Reflection.BindingFlags]'Static,Public,NonPublic'
$summaryMethod = $factoryType.GetMethod('SummaryUsedPercent', $flags)

function New-Bucket([string] $Label, [double] $UsedPercent) {
    $bucket = [Activator]::CreateInstance($bucketType)
    $bucketType.GetProperty('Label').SetValue($bucket, $Label, $null)
    $bucketType.GetProperty('UsedPercent').SetValue($bucket, $UsedPercent, $null)
    return $bucket
}

function New-Provider([object[]] $Buckets, [bool] $Available = $true) {
    $provider = [Activator]::CreateInstance($providerType, @('Test'))
    $providerType.GetProperty('IsAvailable').SetValue($provider, $Available, $null)
    $list = $providerType.GetProperty('Buckets').GetValue($provider, $null)
    foreach ($bucket in $Buckets) {
        $null = $list.Add($bucket)
    }
    return $provider
}

function Summary([object] $Provider) {
    return $summaryMethod.Invoke($null, @($Provider))
}

$weeklyLabel = ([char]0x6BCF).ToString() + [char]0x9031
$currentBucket = New-Bucket -Label 'current session' -UsedPercent 28
$weeklyBucket = New-Bucket -Label $weeklyLabel -UsedPercent 84
$provider = New-Provider -Buckets @($currentBucket, $weeklyBucket)
if ([double](Summary $provider) -ne 84) {
    throw 'The tray summary must prefer the weekly model-total bucket.'
}

$fallback = New-Provider -Buckets @($currentBucket)
if ([double](Summary $fallback) -ne 28) {
    throw 'A provider without a weekly bucket must fall back to its first bucket.'
}

$unavailable = New-Provider -Buckets @($weeklyBucket) -Available $false
if ($null -ne (Summary $unavailable)) {
    throw 'An unavailable provider must not produce a tray summary value.'
}

Write-Host 'PASS: tray icon and tooltip prefer weekly model-total usage'
