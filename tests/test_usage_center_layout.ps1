$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core') {
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -STA -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

function Pump-Events([int] $milliseconds = 120) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($milliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 10
    }
}

function Get-DescendantControls([Windows.Forms.Control] $rootControl) {
    $result = New-Object 'System.Collections.Generic.List[System.Windows.Forms.Control]'
    $pending = New-Object 'System.Collections.Generic.Queue[System.Windows.Forms.Control]'
    $pending.Enqueue($rootControl)
    while ($pending.Count -gt 0) {
        $parent = $pending.Dequeue()
        foreach ($child in $parent.Controls) {
            $result.Add($child)
            $pending.Enqueue($child)
        }
    }
    return $result.ToArray()
}

function Assert-Contained([Windows.Forms.Control] $control, [string] $description) {
    if ($null -eq $control.Parent) {
        throw "$description has no parent control."
    }

    $bounds = $control.Bounds
    $client = $control.Parent.ClientRectangle
    $tolerance = 1
    if ($bounds.Left -lt ($client.Left - $tolerance) -or
        $bounds.Top -lt ($client.Top - $tolerance) -or
        $bounds.Right -gt ($client.Right + $tolerance) -or
        $bounds.Bottom -gt ($client.Bottom + $tolerance)) {
        throw "$description exceeds its parent client area: bounds=$bounds parentClient=$client"
    }
}

