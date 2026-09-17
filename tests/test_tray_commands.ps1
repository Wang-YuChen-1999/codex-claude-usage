$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -STA -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$preferencesPath = Join-Path $env:TEMP "CodexClaudeUsage-tray-commands-$PID.json"
$env:CODEX_CLAUDE_USAGE_PREFERENCES_PATH = $preferencesPath
$env:CODEX_CLAUDE_USAGE_KEEP_OPEN = '1'
$context = $null
$showEvent = $null

function Pump-Events([int] $milliseconds) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($milliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 20
    }
}

function New-Provider([Reflection.Assembly] $assembly, [string] $name, [int] $bucketCount) {
    $providerType = $assembly.GetType('CodexClaudeUsage.ProviderSnapshot', $true)
    $bucketType = $assembly.GetType('CodexClaudeUsage.UsageBucket', $true)
    $provider = [Activator]::CreateInstance($providerType, @($name))
    $provider.IsAvailable = $true
    $provider.UpdatedAt = [DateTimeOffset]::Now
    for ($index = 0; $index -lt $bucketCount; $index++) {
        $bucket = [Activator]::CreateInstance($bucketType)
        $bucket.Label = if ($index -eq 1) { ([string][char]0x6BCF) + ([string][char]0x9031) } else { "Window $index" }
        $bucket.UsedPercent = 10 + ($index * 10)
        $provider.Buckets.Add($bucket)
    }
    return $provider
}

try {
    [Windows.Forms.Application]::EnableVisualStyles()
    [Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)
    $assembly = [Reflection.Assembly]::LoadFile((Join-Path $root 'dist\CodexClaudeUsage.exe'))
    $preferencesType = $assembly.GetType('CodexClaudeUsage.UserPreferences', $true)
    $preferences = $preferencesType.GetMethod('Load').Invoke($null, @())
    $preferencesType.GetProperty('TotalOnly').SetValue($preferences, $true, $null)
    $preferencesType.GetProperty('GlobalHotkeysEnabled').SetValue($preferences, $false, $null)
    $preferencesType.GetMethod('Save').Invoke($preferences, @())

    $configType = $assembly.GetType('CodexClaudeUsage.AppConfig', $true)
    $config = $configType.GetMethod('Load').Invoke($null, [object[]]@([string](Join-Path $root 'config.json')))
    $showEvent = New-Object Threading.EventWaitHandle($false, [Threading.EventResetMode]::AutoReset)
    $showEvent = $showEvent.PSObject.BaseObject
    $contextType = $assembly.GetType('CodexClaudeUsage.TrayApplication', $true)
    $constructor = $contextType.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic') | Select-Object -First 1
    $context = $constructor.Invoke([object[]]@($config, [bool]$false, $showEvent))
    $flags = [Reflection.BindingFlags]'Instance,NonPublic'
    $popup = $contextType.GetField('_popup', $flags).GetValue($context)
    $center = $contextType.GetField('_center', $flags).GetValue($context)
    $contextPreferences = $contextType.GetField('_preferences', $flags).GetValue($context)
    $detailItem = $contextType.GetField('_detailItem', $flags).GetValue($context)
    $centerItem = $contextType.GetField('_centerItem', $flags).GetValue($context)
    $menu = $contextType.GetField('_menu', $flags).GetValue($context)

    $snapshotType = $assembly.GetType('CodexClaudeUsage.UsageSnapshot', $true)
    $snapshot = [Activator]::CreateInstance($snapshotType)
    $snapshot.UpdatedAt = [DateTimeOffset]::Now
    $snapshot.Codex = New-Provider $assembly 'Codex' 3
    $snapshot.Claude = New-Provider $assembly 'Claude Code' 2
    $contextType.GetField('_latestSnapshot', $flags).SetValue($context, $snapshot)
    $selectionType = $assembly.GetType('CodexClaudeUsage.UsageSelection', $true)
    $selection = $selectionType.GetMethod('ForDisplay', [Reflection.BindingFlags]'Static,Public,NonPublic')
    $popup.GetType().GetMethod('SetSnapshot').Invoke($popup, @($selection.Invoke($null, @($snapshot, $true))))

    if ($popup.Visible -or $center.Visible) {
        throw 'Command surfaces must start hidden.'
    }

    $menu.Show([Drawing.Point]::new(120, 120))
    Pump-Events 120
    $detailItem.PerformClick()
    Pump-Events 1000
    if ($menu.Visible) {
        throw 'Tray menu remained open after the short-window command.'
    }
    if (-not $detailItem.Checked) {
        throw 'Short-window command did not expose its checked state.'
    }
    if ([bool]$preferencesType.GetProperty('TotalOnly').GetValue($contextPreferences, $null)) {
        throw 'Short-window command did not update the in-memory preference.'
    }
    if (-not $popup.Visible) {
        throw 'Short-window command did not open the usage popup with the new mode.'
    }
    $displaySnapshot = $popup.GetType().GetField('_snapshot', $flags).GetValue($popup)
    if ($displaySnapshot.Codex.Buckets.Count -ne 3 -or $displaySnapshot.Claude.Buckets.Count -ne 2) {
        throw 'Short-window command did not restore all provider buckets.'
    }

    $popup.Hide()
    $menu.Show([Drawing.Point]::new(120, 120))
    Pump-Events 120
    $centerItem.PerformClick()
    Pump-Events 1000
    if ($menu.Visible) {
        throw 'Tray menu remained open after the usage-center command.'
    }
    if (-not $center.Visible -or $center.WindowState -eq [Windows.Forms.FormWindowState]::Minimized) {
        throw 'Usage-center command did not display a usable window.'
    }
    if ($center.TopMost) {
        throw 'Usage center remained permanently topmost after activation.'
    }

    $loaded = $preferencesType.GetMethod('Load').Invoke($null, @())
    if ([bool]$preferencesType.GetProperty('TotalOnly').GetValue($loaded, $null)) {
        throw 'Short-window preference was not persisted.'
    }

    Write-Host 'PASS: tray commands open short-window usage and the usage center with persisted state'
}
finally {
    if ($null -ne $context) { $context.Dispose() }
    if ($null -ne $showEvent) { $showEvent.Dispose() }
    Remove-Item Env:\CODEX_CLAUDE_USAGE_PREFERENCES_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:\CODEX_CLAUDE_USAGE_KEEP_OPEN -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $preferencesPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath "$preferencesPath.tmp" -Force -ErrorAction SilentlyContinue
}
