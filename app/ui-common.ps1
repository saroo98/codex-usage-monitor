Set-StrictMode -Version Latest

function Initialize-CodexWpf {
    Add-Type -AssemblyName PresentationFramework
    Add-Type -AssemblyName PresentationCore
    Add-Type -AssemblyName WindowsBase
    Add-Type -AssemblyName System.Xaml
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    if (-not ("CodexUsageNotifier.NativeWindow" -as [type])) {
        $nativeSource = @"
using System;
using System.Runtime.InteropServices;

namespace CodexUsageNotifier
{
    public static class NativeWindow
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags
        );

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FLASHWINFO info);

        [DllImport("shell32.dll")]
        private static extern int SetCurrentProcessExplicitAppUserModelID(
            [MarshalAs(UnmanagedType.LPWStr)] string AppID
        );

        public static void SetAppUserModelId(string appId)
        {
            try { SetCurrentProcessExplicitAppUserModelID(appId); } catch { }
        }

        public static void MakeToolWindow(IntPtr handle, bool noActivate)
        {
            int style = GetWindowLong(handle, GWL_EXSTYLE);
            style |= WS_EX_TOOLWINDOW;
            if (noActivate) style |= WS_EX_NOACTIVATE;
            else style &= ~WS_EX_NOACTIVATE;
            SetWindowLong(handle, GWL_EXSTYLE, style);
        }

        public static void BringToAttention(IntPtr handle)
        {
            ShowWindow(handle, 5);
            SetWindowPos(
                handle,
                HWND_TOPMOST,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW
            );
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
        }

        public static void Flash(IntPtr handle)
        {
            FLASHWINFO info = new FLASHWINFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO));
            info.hwnd = handle;
            info.dwFlags = 3U | 12U;
            info.uCount = 10U;
            info.dwTimeout = 0U;
            FlashWindowEx(ref info);
        }
    }
}
"@
        Add-Type -TypeDefinition $nativeSource -ErrorAction Stop
    }

    [CodexUsageNotifier.NativeWindow]::SetAppUserModelId("CodexUsageNotifier.Desktop")
}

function Get-CodexDataDir {
    if ($env:CODEX_USAGE_NOTIFIER_DATA_DIR) {
        return [Environment]::ExpandEnvironmentVariables($env:CODEX_USAGE_NOTIFIER_DATA_DIR)
    }
    return (Join-Path $env:LOCALAPPDATA "CodexUsageNotifier")
}

function Read-CodexJson {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$Attempts = 4
    )

    for ($attempt = 0; $attempt -lt $Attempts; $attempt++) {
        try {
            if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
            $raw = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
            if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
            return ($raw | ConvertFrom-Json)
        } catch {
            if ($attempt -ge ($Attempts - 1)) { return $null }
            Start-Sleep -Milliseconds (40 * ($attempt + 1))
        }
    }
    return $null
}

