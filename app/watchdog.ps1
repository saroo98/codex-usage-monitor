[CmdletBinding()]
param(
    [ValidateRange(120, 3600)][int]$MonitorStaleAfterSeconds = 300,
    [ValidateRange(20, 600)][int]$UiStaleAfterSeconds = 45
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$MainTaskName = "Codex Usage Notifier"
$UiTaskName = "Codex Usage Notifier UI"
$DataDir = Join-Path $env:LOCALAPPDATA "CodexUsageNotifier"
$HeartbeatPath = Join-Path $DataDir "heartbeat.json"
$UiHeartbeatPath = Join-Path $DataDir "ui-heartbeat.json"
$ConfigPath = Join-Path $DataDir "config.json"
$LogPath = Join-Path $DataDir "watchdog.log"

function Write-WatchdogLog {
    param([string]$Message)
    try {
        New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
        if ((Test-Path -LiteralPath $LogPath -PathType Leaf) -and
            (Get-Item -LiteralPath $LogPath).Length -ge 2000000) {
            $backup = "$LogPath.1"
            Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
            Move-Item -LiteralPath $LogPath -Destination $backup -Force
        }
        Add-Content -LiteralPath $LogPath -Encoding UTF8 -Value "$(Get-Date -Format o) $Message"
    } catch {
        # Recovery must not depend on logging.
    }
}

function Test-HeartbeatFresh {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][int]$MaximumAgeSeconds,
        [string[]]$AcceptedStatus = @("ok")
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    try {
        $heartbeat = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        $checkedAt = [DateTimeOffset]::Parse([string]$heartbeat.checked_at)
        $age = ([DateTimeOffset]::UtcNow - $checkedAt.ToUniversalTime()).TotalSeconds
        return ($age -ge -60 -and $age -le $MaximumAgeSeconds -and $AcceptedStatus -contains [string]$heartbeat.status)
    } catch {
        Write-WatchdogLog "Heartbeat could not be parsed at '$Path': $($_.Exception.Message)"
        return $false
    }
}

function Repair-Task {
    param(
        [Parameter(Mandatory = $true)][string]$TaskName,
        [Parameter(Mandatory = $true)][bool]$HeartbeatFresh
    )

    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if (-not $task) {
        Write-WatchdogLog "Task is not registered: $TaskName"
        return $false
    }
    if ($task.State -eq "Disabled") {
        Write-WatchdogLog "Task is disabled and was left stopped: $TaskName"
        return $true
    }
    if ($task.State -eq "Running" -and $HeartbeatFresh) { return $true }

    Write-WatchdogLog "Recovery triggered. Task=$TaskName; State=$($task.State); HeartbeatFresh=$HeartbeatFresh"
    if ($task.State -eq "Running") {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    Start-ScheduledTask -TaskName $TaskName
    Start-Sleep -Seconds 4
    $after = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    $afterState = if ($null -ne $after) { [string]$after.State } else { "not registered" }
    Write-WatchdogLog "Recovery result. Task=$TaskName; State=$afterState"
    return ($null -ne $after)
}

try {
    $monitorFresh = Test-HeartbeatFresh `
        -Path $HeartbeatPath `
        -MaximumAgeSeconds $MonitorStaleAfterSeconds `
        -AcceptedStatus @("ok")
    [void](Repair-Task -TaskName $MainTaskName -HeartbeatFresh $monitorFresh)

    $widgetEnabled = $true
    if (Test-Path -LiteralPath $ConfigPath -PathType Leaf) {
        try {
            $config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $uiProperty = $config.PSObject.Properties["ui"]
            if ($null -ne $uiProperty -and $null -ne $uiProperty.Value) {
                $liveProperty = $uiProperty.Value.PSObject.Properties["live_widget"]
                if ($null -ne $liveProperty) {
                    $widgetEnabled = [bool]$liveProperty.Value
                }
            }
        } catch {
            Write-WatchdogLog "Config could not be parsed while checking widget state: $($_.Exception.Message)"
        }
    }

    if ($widgetEnabled) {
        $uiFresh = Test-HeartbeatFresh `
            -Path $UiHeartbeatPath `
            -MaximumAgeSeconds $UiStaleAfterSeconds `
            -AcceptedStatus @("ok", "stale", "waiting")
        [void](Repair-Task -TaskName $UiTaskName -HeartbeatFresh $uiFresh)
    }
    exit 0
} catch {
    Write-WatchdogLog "Fatal watchdog error: $($_.Exception.ToString())"
    exit 1
}
