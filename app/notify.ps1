param(
    [Parameter(Mandatory = $true)][string]$Title,
    [Parameter(Mandatory = $true)][string]$Message,
    [ValidateRange(5, 300)][int]$Seconds = 60,
    [Parameter(Mandatory = $true)][string]$AckFile,
    [string]$UsageUrl = "https://chatgpt.com/codex/settings/usage",
    [switch]$TestMode,
    [switch]$NoToast,
    [switch]$NoTrayBalloon,
    [switch]$NoPopup,
    [switch]$NoSound
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $ScriptDir "ui-common.ps1")

$DataDir = Split-Path -Parent $AckFile
$LogFile = Join-Path $DataDir "notification.log"
$script:AckWritten = $false
$script:VisibleChannelStarted = $false
$TrayIcon = $null

function Write-NotifyLog {
    param([string]$Text)
    Write-CodexUiLog -DataDir $DataDir -FileName "notification.log" -Message $Text
}

function Write-Acknowledgement {
    param([string]$Value)
    if (-not $script:AckWritten) {
        try {
            $utf8 = New-Object System.Text.UTF8Encoding($false)
            [System.IO.File]::WriteAllText($AckFile, $Value + [Environment]::NewLine, $utf8)
            $script:AckWritten = $true
        } catch {
            Write-NotifyLog "Could not write acknowledgement: $($_.Exception.Message)"
        }
    }
}

function Set-PopupStateAppearance {
    param(
        [Parameter(Mandatory = $true)]$StateHalo,
        [Parameter(Mandatory = $true)]$StateIcon,
        [string]$Mode
    )

    switch ($Mode) {
        "warning" {
            $accent = "#D28A24"
            $halo = "#FFF5E5"
            $StateIcon.Data = [System.Windows.Media.Geometry]::Parse("M 27,10 L 46,44 L 8,44 Z M 27,21 L 27,32 M 27,38 L 27,39")
            $StateIcon.StrokeThickness = 3.2
        }
        "info" {
            $accent = "#5664E8"
            $halo = "#EEF0FF"
            $StateIcon.Data = [System.Windows.Media.Geometry]::Parse("M 27,16 L 27,17 M 27,23 L 27,38")
            $StateIcon.StrokeThickness = 3.4
        }
        default {
            $accent = "#2FB184"
            $halo = "#E9F8F2"
            $StateIcon.Data = [System.Windows.Media.Geometry]::Parse("M 13,27 L 22,36 L 41,16")
            $StateIcon.StrokeThickness = 4.0
        }
    }
    $StateHalo.Fill = ConvertTo-CodexBrush -Color $halo
    $StateIcon.Stroke = ConvertTo-CodexBrush -Color $accent
    return $accent
}

