$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -STA -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}

Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assembly = [Reflection.Assembly]::LoadFile((Join-Path $root 'dist\CodexClaudeUsage.exe'))
$type = $assembly.GetType('CodexClaudeUsage.DropletWindow', $true)
$constructorFlags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$constructor = $type.GetConstructors($constructorFlags) | Select-Object -First 1
$finish = $type.GetMethod('Finish', [Reflection.BindingFlags]'Instance,NonPublic')

$script:abortedCount = 0
$abortedAction = [Action]{ $script:abortedCount++ }
$aborted = $constructor.Invoke([object[]]@([Drawing.Point]::new(100, 100), 12, 80, 300, $abortedAction))
try {
    [void]$finish.Invoke($aborted, [object[]]@($true))
    if ($script:abortedCount -ne 0) { throw 'An aborted droplet must not open the usage panel.' }
}
finally {
    $aborted.Dispose()
}

$script:landedCount = 0
$landedAction = [Action]{ $script:landedCount++ }
$completed = $constructor.Invoke([object[]]@([Drawing.Point]::new(100, 100), 12, 80, 300, $landedAction))
try {
    [void]$finish.Invoke($completed, [object[]]@($false))
    [void]$finish.Invoke($completed, [object[]]@($false))
    if ($script:landedCount -ne 1) { throw 'A completed droplet must open the usage panel exactly once.' }
}
finally {
    $completed.Dispose()
}

Write-Host 'PASS: droplet abort suppression and single successful completion callback'
