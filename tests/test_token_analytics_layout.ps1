param([string] $ScreenshotDirectory)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -STA -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path -ScreenshotDirectory $ScreenshotDirectory
    exit $LASTEXITCODE
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

function Pump-Events {
    1..8 | ForEach-Object { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 10 }
}

function Assert-True([bool] $condition, [string] $message) {
    if (-not $condition) { throw $message }
}

function Read-Field($instance, [string] $name) {
    return $instance.GetType().GetField($name, [Reflection.BindingFlags]'Instance,NonPublic').GetValue($instance)
}

function Select-Choice($combo, [string] $value) {
    for ($index = 0; $index -lt $combo.Items.Count; $index++) {
        if ([string]$combo.Items[$index] -eq $value) { $combo.SelectedIndex = $index; return }
    }
    throw "Filter choice not found: $value"
}

function Assert-DarkComboSelection($combo) {
    $bitmap = [Drawing.Bitmap]::new($combo.Width, $combo.Height)
    try {
        $combo.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height))
        $bright = 0
        $samples = 0
        $right = [Math]::Max(5, $bitmap.Width - [Math]::Max(40, [int]($bitmap.Width * 0.2)))
        for ($y = 4; $y -lt $bitmap.Height - 4; $y++) {
            for ($x = 5; $x -lt $right; $x++) {
                $pixel = $bitmap.GetPixel($x, $y)
                if ($pixel.R -gt 170 -and $pixel.G -gt 170 -and $pixel.B -gt 170) { $bright++ }
                $samples++
            }
        }
        Assert-True ($samples -gt 0 -and $bright -lt $samples * 0.35) 'Rendered combo selection must not contain a white native background.'
        Assert-True ($bright -gt 4) 'Rendered combo selection must preserve its selected text.'
    }
    finally { $bitmap.Dispose() }
}

