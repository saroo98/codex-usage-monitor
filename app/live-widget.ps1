[CmdletBinding()]
param(
    [switch]$Preview,
    [ValidateRange(0, 100)][double]$PreviewPercent = 4
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $ScriptDir "ui-common.ps1")

$DataDir = Get-CodexDataDir
$UiHeartbeatPath = Join-Path $DataDir "ui-heartbeat.json"
$ConfigPath = Join-Path $DataDir "config.json"
$UiStatePath = Join-Path $DataDir "ui-state.json"
$LogPath = Join-Path $DataDir "ui.log"
$VersionPath = Join-Path $ScriptDir "VERSION"
$Version = if (Test-Path -LiteralPath $VersionPath -PathType Leaf) {
    ([System.IO.File]::ReadAllText($VersionPath)).Trim()
} else {
    "5.0.0"
}

$createdNew = $false
$mutex = $null
try {
    $mutex = [System.Threading.Mutex]::new(
        $true,
        "Local\CodexUsageNotifier.LiveWidget",
        [ref]$createdNew
    )
    if (-not $createdNew) { exit 0 }

    Initialize-CodexWpf
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null

    $Config = Get-CodexConfig -DataDir $DataDir
    $widgetEnabled = [bool](Get-CodexUiValue -Config $Config -Name "live_widget" -Default $true)
    if (-not $widgetEnabled -and -not $Preview) {
        Write-CodexJsonAtomic -Path $UiHeartbeatPath -Value ([ordered]@{
            version = $Version
            pid = $PID
            status = "disabled"
            checked_at = [DateTimeOffset]::UtcNow.ToString("o")
        })
        exit 0
    }

    $UiStateExists = Test-Path -LiteralPath $UiStatePath -PathType Leaf
    $UiState = Get-CodexUiState -DataDir $DataDir
    if (-not $UiStateExists) {
        $UiState.preferred_meter = [string](Get-CodexUiValue -Config $Config -Name "preferred_meter" -Default "auto")
        $UiState.always_on_top = [bool](Get-CodexUiValue -Config $Config -Name "always_on_top" -Default $true)
    }

    [xml]$xaml = [System.IO.File]::ReadAllText((Join-Path $ScriptDir "live-widget.xaml"))
    $reader = New-Object System.Xml.XmlNodeReader $xaml
    try {
        $Window = [System.Windows.Markup.XamlReader]::Load($reader)
    } finally {
        $reader.Close()
    }

    $WidgetRoot = $Window.FindName("WidgetRoot")
    $Card = $Window.FindName("Card")
    $ProgressTrack = $Window.FindName("ProgressTrack")
    $ProgressFull = $Window.FindName("ProgressFull")
    $ProgressArc = $Window.FindName("ProgressArc")
    $PercentText = $Window.FindName("PercentText")
    $UsageLabel = $Window.FindName("UsageLabel")
    $UsageDetail = $Window.FindName("UsageDetail")
    $MenuButton = $Window.FindName("MenuButton")

    $Window.Topmost = [bool]$UiState.always_on_top
    Set-CodexWidgetPosition -Window $Window -State $UiState

    $script:DataDir = $DataDir
    $script:UiHeartbeatPath = $UiHeartbeatPath
    $script:ConfigPath = $ConfigPath
    $script:ScriptDir = $ScriptDir
    $script:Version = $Version
    $script:Config = $Config
    $script:UiState = $UiState
    $script:Window = $Window
    $script:WidgetRoot = $WidgetRoot
    $script:Card = $Card
    $script:ProgressArc = $ProgressArc
    $script:ProgressFull = $ProgressFull
    $script:PercentText = $PercentText
    $script:UsageLabel = $UsageLabel
    $script:UsageDetail = $UsageDetail
    $script:MenuButton = $MenuButton
    $script:Preview = [bool]$Preview
    $script:PreviewPercent = [double]$PreviewPercent
    $script:CurrentMeters = @()
    $script:CurrentMeter = $null
    $script:CurrentReading = $null
    $script:LastDisplayedPercent = $null
    $script:LastUiHeartbeat = [DateTimeOffset]::MinValue
    $script:RefreshBusy = $false

    function Save-CurrentUiState {
        try {
            $script:UiState.left = [Math]::Round($script:Window.Left, 1)
            $script:UiState.top = [Math]::Round($script:Window.Top, 1)
            Save-CodexUiState -State $script:UiState -DataDir $script:DataDir
        } catch {
            Write-CodexUiLog -DataDir $script:DataDir -Message "Could not save widget state: $($_.Exception.Message)"
        }
    }

    function Write-LiveWidgetHeartbeat {
        param(
            [string]$Status,
            $Meter,
            $Reading,
            [string]$ErrorText = $null
        )
        try {
            $payload = [ordered]@{
                version = $script:Version
                pid = $PID
                status = $Status
                checked_at = [DateTimeOffset]::UtcNow.ToString("o")
                selected_meter = if ($null -ne $Meter) { [string]$Meter.key } else { $null }
                remaining_percent = if ($null -ne $Meter) { [double]$Meter.remaining_percent } else { $null }
                reading_checked_at = if ($null -ne $Reading) { $Reading.checked_at } else { $null }
                reading_age_seconds = if ($null -ne $Reading) { $Reading.age_seconds } else { $null }
                topmost = [bool]$script:Window.Topmost
                left = [Math]::Round($script:Window.Left, 1)
                top = [Math]::Round($script:Window.Top, 1)
                error = $ErrorText
            }
            Write-CodexJsonAtomic -Path $script:UiHeartbeatPath -Value $payload
            $script:LastUiHeartbeat = [DateTimeOffset]::UtcNow
        } catch {
            Write-CodexUiLog -DataDir $script:DataDir -Message "Could not write UI heartbeat: $($_.Exception.Message)"
        }
    }

    function Set-WidgetVisualState {
        param(
            [double]$Percent,
            [string]$Accent,
            [string]$Label,
            [string]$Detail,
            [string]$Tooltip,
            [switch]$ShowProgress
        )

        $brush = ConvertTo-CodexBrush -Color $Accent
        $script:ProgressArc.Stroke = $brush
        $script:ProgressFull.Stroke = $brush
        $script:PercentText.Foreground = $brush
        $script:PercentText.Text = if ($ShowProgress) { "$(Format-CodexPercent $Percent)%" } else { "--" }
        $script:UsageLabel.Text = $Label
        $script:UsageDetail.Text = $Detail
        $script:Card.ToolTip = $Tooltip
        if ($ShowProgress) {
            Set-CodexProgressArc `
                -ArcPath $script:ProgressArc `
                -FullEllipse $script:ProgressFull `
                -Percent $Percent `
                -Center 22 `
                -Radius 19
        } else {
            $script:ProgressArc.Data = $null
            $script:ProgressFull.Visibility = [System.Windows.Visibility]::Collapsed
        }
    }

    function Update-LiveWidget {
        if ($script:RefreshBusy) { return }
        $script:RefreshBusy = $true
        try {
            if ($script:Preview) {
                $previewReset = [DateTimeOffset]::UtcNow.AddHours(2).AddMinutes(14).ToUnixTimeSeconds()
                $meter = [pscustomobject]@{
                    key = "preview:primary"
                    limit_name = $null
                    slot = "primary"
                    remaining_percent = [double]$script:PreviewPercent
                    window_duration_mins = 300
                    resets_at = [double]$previewReset
                    reached_type = $null
                }
                $reading = [pscustomobject]@{
                    status = "ok"
                    age_seconds = 0
                    checked_at = [DateTimeOffset]::UtcNow.ToString("o")
                    error = $null
                }
                $meters = @($meter)
            } else {
                $script:Config = Get-CodexConfig -DataDir $script:DataDir
                $enabledNow = [bool](Get-CodexUiValue -Config $script:Config -Name "live_widget" -Default $true)
                if (-not $enabledNow) {
                    Write-LiveWidgetHeartbeat -Status "disabled" -Meter $script:CurrentMeter -Reading $script:CurrentReading
                    $script:Window.Close()
                    return
                }
                $reading = Get-CodexLiveReading -DataDir $script:DataDir
                $meters = Get-CodexMeters -Snapshot $reading.snapshot
                $preferred = [string]$script:UiState.preferred_meter
                if ([string]::IsNullOrWhiteSpace($preferred)) {
                    $preferred = [string](Get-CodexUiValue -Config $script:Config -Name "preferred_meter" -Default "auto")
                }
                $meter = Select-CodexMeter -Meters $meters -Preferred $preferred
            }

            $script:CurrentMeters = @($meters)
            $script:CurrentMeter = $meter
            $script:CurrentReading = $reading

            $staleAfter = [double](Get-CodexUiValue -Config $script:Config -Name "stale_after_seconds" -Default 180)
            $isStale = ($null -eq $reading.age_seconds -or [double]$reading.age_seconds -gt $staleAfter)
            $isError = ([string]$reading.status -eq "error")

            if ($null -eq $meter) {
                $detail = if ($isError) { "Monitor needs attention" } else { "Waiting for live data" }
                $tooltip = if ($reading.error) {
                    "Codex Usage Notifier`n$($reading.error)"
                } else {
                    "Codex Usage Notifier`nWaiting for the first usage reading."
                }
                Set-WidgetVisualState `
                    -Percent 0 `
                    -Accent "#8B96A8" `
                    -Label "Usage" `
                    -Detail $detail `
                    -Tooltip $tooltip
                $heartbeatStatus = if ($isError) { "error" } else { "waiting" }
                $now = [DateTimeOffset]::UtcNow
                if (($now - $script:LastUiHeartbeat).TotalSeconds -ge 5) {
                    Write-LiveWidgetHeartbeat `
                        -Status $heartbeatStatus `
                        -Meter $null `
                        -Reading $reading `
                        -ErrorText $reading.error
                }
                return
            }

            $percent = [double]$meter.remaining_percent
            $accent = Get-CodexAccentColor -Remaining $percent -Stale:$isStale -ErrorState:$isError
            $formattedPercent = Format-CodexPercent $percent
            $resetText = Format-CodexResetCountdown $meter.resets_at
            $windowLabel = Get-CodexWindowLabel $meter
            $showReset = [bool](Get-CodexUiValue -Config $script:Config -Name "show_reset_countdown" -Default $true)

            if ($isStale) {
                $ageText = if ($null -ne $reading.age_seconds) { Format-CodexAge ([double]$reading.age_seconds) } else { "unknown age" }
                $detail = "$formattedPercent% cached | $ageText"
            } elseif ($isError) {
                $detail = "$formattedPercent% cached | offline"
            } elseif ($showReset -and $meter.resets_at) {
                $compactReset = $resetText -replace "^Resets in ", ""
                $detail = "$formattedPercent% left | $compactReset"
            } else {
                $detail = "$formattedPercent% left"
            }

            $updatedText = if ($null -ne $reading.age_seconds) {
                "Updated $(Format-CodexAge ([double]$reading.age_seconds))"
            } else {
                "Update time unavailable"
            }
            $tooltip = @(
                "Codex Usage Notifier"
                "${windowLabel}: $formattedPercent% remaining"
                $resetText
                $updatedText
                "Double-click to open Codex Usage. Drag to reposition."
            ) -join "`n"

            Set-WidgetVisualState `
                -Percent $percent `
                -Accent $accent `
                -Label "Usage" `
                -Detail $detail `
                -Tooltip $tooltip `
                -ShowProgress

            $now = [DateTimeOffset]::UtcNow
            if (($now - $script:LastUiHeartbeat).TotalSeconds -ge 5) {
                $heartbeatStatus = if ($isStale -or $isError) { "stale" } else { "ok" }
                Write-LiveWidgetHeartbeat `
                    -Status $heartbeatStatus `
                    -Meter $meter `
                    -Reading $reading `
                    -ErrorText $reading.error
            }
            $script:LastDisplayedPercent = $percent
        } catch {
            Set-WidgetVisualState `
                -Percent 0 `
                -Accent "#8B96A8" `
                -Label "Usage" `
                -Detail "Unable to read usage" `
                -Tooltip "Codex Usage Notifier`n$($_.Exception.Message)"
            $now = [DateTimeOffset]::UtcNow
            if (($now - $script:LastUiHeartbeat).TotalSeconds -ge 5) {
                Write-LiveWidgetHeartbeat -Status "error" -Meter $null -Reading $null -ErrorText $_.Exception.Message
            }
            Write-CodexUiLog -DataDir $script:DataDir -Message "Widget refresh failed: $($_.Exception.Message)"
        } finally {
            $script:RefreshBusy = $false
        }
    }

    function New-WidgetMenuItem {
        param(
            [Parameter(Mandatory = $true)][string]$Header,
            [scriptblock]$OnClick,
            [switch]$Checkable,
            [switch]$Checked
        )
        $item = New-Object System.Windows.Controls.MenuItem
        $item.Header = $Header
        $item.IsCheckable = [bool]$Checkable
        $item.IsChecked = [bool]$Checked
        if ($null -ne $OnClick) { $item.Add_Click($OnClick) }
        return $item
    }

    function Show-WidgetMenu {
        $menu = New-Object System.Windows.Controls.ContextMenu
        $menu.Placement = [System.Windows.Controls.Primitives.PlacementMode]::Bottom
        $menu.PlacementTarget = $script:MenuButton
        $menu.MinWidth = 210

        $openItem = New-WidgetMenuItem -Header "Open Codex usage" -OnClick {
            [void](Open-CodexUsage -Url (Get-CodexUsageUrl $script:Config))
        }
        [void]$menu.Items.Add($openItem)

        $refreshItem = New-WidgetMenuItem -Header "Refresh display now" -OnClick {
            Update-LiveWidget
        }
        [void]$menu.Items.Add($refreshItem)
        [void]$menu.Items.Add((New-Object System.Windows.Controls.Separator))

        $meterHeader = New-WidgetMenuItem -Header "Displayed limit"
        $meterHeader.IsEnabled = $false
        [void]$menu.Items.Add($meterHeader)

        $autoItem = New-WidgetMenuItem `
            -Header "Auto: lowest remaining" `
            -Checkable `
            -Checked:([string]$script:UiState.preferred_meter -eq "auto") `
            -OnClick {
                $script:UiState.preferred_meter = "auto"
                Save-CurrentUiState
                Update-LiveWidget
            }
        [void]$menu.Items.Add($autoItem)

        foreach ($meterValue in @($script:CurrentMeters)) {
            $meterKey = [string]$meterValue.key
            $meterCaption = "$(Get-CodexWindowLabel $meterValue) - $(Format-CodexPercent ([double]$meterValue.remaining_percent))% left"
            $selected = ([string]$script:UiState.preferred_meter -eq $meterKey)
            $item = New-WidgetMenuItem `
                -Header $meterCaption `
                -Checkable `
                -Checked:$selected `
                -OnClick {
                    param($sender, $eventArgs)

                    $selectedKey = [string]$sender.Tag

                    if ([string]::IsNullOrWhiteSpace($selectedKey)) {
                        return
                    }

                    $script:UiState.preferred_meter = $selectedKey
                    Save-CurrentUiState
                    Update-LiveWidget

                    $eventArgs.Handled = $true
                }

            $item.Tag = $meterKey
            [void]$menu.Items.Add($item)
        }

        [void]$menu.Items.Add((New-Object System.Windows.Controls.Separator))
        $topItem = New-WidgetMenuItem `
            -Header "Always on top" `
            -Checkable `
            -Checked:([bool]$script:Window.Topmost) `
            -OnClick {
                $script:Window.Topmost = -not [bool]$script:Window.Topmost
                $script:UiState.always_on_top = [bool]$script:Window.Topmost
                Save-CurrentUiState
            }
        [void]$menu.Items.Add($topItem)

        $resetPositionItem = New-WidgetMenuItem -Header "Reset widget position" -OnClick {
            $script:UiState.left = $null
            $script:UiState.top = $null
            Set-CodexWidgetPosition -Window $script:Window -State $script:UiState
            Save-CurrentUiState
        }
        [void]$menu.Items.Add($resetPositionItem)

        $configItem = New-WidgetMenuItem -Header "Open configuration" -OnClick {
            [void](Open-CodexFile -Path $script:ConfigPath)
        }
        [void]$menu.Items.Add($configItem)

        $logsItem = New-WidgetMenuItem -Header "Open logs folder" -OnClick {
            [void](Open-CodexFile -Path $script:DataDir)
        }
        [void]$menu.Items.Add($logsItem)

        $statusItem = New-WidgetMenuItem -Header "Run diagnostics" -OnClick {
            try {
                $powershell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
                $scriptPath = Join-Path $script:ScriptDir "run-diagnostics.ps1"
                Start-Process `
                    -FilePath $powershell `
                    -ArgumentList @("-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$scriptPath`"") | Out-Null
            } catch {
                Write-CodexUiLog -DataDir $script:DataDir -Message "Could not open diagnostics: $($_.Exception.Message)"
            }
        }
        [void]$menu.Items.Add($statusItem)

        $menu.IsOpen = $true
    }

    $Window.Add_SourceInitialized({
        Set-CodexToolWindow -Window $script:Window -NoActivate
    })

    $Window.Add_Loaded({
        Set-CodexWidgetPosition -Window $script:Window -State $script:UiState
        $animation = New-Object System.Windows.Media.Animation.DoubleAnimation
        $animation.From = 0.0
        $animation.To = 1.0
        $animation.Duration = [System.Windows.Duration]::new([TimeSpan]::FromMilliseconds(210))
        $script:WidgetRoot.BeginAnimation([System.Windows.UIElement]::OpacityProperty, $animation)
        Update-LiveWidget
    })

    $Card.Add_MouseLeftButtonDown({
        param($sender, $eventArgs)
        if ($eventArgs.ChangedButton -ne [System.Windows.Input.MouseButton]::Left) { return }
        if ($eventArgs.ClickCount -ge 2) {
            [void](Open-CodexUsage -Url (Get-CodexUsageUrl $script:Config))
            $eventArgs.Handled = $true
            return
        }
        try {
            $script:Window.DragMove()
            $script:UiState.left = [Math]::Round($script:Window.Left, 1)
            $script:UiState.top = [Math]::Round($script:Window.Top, 1)
            Save-CurrentUiState
        } catch { }
    })

    $Card.Add_MouseRightButtonUp({
        param($sender, $eventArgs)
        Show-WidgetMenu
        $eventArgs.Handled = $true
    })

    $MenuButton.Add_Click({ Show-WidgetMenu })

    $Window.Add_Closed({
        try {
            Write-LiveWidgetHeartbeat -Status "stopped" -Meter $script:CurrentMeter -Reading $script:CurrentReading
        } catch { }
    })

    $refreshMilliseconds = [int](Get-CodexUiValue -Config $Config -Name "refresh_milliseconds" -Default 1000)
    $refreshMilliseconds = [Math]::Max(500, [Math]::Min(5000, $refreshMilliseconds))
    $Timer = New-Object System.Windows.Threading.DispatcherTimer
    $Timer.Interval = [TimeSpan]::FromMilliseconds($refreshMilliseconds)
    $Timer.Add_Tick({ Update-LiveWidget })
    $Timer.Start()

    Write-CodexUiLog -DataDir $DataDir -Message "Live widget started. Version=$Version; PID=$PID"

    # Publish an immediate startup heartbeat before entering the WPF message loop.
    # The normal refresh cycle will replace this with ok, stale, or error.
    Write-LiveWidgetHeartbeat `
        -Status "waiting" `
        -Meter $script:CurrentMeter `
        -Reading $script:CurrentReading

    $application = New-Object System.Windows.Application
    $application.ShutdownMode = [System.Windows.ShutdownMode]::OnMainWindowClose
    [void]$application.Run($Window)
} catch {
    Write-CodexUiLog -DataDir $DataDir -Message "Fatal live widget error: $($_.Exception.ToString())"
    try {
        Write-CodexJsonAtomic -Path $UiHeartbeatPath -Value ([ordered]@{
            version = $Version
            pid = $PID
            status = "error"
            checked_at = [DateTimeOffset]::UtcNow.ToString("o")
            error = $_.Exception.Message
        })
    } catch { }
    exit 1
} finally {
    if ($null -ne $mutex -and $createdNew) {
        try { $mutex.ReleaseMutex() } catch { }
        $mutex.Dispose()
    }
}