function Write-CodexJsonAtomic {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $temp = "$Path.$PID.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $json = $Value | ConvertTo-Json -Depth 30
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($temp, $json + [Environment]::NewLine, $utf8)
        $moved = $false
        $lastError = $null
        for ($attempt = 0; $attempt -lt 7; $attempt++) {
            try {
                Move-Item -LiteralPath $temp -Destination $Path -Force -ErrorAction Stop
                $moved = $true
                break
            } catch {
                $lastError = $_
                if ($attempt -lt 6) {
                    Start-Sleep -Milliseconds (50 * [Math]::Pow(2, $attempt))
                }
            }
        }
        if (-not $moved) {
            throw $lastError
        }
    } finally {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

function Get-CodexConfig {
    param([string]$DataDir = (Get-CodexDataDir))

    $config = Read-CodexJson -Path (Join-Path $DataDir "config.json")
    if ($null -eq $config) {
        return [pscustomobject]@{
            usage_url = "https://chatgpt.com/codex/settings/usage"
            ui = [pscustomobject]@{
                live_widget = $true
                always_on_top = $true
                preferred_meter = "auto"
                stale_after_seconds = 180
                refresh_milliseconds = 1000
                show_reset_countdown = $true
            }
        }
    }
    return $config
}

function Get-CodexUiValue {
    param(
        $Config,
        [Parameter(Mandatory = $true)][string]$Name,
        $Default
    )

    if ($null -ne $Config) {
        $uiProperty = $Config.PSObject.Properties["ui"]
        if ($null -ne $uiProperty -and $null -ne $uiProperty.Value) {
            $property = $uiProperty.Value.PSObject.Properties[$Name]
            if ($null -ne $property -and $null -ne $property.Value) {
                return $property.Value
            }
        }
    }
    return $Default
}

function Get-CodexUiState {
    param([string]$DataDir = (Get-CodexDataDir))

    $value = Read-CodexJson -Path (Join-Path $DataDir "ui-state.json")
    $state = [ordered]@{
        schema_version = 1
        left = $null
        top = $null
        preferred_meter = "auto"
        always_on_top = $true
        updated_at = $null
    }
    if ($null -ne $value) {
        foreach ($name in @("left", "top", "preferred_meter", "always_on_top", "updated_at")) {
            $property = $value.PSObject.Properties[$name]
            if ($null -ne $property) { $state[$name] = $property.Value }
        }
    }
    return [pscustomobject]$state
}

function Save-CodexUiState {
    param(
        [Parameter(Mandatory = $true)]$State,
        [string]$DataDir = (Get-CodexDataDir)
    )

    $State.updated_at = [DateTimeOffset]::UtcNow.ToString("o")
    Write-CodexJsonAtomic -Path (Join-Path $DataDir "ui-state.json") -Value $State
}

function Get-CodexOptionalDouble {
    param($Value)
    if ($null -eq $Value) { return $null }
    try { return [double]$Value } catch { return $null }
}

function Get-CodexOptionalInt {
    param($Value)
    if ($null -eq $Value) { return $null }
    try { return [int]$Value } catch { return $null }
}

function Get-CodexLiveReading {
    param([string]$DataDir = (Get-CodexDataDir))

    $heartbeat = Read-CodexJson -Path (Join-Path $DataDir "heartbeat.json")
    $state = Read-CodexJson -Path (Join-Path $DataDir "state.json")
    $status = "waiting"
    $errorText = $null
    $heartbeatCheckedAt = $null
    $candidates = @()

    if ($null -ne $heartbeat) {
        $statusProperty = $heartbeat.PSObject.Properties["status"]
        if ($null -ne $statusProperty) { $status = [string]$statusProperty.Value }
        $errorProperty = $heartbeat.PSObject.Properties["error"]
        if ($null -ne $errorProperty) { $errorText = [string]$errorProperty.Value }
        $checkedProperty = $heartbeat.PSObject.Properties["checked_at"]
        if ($null -ne $checkedProperty) { $heartbeatCheckedAt = [string]$checkedProperty.Value }
        $snapshotProperty = $heartbeat.PSObject.Properties["snapshot"]
        if ($null -ne $snapshotProperty -and $null -ne $snapshotProperty.Value) {
            $candidates += [pscustomobject]@{
                source = "heartbeat"
                snapshot = $snapshotProperty.Value
                checked_at = $heartbeatCheckedAt
                priority = 2
            }
        }
    }

    if ($null -ne $state) {
        $pendingProperty = $state.PSObject.Properties["pending_alert"]
        if ($null -ne $pendingProperty -and $null -ne $pendingProperty.Value) {
            $pendingSnapshotProperty = $pendingProperty.Value.PSObject.Properties["snapshot"]
            if ($null -ne $pendingSnapshotProperty -and $null -ne $pendingSnapshotProperty.Value) {
                $pendingFetchedProperty = $pendingSnapshotProperty.Value.PSObject.Properties["fetched_at"]
                $pendingFetchedAt = if ($null -ne $pendingFetchedProperty) {
                    [string]$pendingFetchedProperty.Value
                } else {
                    $null
                }
                $candidates += [pscustomobject]@{
                    source = "pending-alert"
                    snapshot = $pendingSnapshotProperty.Value
                    checked_at = $pendingFetchedAt
                    priority = 3
                }
            }
        }

        $lastSnapshotProperty = $state.PSObject.Properties["last_snapshot"]
        if ($null -ne $lastSnapshotProperty -and $null -ne $lastSnapshotProperty.Value) {
            $lastFetchedProperty = $lastSnapshotProperty.Value.PSObject.Properties["fetched_at"]
            $lastFetchedAt = if ($null -ne $lastFetchedProperty) {
                [string]$lastFetchedProperty.Value
            } else {
                $null
            }
            $candidates += [pscustomobject]@{
                source = "state"
                snapshot = $lastSnapshotProperty.Value
                checked_at = $lastFetchedAt
                priority = 1
            }
        }
    }

    $selected = $null
    $selectedStamp = [DateTimeOffset]::MinValue
    foreach ($candidate in @($candidates)) {
        $stamp = [DateTimeOffset]::MinValue
        if ($candidate.checked_at) {
            try { $stamp = [DateTimeOffset]::Parse([string]$candidate.checked_at).ToUniversalTime() } catch { }
        }
        if ($null -eq $selected -or
            $stamp -gt $selectedStamp -or
            ($stamp -eq $selectedStamp -and [int]$candidate.priority -gt [int]$selected.priority)) {
            $selected = $candidate
            $selectedStamp = $stamp
        }
    }

    $snapshot = if ($null -ne $selected) { $selected.snapshot } else { $null }
    $source = if ($null -ne $selected) { [string]$selected.source } else { "none" }
    $checkedAt = if ($null -ne $selected -and $selected.checked_at) {
        [string]$selected.checked_at
    } else {
        $heartbeatCheckedAt
    }

    $age = $null
    if ($checkedAt) {
        try {
            $stamp = [DateTimeOffset]::Parse($checkedAt).ToUniversalTime()
            $age = [Math]::Max(0.0, ([DateTimeOffset]::UtcNow - $stamp).TotalSeconds)
        } catch {
            $age = $null
        }
    }

    return [pscustomobject]@{
        heartbeat = $heartbeat
        snapshot = $snapshot
        source = $source
        checked_at = $checkedAt
        age_seconds = $age
        status = $status
        error = $errorText
    }
}

function Get-CodexMeters {
    param($Snapshot)

    $result = @()
    if ($null -eq $Snapshot) { return $result }
    $metersProperty = $Snapshot.PSObject.Properties["meters"]
    if ($null -eq $metersProperty -or $null -eq $metersProperty.Value) { return $result }

    foreach ($property in $metersProperty.Value.PSObject.Properties) {
        $meter = $property.Value
        if ($null -eq $meter) { continue }

        $remainingProperty = $meter.PSObject.Properties["remaining_percent"]
        if ($null -eq $remainingProperty) { continue }
        $remaining = Get-CodexOptionalDouble $remainingProperty.Value
        if ($null -eq $remaining) { continue }

        $limitIdProperty = $meter.PSObject.Properties["limit_id"]
        $limitNameProperty = $meter.PSObject.Properties["limit_name"]
        $slotProperty = $meter.PSObject.Properties["slot"]
        $usedProperty = $meter.PSObject.Properties["used_percent"]
        $windowProperty = $meter.PSObject.Properties["window_duration_mins"]
        $resetProperty = $meter.PSObject.Properties["resets_at"]
        $reachedProperty = $meter.PSObject.Properties["reached_type"]
        $planProperty = $meter.PSObject.Properties["plan_type"]

        $result += [pscustomobject]@{
            key = [string]$property.Name
            limit_id = if ($null -ne $limitIdProperty) { [string]$limitIdProperty.Value } else { "codex" }
            limit_name = if ($null -ne $limitNameProperty) { [string]$limitNameProperty.Value } else { $null }
            slot = if ($null -ne $slotProperty) { [string]$slotProperty.Value } else { "primary" }
            remaining_percent = [Math]::Max(0.0, [Math]::Min(100.0, $remaining))
            used_percent = if ($null -ne $usedProperty) { Get-CodexOptionalDouble $usedProperty.Value } else { $null }
            window_duration_mins = if ($null -ne $windowProperty) { Get-CodexOptionalInt $windowProperty.Value } else { $null }
            resets_at = if ($null -ne $resetProperty) { Get-CodexOptionalDouble $resetProperty.Value } else { $null }
            reached_type = if ($null -ne $reachedProperty) { [string]$reachedProperty.Value } else { $null }
            plan_type = if ($null -ne $planProperty) { [string]$planProperty.Value } else { $null }
        }
    }
    return @($result)
}

function Select-CodexMeter {
    param(
        [Parameter(Mandatory = $true)]$Meters,
        [string]$Preferred = "auto"
    )

    $items = @($Meters)
    if ($items.Count -eq 0) { return $null }
    if ($Preferred -and $Preferred -ne "auto") {
        $exact = @($items | Where-Object { $_.key -eq $Preferred })
        if ($exact.Count -gt 0) { return $exact[0] }
    }
    return @($items | Sort-Object `
        @{ Expression = { [double]$_.remaining_percent }; Ascending = $true }, `
        @{ Expression = { if ($_.slot -eq "primary") { 0 } else { 1 } }; Ascending = $true }, `
        @{ Expression = { if ($null -eq $_.window_duration_mins) { 2147483647 } else { [int]$_.window_duration_mins } }; Ascending = $true }
    )[0]
}

function Get-CodexWindowLabel {
    param($Meter)
    if ($null -eq $Meter) { return "Codex usage" }
    $limitNameProperty = $Meter.PSObject.Properties["limit_name"]
    if ($null -ne $limitNameProperty -and $limitNameProperty.Value) {
        return [string]$limitNameProperty.Value
    }
    $windowProperty = $Meter.PSObject.Properties["window_duration_mins"]
    $minutes = if ($null -ne $windowProperty) { Get-CodexOptionalInt $windowProperty.Value } else { $null }
    if ($null -eq $minutes) {
        $slotProperty = $Meter.PSObject.Properties["slot"]
        if ($null -ne $slotProperty -and [string]$slotProperty.Value -eq "secondary") {
            return "Secondary limit"
        }
        return "Primary limit"
    }
    if ($minutes -ge 10080 -and ($minutes % 10080) -eq 0) {
        $weeks = [int]($minutes / 10080)
        if ($weeks -eq 1) { return "Weekly limit" }
        return "$weeks-week limit"
    }
    if ($minutes -ge 1440 -and ($minutes % 1440) -eq 0) {
        $days = [int]($minutes / 1440)
        if ($days -eq 1) { return "Daily limit" }
        return "$days-day limit"
    }
    if ($minutes -ge 60 -and ($minutes % 60) -eq 0) {
        $hours = [int]($minutes / 60)
        return "$hours-hour limit"
    }
    return "$minutes-minute limit"
}

function Format-CodexPercent {
    param([double]$Value)
    $rounded = [Math]::Round($Value, 1)
    if ([Math]::Abs($rounded - [Math]::Round($rounded)) -lt 0.001) {
        return ([int][Math]::Round($rounded)).ToString()
    }
    return $rounded.ToString("0.0", [Globalization.CultureInfo]::InvariantCulture)
}

function Format-CodexAge {
    param([double]$Seconds)
    if ($Seconds -lt 60) { return "just now" }
    if ($Seconds -lt 3600) { return "$([int][Math]::Floor($Seconds / 60))m ago" }
    if ($Seconds -lt 86400) { return "$([int][Math]::Floor($Seconds / 3600))h ago" }
    return "$([int][Math]::Floor($Seconds / 86400))d ago"
}

function Format-CodexResetCountdown {
    param($EpochSeconds)
    $epoch = Get-CodexOptionalDouble $EpochSeconds
    if ($null -eq $epoch -or $epoch -le 0) { return "Reset time unavailable" }
    try {
        $reset = [DateTimeOffset]::FromUnixTimeSeconds([long][Math]::Floor($epoch))
        $seconds = ($reset - [DateTimeOffset]::UtcNow).TotalSeconds
    } catch {
        return "Reset time unavailable"
    }
    if ($seconds -le 0 -and $seconds -ge -300) { return "Resetting now" }
    if ($seconds -lt -300) { return "Reset pending" }
    if ($seconds -lt 60) { return "Resets in less than 1m" }
    $span = [TimeSpan]::FromSeconds($seconds)
    if ($span.TotalDays -ge 1) {
        return "Resets in $([int][Math]::Floor($span.TotalDays))d $($span.Hours)h"
    }
    if ($span.TotalHours -ge 1) {
        return "Resets in $([int][Math]::Floor($span.TotalHours))h $($span.Minutes)m"
    }
    return "Resets in $($span.Minutes)m"
}

function Get-CodexAccentColor {
    param(
        [double]$Remaining,
        [switch]$Stale,
        [switch]$ErrorState
    )
    if ($Stale -or $ErrorState) { return "#8B96A8" }
    if ($Remaining -le 1.0) { return "#D85D65" }
    if ($Remaining -lt 10.0) { return "#D28A24" }
    return "#2FB184"
}

function Get-CodexSoftAccentColor {
    param([string]$Accent)
    switch ($Accent.ToUpperInvariant()) {
        "#D85D65" { return "#FCECEF" }
        "#D28A24" { return "#FFF5E5" }
        "#8B96A8" { return "#EEF1F5" }
        default { return "#E9F8F2" }
    }
}

function ConvertTo-CodexBrush {
    param([Parameter(Mandatory = $true)][string]$Color)
    $converter = New-Object System.Windows.Media.BrushConverter
    return $converter.ConvertFromString($Color)
}

function Set-CodexProgressArc {
    param(
        [Parameter(Mandatory = $true)]$ArcPath,
        [Parameter(Mandatory = $true)]$FullEllipse,
        [double]$Percent,
        [double]$Center = 22.0,
        [double]$Radius = 19.0
    )

    $value = [Math]::Max(0.0, [Math]::Min(100.0, $Percent))
    if ($value -le 0.001) {
        $ArcPath.Data = $null
        $FullEllipse.Visibility = [System.Windows.Visibility]::Collapsed
        return
    }
    if ($value -ge 99.999) {
        $ArcPath.Data = $null
        $FullEllipse.Visibility = [System.Windows.Visibility]::Visible
        return
    }

    $FullEllipse.Visibility = [System.Windows.Visibility]::Collapsed
    $angle = ($value / 100.0) * 360.0
    $startRadians = -90.0 * [Math]::PI / 180.0
    $endRadians = (-90.0 + $angle) * [Math]::PI / 180.0
    $start = [System.Windows.Point]::new(
        $Center + ($Radius * [Math]::Cos($startRadians)),
        $Center + ($Radius * [Math]::Sin($startRadians))
    )
    $finish = [System.Windows.Point]::new(
        $Center + ($Radius * [Math]::Cos($endRadians)),
        $Center + ($Radius * [Math]::Sin($endRadians))
    )

    $segment = New-Object System.Windows.Media.ArcSegment
    $segment.Point = $finish
    $segment.Size = [System.Windows.Size]::new($Radius, $Radius)
    $segment.IsLargeArc = ($angle -gt 180.0)
    $segment.SweepDirection = [System.Windows.Media.SweepDirection]::Clockwise
    $segment.IsStroked = $true

    $figure = New-Object System.Windows.Media.PathFigure
    $figure.StartPoint = $start
    $figure.IsClosed = $false
    $figure.IsFilled = $false
    [void]$figure.Segments.Add($segment)

    $geometry = New-Object System.Windows.Media.PathGeometry
    [void]$geometry.Figures.Add($figure)
    $ArcPath.Data = $geometry
}

function Get-CodexUsageUrl {
    param($Config)
    if ($null -ne $Config -and $Config.PSObject.Properties["usage_url"] -and $Config.usage_url) {
        return [string]$Config.usage_url
    }
    return "https://chatgpt.com/codex/settings/usage"
}

function Open-CodexUsage {
    param([Parameter(Mandatory = $true)][string]$Url)
    try {
        Start-Process $Url | Out-Null
        return $true
    } catch {
        return $false
    }
}

function Open-CodexFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    try {
        if (Test-Path -LiteralPath $Path) {
            Start-Process $Path | Out-Null
            return $true
        }
    } catch { }
    return $false
}

function Set-CodexToolWindow {
    param(
        [Parameter(Mandatory = $true)]$Window,
        [switch]$NoActivate
    )
    try {
        $helper = New-Object System.Windows.Interop.WindowInteropHelper($Window)
        [CodexUsageNotifier.NativeWindow]::MakeToolWindow($helper.Handle, [bool]$NoActivate)
    } catch { }
}

function Bring-CodexWindowToAttention {
    param([Parameter(Mandatory = $true)]$Window)
    try {
        $helper = New-Object System.Windows.Interop.WindowInteropHelper($Window)
        [CodexUsageNotifier.NativeWindow]::BringToAttention($helper.Handle)
        [CodexUsageNotifier.NativeWindow]::Flash($helper.Handle)
    } catch { }
}

function Set-CodexPopupPosition {
    param(
        [Parameter(Mandatory = $true)]$Window,
        [double]$Margin = 18.0,
        [string]$DataDir = (Get-CodexDataDir)
    )

    $workLeft = $null
    $workTop = $null
    $workRight = $null
    $workBottom = $null
    try {
        $screen = [System.Windows.Forms.Screen]::FromPoint([System.Windows.Forms.Cursor]::Position)
        $scaleX = 1.0
        $scaleY = 1.0
        try {
            $dpi = [System.Windows.Media.VisualTreeHelper]::GetDpi($Window)
            if ($dpi.DpiScaleX -gt 0) { $scaleX = $dpi.DpiScaleX }
            if ($dpi.DpiScaleY -gt 0) { $scaleY = $dpi.DpiScaleY }
        } catch { }
        $workLeft = $screen.WorkingArea.Left / $scaleX
        $workTop = $screen.WorkingArea.Top / $scaleY
        $workRight = $screen.WorkingArea.Right / $scaleX
        $workBottom = $screen.WorkingArea.Bottom / $scaleY
    } catch {
        $work = [System.Windows.SystemParameters]::WorkArea
        $workLeft = $work.Left
        $workTop = $work.Top
        $workRight = $work.Right
        $workBottom = $work.Bottom
    }

    $left = $workRight - $Window.Width - $Margin
    $top = $workBottom - $Window.Height - $Margin

    try {
        $config = Get-CodexConfig -DataDir $DataDir
        $widgetEnabled = [bool](Get-CodexUiValue -Config $config -Name "live_widget" -Default $true)
        if ($widgetEnabled) {
            $state = Get-CodexUiState -DataDir $DataDir
            $widgetLeft = Get-CodexOptionalDouble $state.left
            $widgetTop = Get-CodexOptionalDouble $state.top
            if ($null -eq $widgetLeft -or $null -eq $widgetTop) {
                $widgetLeft = $workRight - 226.0 - $Margin
                $widgetTop = $workBottom - 78.0 - $Margin
            }

            $widgetRight = $widgetLeft + 226.0
            $widgetBottom = $widgetTop + 78.0
            $popupRight = $left + $Window.Width
            $popupBottom = $top + $Window.Height
            $overlaps = (
                $left -lt $widgetRight -and
                $popupRight -gt $widgetLeft -and
                $top -lt $widgetBottom -and
                $popupBottom -gt $widgetTop
            )
            if ($overlaps) {
                $left = [Math]::Max(
                    $workLeft + $Margin,
                    [Math]::Min($workRight - $Window.Width - $Margin, $widgetRight - $Window.Width)
                )
                $top = $widgetTop - $Window.Height - 12.0
            }
        }
    } catch {
        # Falling back to the standard lower-right position is safe.
    }

    $Window.Left = [Math]::Max(
        $workLeft + $Margin,
        [Math]::Min($workRight - $Window.Width - $Margin, $left)
    )
    $Window.Top = [Math]::Max(
        $workTop + $Margin,
        [Math]::Min($workBottom - $Window.Height - $Margin, $top)
    )
}

function Set-CodexWidgetPosition {
    param(
        [Parameter(Mandatory = $true)]$Window,
        [Parameter(Mandatory = $true)]$State
    )

    $hasSaved = ($null -ne $State.left -and $null -ne $State.top)
    if ($hasSaved) {
        try {
            $left = [double]$State.left
            $top = [double]$State.top
            $minLeft = [System.Windows.SystemParameters]::VirtualScreenLeft + 4
            $minTop = [System.Windows.SystemParameters]::VirtualScreenTop + 4
            $maxLeft = [System.Windows.SystemParameters]::VirtualScreenLeft + [System.Windows.SystemParameters]::VirtualScreenWidth - $Window.Width - 4
            $maxTop = [System.Windows.SystemParameters]::VirtualScreenTop + [System.Windows.SystemParameters]::VirtualScreenHeight - $Window.Height - 4
            $Window.Left = [Math]::Max($minLeft, [Math]::Min($maxLeft, $left))
            $Window.Top = [Math]::Max($minTop, [Math]::Min($maxTop, $top))
            return
        } catch { }
    }
    $work = [System.Windows.SystemParameters]::WorkArea
    $Window.Left = $work.Right - $Window.Width - 18
    $Window.Top = $work.Bottom - $Window.Height - 18
}

function Write-CodexUiLog {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [string]$DataDir = (Get-CodexDataDir),
        [string]$FileName = "ui.log"
    )
    try {
        New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
        $path = Join-Path $DataDir $FileName
        if ((Test-Path -LiteralPath $path -PathType Leaf) -and
            (Get-Item -LiteralPath $path).Length -ge 2000000) {
            $backup = "$path.1"
            Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
            Move-Item -LiteralPath $path -Destination $backup -Force
        }
        $line = "$(Get-Date -Format o) $Message$([Environment]::NewLine)"
        [System.IO.File]::AppendAllText($path, $line, (New-Object System.Text.UTF8Encoding($false)))
    } catch { }
}