function Assert-KeyLayout(
    [Windows.Forms.Form] $form,
    [Windows.Forms.Control] $tabs,
    [Windows.Forms.Control] $selectedPage,
    [string] $sizeName
) {
    $form.PerformLayout()
    $tabs.PerformLayout()
    $selectedPage.PerformLayout()
    Pump-Events 80

    foreach ($control in $form.Controls) {
        if ($control.Visible) {
            Assert-Contained $control "$sizeName form child '$($control.Name)'"
        }
    }
    Assert-Contained $tabs "$sizeName navigation/content host"
    Assert-Contained $selectedPage "$sizeName selected content page"
    foreach ($control in $selectedPage.Controls) {
        if ($control.Visible) {
            Assert-Contained $control "$sizeName selected-page child '$($control.Name)'"
        }
    }
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$preferencesPath = Join-Path $env:TEMP "CodexClaudeUsage-center-layout-$PID.json"
$env:CODEX_CLAUDE_USAGE_PREFERENCES_PATH = $preferencesPath
$form = $null

try {
    [Windows.Forms.Application]::EnableVisualStyles()
    [Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)

    $assembly = [Reflection.Assembly]::LoadFile((Join-Path $root 'dist\CodexClaudeUsage.exe'))
    $configType = $assembly.GetType('CodexClaudeUsage.AppConfig', $true)
    $config = $configType.GetMethod('Load').Invoke($null, [object[]]@([string](Join-Path $root 'config.json')))
    $preferencesType = $assembly.GetType('CodexClaudeUsage.UserPreferences', $true)
    $preferences = $preferencesType.GetMethod('Load').Invoke($null, @())
    $formType = $assembly.GetType('CodexClaudeUsage.UsageCenterForm', $true)
    $flags = [Reflection.BindingFlags]'Instance,NonPublic'
    $constructor = $formType.GetConstructors([Reflection.BindingFlags]'Instance,Public,NonPublic') | Select-Object -First 1
    $form = $constructor.Invoke([object[]]@($config, $preferences))

    $tabs = $formType.GetField('_tabs', $flags).GetValue($form)
    if ($null -eq $tabs) {
        throw 'Usage center _tabs field is null.'
    }
    $tabCountProperty = $tabs.GetType().GetProperty('TabCount', [Reflection.BindingFlags]'Instance,Public,NonPublic')
    if ($null -eq $tabCountProperty -or [int]$tabCountProperty.GetValue($tabs, $null) -ne 4) {
        throw 'Usage center navigation must expose TabCount=4.'
    }

    $pageFields = @('_overviewPage', '_historyPage', '_healthPage', '_settingsPage')
    $pages = @($pageFields | ForEach-Object { $formType.GetField($_, $flags).GetValue($form) })
    if (($pages | Where-Object { $null -eq $_ }).Count -gt 0) {
        throw 'One or more usage-center content pages are missing.'
    }

    $form.Show()
    Pump-Events 160
    $allControls = @(Get-DescendantControls $form)
    $nativeTabs = @($allControls | Where-Object { $_ -is [Windows.Forms.TabControl] })
    if ($nativeTabs.Count -ne 0) {
        throw "Usage center still contains $($nativeTabs.Count) native TabControl instance(s)."
    }

    $lists = @($allControls | Where-Object { $_ -is [Windows.Forms.ListView] })
    if ($lists.Count -ne 3) {
        throw "Expected exactly three ListView controls; found $($lists.Count)."
    }
    foreach ($list in $lists) {
        if (-not $list.OwnerDraw) {
            throw "ListView '$($list.Name)' must use owner drawing."
        }
        if ($list.BorderStyle -ne [Windows.Forms.BorderStyle]::None) {
            throw "ListView '$($list.Name)' must have BorderStyle=None."
        }
        $color = $list.BackColor
        $luminance = (0.2126 * $color.R) + (0.7152 * $color.G) + (0.0722 * $color.B)
        if ($luminance -ge 128) {
            throw "ListView '$($list.Name)' does not have a dark background: $color"
        }
    }

    $selectTab = $formType.GetMethod('SelectTab', $flags)
    if ($null -eq $selectTab) {
        throw 'Usage center SelectTab method is missing.'
    }

    $paletteType = $assembly.GetType('CodexClaudeUsage.Palette', $true)
    $staticPublic = [Reflection.BindingFlags]'Static,Public'
    $borderColor = [Drawing.Color]$paletteType.GetField('Border', $staticPublic).GetValue($null)
    $focusColor = [Drawing.Color]$paletteType.GetField('Focus', $staticPublic).GetValue($null)

    foreach ($fieldName in @('_codexRow', '_claudeRow')) {
        $row = $formType.GetField($fieldName, $flags).GetValue($form)
        $bitmap = New-Object Drawing.Bitmap([Math]::Max(1, $row.Width), [Math]::Max(1, $row.Height))
        try {
            $row.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height))
            $sideBorderPixels = 0
            for ($y = 1; $y -lt ($bitmap.Height - 1); $y++) {
                if ($bitmap.GetPixel(0, $y).ToArgb() -eq $borderColor.ToArgb()) { $sideBorderPixels++ }
                if ($bitmap.GetPixel($bitmap.Width - 1, $y).ToArgb() -eq $borderColor.ToArgb()) { $sideBorderPixels++ }
            }
            if ($sideBorderPixels -ne 0) {
                throw "Overview row '$fieldName' still paints $sideBorderPixels side-border pixels."
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }

    $selectedNavigation = @($tabs.Controls | Where-Object {
        $selected = $_.GetType().GetProperty('Selected', [Reflection.BindingFlags]'Instance,Public,NonPublic')
        $null -ne $selected -and [bool]$selected.GetValue($_, $null)
    }) | Select-Object -First 1
    if ($null -eq $selectedNavigation) {
        throw 'Selected overview navigation item is missing.'
    }
    $navigationBitmap = New-Object Drawing.Bitmap([Math]::Max(1, $selectedNavigation.Width), [Math]::Max(1, $selectedNavigation.Height))
    try {
        $selectedNavigation.DrawToBitmap($navigationBitmap, [Drawing.Rectangle]::new(0, 0, $navigationBitmap.Width, $navigationBitmap.Height))
        $sideAccentPixels = 0
        $sampleWidth = [Math]::Min(8, $navigationBitmap.Width)
        for ($x = 0; $x -lt $sampleWidth; $x++) {
            for ($y = 0; $y -lt $navigationBitmap.Height; $y++) {
                if ($navigationBitmap.GetPixel($x, $y).ToArgb() -eq $focusColor.ToArgb()) { $sideAccentPixels++ }
            }
        }
        if ($sideAccentPixels -ne 0) {
            throw "Selected overview navigation still paints a side accent ($sideAccentPixels pixels)."
        }
    }
    finally {
        $navigationBitmap.Dispose()
    }

    $defaultSize = $form.Size
    foreach ($sizeCase in @(
        @{ Name = 'default'; Size = $defaultSize },
        @{ Name = 'minimum'; Size = $form.MinimumSize },
        @{ Name = 'wide'; Size = [Drawing.Size]::new(1920, 900) }
    )) {
        $form.Size = $sizeCase.Size
        Pump-Events 120
        for ($index = 0; $index -lt 4; $index++) {
            $selectTab.Invoke($form, @([int]$index)) | Out-Null
            Pump-Events 100
            $visiblePages = @($pages | Where-Object { $_.Visible })
            if ($visiblePages.Count -ne 1 -or -not [object]::ReferenceEquals($visiblePages[0], $pages[$index])) {
                throw "$($sizeCase.Name) size tab $index did not expose exactly its selected content page."
            }
            Assert-KeyLayout $form $tabs $pages[$index] "$($sizeCase.Name) size tab $index"
        }
    }

    Write-Host 'PASS: usage center uses borderless overview bands, four custom tabs, dark owner-drawn lists, exclusive pages, and bounded layouts'
}
finally {
    if ($null -ne $form) {
        $form.Dispose()
    }
    Remove-Item Env:CODEX_CLAUDE_USAGE_PREFERENCES_PATH -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $preferencesPath -Force -ErrorAction SilentlyContinue
}