function New-Entry([DateTime] $date, [string] $provider, [string] $model, [string] $project, [long] $inputCount, [long] $readCount, [long] $writeCount, [long] $outputCount) {
    $entry = [Activator]::CreateInstance($script:entryType, $true)
    $entry.Date = $date
    $entry.Provider = $provider
    $entry.Model = $model
    $entry.Project = $project
    $entry.InputTokens = $inputCount
    $entry.CacheReadTokens = $readCount
    $entry.CacheWriteTokens = $writeCount
    $entry.OutputTokens = $outputCount
    return $entry
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assembly = [Reflection.Assembly]::LoadFile((Join-Path $root 'dist\CodexClaudeUsage.exe'))
$entryType = $assembly.GetType('CodexClaudeUsage.TokenUsageEntry', $true)
$snapshotType = $assembly.GetType('CodexClaudeUsage.TokenAnalyticsSnapshot', $true)
$formType = $assembly.GetType('CodexClaudeUsage.UsageCenterForm', $true)
$preferencesType = $assembly.GetType('CodexClaudeUsage.UserPreferences', $true)
$configType = $assembly.GetType('CodexClaudeUsage.AppConfig', $true)
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$form = $null

try {
    [Windows.Forms.Application]::EnableVisualStyles()
    $config = $configType.GetMethod('Load').Invoke($null, [object[]]@([string](Join-Path $root 'config.json')))
    $preferences = [Activator]::CreateInstance($preferencesType, $true)
    $constructor = $formType.GetConstructors($flags) | Select-Object -First 1
    $form = $constructor.Invoke([object[]]@($config, $preferences))
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = [Drawing.Point]::new(-20000, -20000)
    $form.ShowInTaskbar = $false
    $form.Show()
    $selectTab = $formType.GetMethod('SelectTab', $flags)
    $selectTab.Invoke($form, [object[]]@(4)) | Out-Null
    Pump-Events

    $panel = Read-Field $form '_tokenAnalyticsPage'
    $tabs = Read-Field $form '_tabs'
    Assert-True ($tabs.TabCount -eq 5) 'Token analytics must be the fifth navigation page.'
    Assert-True $panel.Visible 'Selecting tab 4 must expose token analytics.'
    $selectTab.Invoke($form, [object[]]@(0)) | Out-Null
    $keyArguments = [object[]]@([Windows.Forms.Message]::new(), [Windows.Forms.Keys]([int][Windows.Forms.Keys]::Control -bor [int][Windows.Forms.Keys]::D5))
    $handled = $formType.GetMethod('ProcessCmdKey', $flags).Invoke($form, $keyArguments)
    Assert-True ([bool]$handled -and $panel.Visible) 'Ctrl+5 must activate token analytics.'
    foreach ($other in @('_overviewPage', '_historyPage', '_healthPage', '_settingsPage')) {
        Assert-True (-not (Read-Field $form $other).Visible) 'Token analytics must have exclusive page visibility.'
    }

    $snapshot = [Activator]::CreateInstance($snapshotType, $true)
    $today = [DateTime]::Today
    $snapshot.CapturedAt = [DateTimeOffset]::Now
    $snapshot.LastRecordAt = [DateTimeOffset]::Now
    $snapshot.CoverageStart = $today.AddDays(-60)
    $snapshot.IsComplete = $true
    $snapshot.Status = 'Fixture: all available local records indexed.'
    $snapshot.Rows.Add((New-Entry $today 'Codex' 'model-a' 'project-a' 100 20 10 30))
    $snapshot.Rows.Add((New-Entry $today 'Claude' 'model-b' 'project-b' 70 10 0 20))
    $snapshot.Rows.Add((New-Entry $today.AddDays(-1) 'Codex' 'model-a' 'project-a' 200 0 0 40))
    $snapshot.Rows.Add((New-Entry $today.AddDays(-2) 'Codex' 'model-b' 'project-b' 10 5 0 5))
    $snapshot.Rows.Add((New-Entry $today.AddDays(-3) 'Codex' 'model-a' 'project-a' 500 0 0 0))
    $setSnapshot = $formType.GetMethod('SetTokenAnalytics', $flags)
    $setSnapshot.Invoke($form, [object[]]@($snapshot)) | Out-Null

    $period = Read-Field $panel '_period'
    $start = Read-Field $panel '_start'
    $end = Read-Field $panel '_end'
    $provider = Read-Field $panel '_provider'
    $model = Read-Field $panel '_model'
    $project = Read-Field $panel '_project'
    $getRows = $panel.GetType().GetMethod('GetFilteredRows', $flags)

    $period.SelectedIndex = 0
    Assert-True ($start.Value.Date -eq $today -and $end.Value.Date -eq $today) 'Today range is incorrect.'
    Assert-True (@($getRows.Invoke($panel, @())).Count -eq 2) 'Today filter must include both providers.'
    $period.SelectedIndex = 1
    $weekStart = $today.AddDays(-(([int]$today.DayOfWeek + 6) % 7))
    Assert-True ($start.Value.Date -eq $weekStart) 'Week must start on Monday.'
    $period.SelectedIndex = 2
    Assert-True ($start.Value.Day -eq 1 -and $start.Value.Month -eq $today.Month) 'Month range is incorrect.'
    $period.SelectedIndex = 3
    Assert-True (($end.Value.Date - $start.Value.Date).Days -eq 29) 'Thirty-day range must include exactly 30 dates.'
    $period.SelectedIndex = 4
    $start.Value = $today.AddDays(-2)
    $end.Value = $today
    Assert-True ($start.Enabled -and $end.Enabled) 'Custom date controls must be enabled.'
    Assert-True ($start.MinDate.Date -eq $snapshot.CoverageStart.Date -and $end.MaxDate.Date -eq $today) 'Custom dates must respect local coverage and today.'
    $rows = @($getRows.Invoke($panel, @()))
    Assert-True ($rows.Count -eq 4) 'Custom date boundaries must be inclusive.'
    Assert-True ((Read-Field $panel '_total').Text -match '520') 'Custom range total must include input, cache reads, cache writes and output exactly once.'
    $models = Read-Field $panel '_models'
    Assert-True ($models.Items[0].SubItems[1].Text -eq '400') 'Model totals must sort numerically descending.'
    $onColumnClick = [Windows.Forms.ListView].GetMethod('OnColumnClick', $flags)
    $onColumnClick.Invoke($models, [object[]]@([Windows.Forms.ColumnClickEventArgs]::new(1))) | Out-Null
    Assert-True ($models.Items[0].SubItems[1].Text -eq '20') 'Clicking Token must reverse numeric sorting.'

    Select-Choice $provider 'Codex'
    Select-Choice $model 'model-a'
    Select-Choice $project 'project-a'
    $rows = @($getRows.Invoke($panel, @()))
    Assert-True ($rows.Count -eq 2 -and ($rows | Where-Object { $_.Provider -ne 'Codex' -or $_.Model -ne 'model-a' -or $_.Project -ne 'project-a' }).Count -eq 0) 'Combined filters must select the exact rows.'

    $script:exportedRows = $null
    $event = $formType.GetEvent('ExportTokensCsvRequested', $flags)
    $callback = [Management.Automation.LanguagePrimitives]::ConvertTo([scriptblock]{ param($exportRows) $script:exportedRows = $exportRows }, $event.EventHandlerType)
    $event.AddEventHandler($form, $callback)
    $panel.GetType().GetMethod('ExportCurrentRows', $flags).Invoke($panel, @()) | Out-Null
    Assert-True ($null -ne $script:exportedRows -and $script:exportedRows.Count -eq 2) 'Export event must forward only the filtered rows through the center.'
    $csvArguments = New-Object object[] 1
    $csvArguments[0] = $script:exportedRows
    $csv = $assembly.GetType('CodexClaudeUsage.TokenAnalyticsReport', $true).GetMethod('ToCsv').Invoke($null, $csvArguments)
    $parsed = @($csv | ConvertFrom-Csv)
    Assert-True ($parsed.Count -eq 2 -and ($parsed | Where-Object { $_.Project -ne 'project-a' }).Count -eq 0) 'CSV must match the selected date/model/project filters.'

    $budget = Read-Field $panel '_budget'
    $budget.Value = 1000
    Assert-True ($preferences.TokenDailyBudget -eq 1000) 'Budget changes must update preferences.'
    Assert-True ((Read-Field $panel '_budgetStatus').Text -match '260') 'Budget must count all today providers regardless of filters.'
    $completeBudgetText = (Read-Field $panel '_budgetStatus').Text
    Assert-True ($budget.Maximum -eq 9000000000000000) 'Budget must support large integer values.'

    $empty = [Activator]::CreateInstance($snapshotType, $true)
    $empty.CapturedAt = [DateTimeOffset]::Now
    $empty.CoverageStart = $today.AddDays(-60)
    $empty.IsComplete = $false
    $empty.IsIndexing = $true
    $empty.Status = 'Indexing fixture records.'
    $setSnapshot.Invoke($form, [object[]]@($empty)) | Out-Null
    Assert-True ((Read-Field $panel '_total').Text -notmatch '0\s+Token') 'Incomplete empty scans must not claim zero consumption.'
    Assert-True ((Read-Field $panel '_comparison').Text -notmatch '%') 'Incomplete scans must not claim a usage decrease.'
    Assert-True (-not (Read-Field $panel '_export').Enabled) 'Empty exports must be disabled.'
    $snapshot.IsComplete = $false
    $snapshot.CompleteFrom = $today
    $setSnapshot.Invoke($form, [object[]]@($snapshot)) | Out-Null
    Assert-True ((Read-Field $panel '_budgetStatus').Text -eq $completeBudgetText) 'Older broken records must not disable a confirmed-complete today budget.'
    Assert-True ((Read-Field $panel '_comparison').Text -notmatch '%') 'A complete today must not imply complete previous-period records.'
    $snapshot.IsComplete = $true
    $snapshot.CompleteFrom = $null
    $snapshot.CoverageStart = $today.AddDays(-2)
    $setSnapshot.Invoke($form, [object[]]@($snapshot)) | Out-Null
    Assert-True ((Read-Field $panel '_comparison').Text -notmatch '%') 'Comparison must be unavailable when previous dates exceed coverage.'

    foreach ($size in @([Drawing.Size]::new(900, 620), $form.MinimumSize, [Drawing.Size]::new(1280, 900))) {
        $form.Size = $size
        Pump-Events
        $panel.AutoScrollPosition = [Drawing.Point]::Empty
        $panel.PerformLayout()
        $layout = Read-Field $panel '_layout'
        $layout.PerformLayout()
        Pump-Events
        Assert-True ($panel.Visible -and $panel.Width -gt 200 -and $panel.Height -gt 200) 'Analytics page must remain reachable at every window size.'
        Assert-True (-not $panel.HorizontalScroll.Visible) 'Analytics page must not require horizontal scrolling.'
        foreach ($comboName in @('_period', '_provider', '_model', '_project')) {
            Assert-DarkComboSelection (Read-Field $panel $comboName)
        }
        foreach ($child in $layout.Controls) {
            Assert-True ($child.Left -ge 0 -and $child.Right -le $layout.ClientSize.Width + 1) "Section exceeds width at $size : $($child.GetType().Name)"
        }
        Assert-True ($panel.VerticalScroll.Visible) 'Long analytics sections must be reachable by vertical scrolling.'
        foreach ($listName in @('_models', '_projects')) {
            $tokenList = Read-Field $panel $listName
            $columnWidth = ($tokenList.Columns | Measure-Object -Property Width -Sum).Sum
            Assert-True ($columnWidth -lt $tokenList.ClientSize.Width) 'Token tables must leave room for native list edges without horizontal scrolling.'
        }
        $export = Read-Field $panel '_export'
        $exportTop = $panel.PointToClient($export.PointToScreen([Drawing.Point]::Empty)).Y
        Assert-True ($exportTop -ge 0 -and $exportTop -lt $panel.ClientSize.Height) 'CSV export must be reachable near the top without scrolling.'
        $bitmap = [Drawing.Bitmap]::new($panel.Width, $panel.Height)
        try {
            $panel.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height))
            $dark = $bitmap.GetPixel(1, 1)
            Assert-True (($dark.R + $dark.G + $dark.B) -lt 300) 'Analytics page must preserve the dark native surface.'
        }
        finally { $bitmap.Dispose() }
        if ($ScreenshotDirectory) {
            $bitmap = [Drawing.Bitmap]::new($form.Width, $form.Height)
            try {
                $form.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height))
                $bitmap.Save((Join-Path $ScreenshotDirectory ("token-analytics-fixture-{0}x{1}.png" -f $form.Width, $form.Height)), [Drawing.Imaging.ImageFormat]::Png)
            }
            finally { $bitmap.Dispose() }
            $panel.AutoScrollPosition = [Drawing.Point]::new(0, $layout.Height)
            Pump-Events
            $bitmap = [Drawing.Bitmap]::new($form.Width, $form.Height)
            try {
                $form.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height))
                $bitmap.Save((Join-Path $ScreenshotDirectory ("token-analytics-fixture-{0}x{1}-details.png" -f $form.Width, $form.Height)), [Drawing.Imaging.ImageFormat]::Png)
            }
            finally { $bitmap.Dispose() }
        }
    }

    $scaledHost = [Windows.Forms.Form]::new()
    try {
        $panelType = $assembly.GetType('CodexClaudeUsage.TokenAnalyticsPanel', $true)
        $panelConstructor = $panelType.GetConstructors($flags) | Select-Object -First 1
        $scaledPanel = $panelConstructor.Invoke([object[]]@([single]2, $preferences))
        $scaledPanel.Dock = [Windows.Forms.DockStyle]::Fill
        $scaledHost.StartPosition = [Windows.Forms.FormStartPosition]::Manual
        $scaledHost.Location = [Drawing.Point]::new(-20000, -20000)
        $scaledHost.ClientSize = [Drawing.Size]::new(1040, 750)
        $scaledHost.ShowInTaskbar = $false
        $scaledHost.Controls.Add($scaledPanel)
        $scaledPanel.SetSnapshot($snapshot)
        $scaledHost.Show()
        Pump-Events
        Assert-True (-not $scaledPanel.HorizontalScroll.Visible) 'Two-times scale must not introduce horizontal overflow.'
        foreach ($comboName in @('_period', '_provider', '_model', '_project')) {
            Assert-DarkComboSelection (Read-Field $scaledPanel $comboName)
        }
        $scaledLayout = Read-Field $scaledPanel '_layout'
        foreach ($child in $scaledLayout.Controls) {
            Assert-True ($child.Left -ge 0 -and $child.Right -le $scaledLayout.ClientSize.Width + 1) 'Two-times scale sections must fit the scrollable content width.'
        }
        if ($ScreenshotDirectory) {
            $bitmap = [Drawing.Bitmap]::new($scaledPanel.Width, $scaledPanel.Height)
            try {
                $scaledPanel.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height))
                $bitmap.Save((Join-Path $ScreenshotDirectory 'token-analytics-fixture-layout-scale2.png'), [Drawing.Imaging.ImageFormat]::Png)
            }
            finally { $bitmap.Dispose() }
        }
    }
    finally { $scaledHost.Dispose() }

    Write-Host 'PASS: token analytics ranges, combined filters, exact CSV rows, numeric sorting, unfiltered daily budget, partial-data states and responsive fifth-page layout.'
}
finally {
    if ($null -ne $form) { $form.Dispose() }
}