try {
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    Initialize-CodexWpf

    if (-not $NoSound) {
        try {
            for ($i = 0; $i -lt 3; $i++) {
                [System.Media.SystemSounds]::Exclamation.Play()
                Start-Sleep -Milliseconds 330
            }
            Write-NotifyLog "Sound submitted."
        } catch {
            Write-NotifyLog "Sound failed: $($_.Exception.Message)"
        }
    }

    if (-not $NoToast) {
        try {
            [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
            [Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
            [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null

            $escapedTitle = [System.Security.SecurityElement]::Escape($Title)
            $escapedMessage = [System.Security.SecurityElement]::Escape($Message)
            $toastAudio = if ($NoSound) {
                '<audio silent="true" />'
            } else {
                '<audio src="ms-winsoundevent:Notification.Default" />'
            }
            $toastXml = @"
<toast duration="long">
  <visual>
    <binding template="ToastGeneric">
      <text>$escapedTitle</text>
      <text>$escapedMessage</text>
    </binding>
  </visual>
  $toastAudio
</toast>
"@
            $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
            $xml.LoadXml($toastXml)
            $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
            foreach ($appId in @(
                "CodexUsageNotifier.Desktop",
                "Microsoft.WindowsPowerShell",
                "Microsoft.PowerShell",
                "Microsoft.Windows.PowerShell",
                "Windows PowerShell"
            )) {
                try {
                    $notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId)
                    $notifier.Show($toast)
                    $script:VisibleChannelStarted = $true
                    Write-NotifyLog "Toast submitted with AppID '$appId'."
                    break
                } catch {
                    Write-NotifyLog "Toast AppID '$appId' failed: $($_.Exception.Message)"
                }
            }
        } catch {
            Write-NotifyLog "Toast failed: $($_.Exception.Message)"
        }
    }

    if (-not $NoTrayBalloon) {
        try {
            $TrayIcon = New-Object System.Windows.Forms.NotifyIcon
            $TrayIcon.Icon = [System.Drawing.SystemIcons]::Information
            $TrayIcon.Text = "Codex Usage Notifier"
            $TrayIcon.BalloonTipIcon = [System.Windows.Forms.ToolTipIcon]::Info
            $TrayIcon.BalloonTipTitle = $Title
            $balloonText = $Message
            if ($balloonText.Length -gt 240) {
                $balloonText = $balloonText.Substring(0, 237) + "..."
            }
            $TrayIcon.BalloonTipText = $balloonText
            $TrayIcon.Visible = $true
            $TrayIcon.ShowBalloonTip([Math]::Min(30000, $Seconds * 1000))
            $script:VisibleChannelStarted = $true
            Write-NotifyLog "Tray balloon submitted."
        } catch {
            Write-NotifyLog "Tray balloon failed: $($_.Exception.Message)"
            if ($TrayIcon) {
                try { $TrayIcon.Dispose() } catch { }
                $TrayIcon = $null
            }
        }
    }

    if (-not $NoPopup) {
        try {
            [xml]$xaml = [System.IO.File]::ReadAllText((Join-Path $ScriptDir "popup.xaml"))
            $reader = New-Object System.Xml.XmlNodeReader $xaml
            try {
                $Window = [System.Windows.Markup.XamlReader]::Load($reader)
            } finally {
                $reader.Close()
            }

            $SlideRoot = $Window.FindName("SlideRoot")
            $SlideTransform = $Window.FindName("SlideTransform")
            $Card = $Window.FindName("Card")
            $CloseButton = $Window.FindName("CloseButton")
            $StateHalo = $Window.FindName("StateHalo")
            $StateIcon = $Window.FindName("StateIcon")
            $HeadlineText = $Window.FindName("HeadlineText")
            $BodyText = $Window.FindName("BodyText")
            $StatusPanel = $Window.FindName("StatusPanel")
            $StatusProgressFull = $Window.FindName("StatusProgressFull")
            $StatusProgressArc = $Window.FindName("StatusProgressArc")
            $StatusPercentText = $Window.FindName("StatusPercentText")
            $StatusResetText = $Window.FindName("StatusResetText")
            $AutoDismissText = $Window.FindName("AutoDismissText")
            $PrimaryButton = $Window.FindName("PrimaryButton")
            $DismissButton = $Window.FindName("DismissButton")

            $isWarning = ($Title -match "(?i)attention|error|failed|failure|offline")
            $isSuccess = ($Title -match "(?i)available|increased|reset|cleared")
            $mode = if ($isWarning) { "warning" } elseif ($isSuccess) { "success" } else { "info" }
            if ($TestMode) { $mode = "info" }
            $accent = Set-PopupStateAppearance -StateHalo $StateHalo -StateIcon $StateIcon -Mode $mode

            if ($TestMode) {
                $HeadlineText.Text = "Modern notification test"
                $BodyText.Text = "The desktop popup, toast, tray alert, sound, and live usage widget are configured."
            } else {
                $HeadlineText.Text = $Title
                $BodyText.Text = $Message
            }

            $Config = Get-CodexConfig -DataDir $DataDir
            if ([string]::IsNullOrWhiteSpace($UsageUrl)) {
                $UsageUrl = Get-CodexUsageUrl -Config $Config
            }
            $UiState = Get-CodexUiState -DataDir $DataDir

            if ($TestMode) {
                $meter = [pscustomobject]@{
                    key = "preview:primary"
                    remaining_percent = 4.0
                    window_duration_mins = 300
                    resets_at = [DateTimeOffset]::UtcNow.AddHours(2).AddMinutes(14).ToUnixTimeSeconds()
                    slot = "primary"
                    limit_name = $null
                }
            } else {
                $reading = Get-CodexLiveReading -DataDir $DataDir
                $meters = Get-CodexMeters -Snapshot $reading.snapshot
                $preferredMeter = [string]$UiState.preferred_meter

                # When this popup represents a confirmed pending alert, show the
                # exact meter that triggered it instead of an unrelated lower
                # meter selected by the always-on widget.
                $state = Read-CodexJson -Path (Join-Path $DataDir "state.json")
                if ($null -ne $state) {
                    $pendingProperty = $state.PSObject.Properties["pending_alert"]
                    if ($null -ne $pendingProperty -and $null -ne $pendingProperty.Value) {
                        $eventsProperty = $pendingProperty.Value.PSObject.Properties["events"]
                        if ($null -ne $eventsProperty -and $null -ne $eventsProperty.Value) {
                            $firstEvent = @($eventsProperty.Value) | Select-Object -First 1
                            if ($null -ne $firstEvent) {
                                $keyProperty = $firstEvent.PSObject.Properties["key"]
                                if ($null -ne $keyProperty -and $keyProperty.Value) {
                                    $preferredMeter = [string]$keyProperty.Value
                                }
                            }
                        }
                    }
                }
                $meter = Select-CodexMeter -Meters $meters -Preferred $preferredMeter
            }

            if ($null -ne $meter) {
                $percent = [double]$meter.remaining_percent
                $meterAccent = Get-CodexAccentColor -Remaining $percent
                if ($mode -eq "warning") { $meterAccent = $accent }
                $meterBrush = ConvertTo-CodexBrush -Color $meterAccent
                $StatusProgressFull.Stroke = $meterBrush
                $StatusProgressArc.Stroke = $meterBrush
                Set-CodexProgressArc `
                    -ArcPath $StatusProgressArc `
                    -FullEllipse $StatusProgressFull `
                    -Percent $percent `
                    -Center 13.5 `
                    -Radius 11.0
                $StatusPercentText.Text = "Usage remaining: $(Format-CodexPercent $percent)%"
                $StatusResetText.Text = Format-CodexResetCountdown $meter.resets_at
                $StatusPanel.Visibility = [System.Windows.Visibility]::Visible
            } else {
                $StatusProgressArc.Data = $null
                $StatusProgressFull.Visibility = [System.Windows.Visibility]::Collapsed
                $StatusPercentText.Text = "Live usage unavailable"
                $StatusResetText.Text = "Open usage for details"
            }

            $script:PopupClosed = $false
            $script:Deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
            $script:PausedAt = $null

            $ClosePopup = {
                if (-not $script:PopupClosed) {
                    $script:PopupClosed = $true
                    $fade = New-Object System.Windows.Media.Animation.DoubleAnimation
                    $fade.From = $Window.Opacity
                    $fade.To = 0.0
                    $fade.Duration = [System.Windows.Duration]::new([TimeSpan]::FromMilliseconds(150))
                    $fade.Add_Completed({ $Window.Close() })
                    $Window.BeginAnimation([System.Windows.UIElement]::OpacityProperty, $fade)
                }
            }

            $CloseButton.Add_Click($ClosePopup)
            $DismissButton.Add_Click($ClosePopup)
            $PrimaryButton.Add_Click({
                [void](Open-CodexUsage -Url $UsageUrl)
                & $ClosePopup
            })

            $Window.Add_KeyDown({
                param($sender, $eventArgs)
                if ($eventArgs.Key -eq [System.Windows.Input.Key]::Escape) {
                    & $ClosePopup
                    $eventArgs.Handled = $true
                } elseif ($eventArgs.Key -eq [System.Windows.Input.Key]::Enter) {
                    [void](Open-CodexUsage -Url $UsageUrl)
                    & $ClosePopup
                    $eventArgs.Handled = $true
                }
            })

            $Card.Add_MouseEnter({
                if ($null -eq $script:PausedAt) {
                    $script:PausedAt = [DateTimeOffset]::UtcNow
                    $AutoDismissText.Text = "Auto-close paused"
                }
            })
            $Card.Add_MouseLeave({
                if ($null -ne $script:PausedAt) {
                    $pausedFor = [DateTimeOffset]::UtcNow - $script:PausedAt
                    $script:Deadline = $script:Deadline.Add($pausedFor)
                    $script:PausedAt = $null
                }
            })

            $CloseTimer = New-Object System.Windows.Threading.DispatcherTimer
            $CloseTimer.Interval = [TimeSpan]::FromMilliseconds(250)
            $CloseTimer.Add_Tick({
                if ($null -ne $script:PausedAt) { return }
                $remaining = ($script:Deadline - [DateTimeOffset]::UtcNow).TotalSeconds
                if ($remaining -le 0) {
                    $CloseTimer.Stop()
                    & $ClosePopup
                } else {
                    $AutoDismissText.Text = "Closes in $([int][Math]::Ceiling($remaining))s"
                }
            })

            $Window.Add_Loaded({
                Set-CodexPopupPosition -Window $Window -Margin 18 -DataDir $DataDir
                $Window.Opacity = 0.0
                $SlideTransform.Y = 14.0

                $fadeIn = New-Object System.Windows.Media.Animation.DoubleAnimation
                $fadeIn.From = 0.0
                $fadeIn.To = 1.0
                $fadeIn.Duration = [System.Windows.Duration]::new([TimeSpan]::FromMilliseconds(220))
                $Window.BeginAnimation([System.Windows.UIElement]::OpacityProperty, $fadeIn)

                $slideIn = New-Object System.Windows.Media.Animation.DoubleAnimation
                $slideIn.From = 14.0
                $slideIn.To = 0.0
                $slideIn.Duration = [System.Windows.Duration]::new([TimeSpan]::FromMilliseconds(240))
                $slideIn.DecelerationRatio = 0.8
                $SlideTransform.BeginAnimation([System.Windows.Media.TranslateTransform]::YProperty, $slideIn)
            })

            $Window.Add_ContentRendered({
                $script:VisibleChannelStarted = $true
                Write-Acknowledgement "ok modern-popup"
                Write-NotifyLog "Modern WPF popup displayed."
                $CloseTimer.Start()
                Bring-CodexWindowToAttention -Window $Window
            })

            [void]$Window.ShowDialog()
            $CloseTimer.Stop()
        } catch {
            Write-NotifyLog "Modern popup failed: $($_.Exception.ToString())"
        }
    }

    if ($script:VisibleChannelStarted) {
        Write-Acknowledgement "ok visible-channel"
        if ($TrayIcon) {
            Start-Sleep -Seconds 3
            $TrayIcon.Visible = $false
            $TrayIcon.Dispose()
            $TrayIcon = $null
        }
        exit 0
    }

    Write-Acknowledgement "error no-visible-channel"
    Write-NotifyLog "All visible notification channels failed."
    exit 1
} catch {
    if ($TrayIcon) {
        try { $TrayIcon.Visible = $false; $TrayIcon.Dispose() } catch { }
    }
    Write-NotifyLog "Fatal notification error: $($_.Exception.ToString())"
    Write-Acknowledgement "error fatal"
    exit 1
}
