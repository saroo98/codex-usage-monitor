Set-StrictMode -Version Latest
$ErrorActionPreference = "Continue"
$InstallDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $InstallDir "ui-common.ps1")

$DataDir = Get-CodexDataDir
$Python = Join-Path $InstallDir ".venv\Scripts\python.exe"
$Monitor = Join-Path $InstallDir "codex_usage_notifier.py"

function Get-StatusValue {
    param($Object, [Parameter(Mandatory = $true)][string]$Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

Write-Host "=== Live Codex usage ==="
if ((Test-Path -LiteralPath $Python -PathType Leaf) -and
    (Test-Path -LiteralPath $Monitor -PathType Leaf)) {
    & $Python $Monitor --status
} else {
    Write-Host "Installed Python or monitor script was not found."
}

Write-Host ""
Write-Host "=== Scheduled Tasks ==="
foreach ($name in @(
    "Codex Usage Notifier",
    "Codex Usage Notifier UI",
    "Codex Usage Notifier Watchdog"
)) {
    $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    if ($null -ne $task) {
        $info = Get-ScheduledTaskInfo -TaskName $name -ErrorAction SilentlyContinue
        [pscustomobject]@{
            Task = $name
            State = Get-StatusValue -Object $task -Name "State" -Default "unknown"
            LastRun = Get-StatusValue -Object $info -Name "LastRunTime"
            LastResult = Get-StatusValue -Object $info -Name "LastTaskResult"
            NextRun = Get-StatusValue -Object $info -Name "NextRunTime"
        } | Format-List
    } else {
        Write-Host "${name}: not installed"
    }
}

Write-Host "=== Monitor heartbeat ==="
$heartbeatPath = Join-Path $DataDir "heartbeat.json"
$heartbeat = Read-CodexJson -Path $heartbeatPath
if ($null -ne $heartbeat) {
    try {
        $checkedText = [string](Get-StatusValue -Object $heartbeat -Name "checked_at")
        $checked = [DateTimeOffset]::Parse($checkedText)
        $age = [Math]::Round(([DateTimeOffset]::UtcNow - $checked.ToUniversalTime()).TotalSeconds, 1)
        [pscustomobject]@{
            Status = Get-StatusValue -Object $heartbeat -Name "status" -Default "unknown"
            CheckedAt = $checked.LocalDateTime
            AgeSeconds = $age
            ProcessId = Get-StatusValue -Object $heartbeat -Name "pid"
            Failures = Get-StatusValue -Object $heartbeat -Name "consecutive_failures" -Default 0
            Error = Get-StatusValue -Object $heartbeat -Name "error"
        } | Format-List
    } catch {
        Write-Host "Monitor heartbeat could not be parsed: $($_.Exception.Message)"
    }
} else {
    Write-Host "No readable monitor heartbeat has been written."
}

Write-Host "=== Live widget heartbeat ==="
$uiHeartbeatPath = Join-Path $DataDir "ui-heartbeat.json"
$uiHeartbeat = Read-CodexJson -Path $uiHeartbeatPath
if ($null -ne $uiHeartbeat) {
    try {
        $checkedText = [string](Get-StatusValue -Object $uiHeartbeat -Name "checked_at")
        $uiChecked = [DateTimeOffset]::Parse($checkedText)
        $uiAge = [Math]::Round(([DateTimeOffset]::UtcNow - $uiChecked.ToUniversalTime()).TotalSeconds, 1)
        [pscustomobject]@{
            Status = Get-StatusValue -Object $uiHeartbeat -Name "status" -Default "unknown"
            CheckedAt = $uiChecked.LocalDateTime
            AgeSeconds = $uiAge
            ProcessId = Get-StatusValue -Object $uiHeartbeat -Name "pid"
            SelectedMeter = Get-StatusValue -Object $uiHeartbeat -Name "selected_meter"
            RemainingPercent = Get-StatusValue -Object $uiHeartbeat -Name "remaining_percent"
            AlwaysOnTop = Get-StatusValue -Object $uiHeartbeat -Name "topmost"
            Position = "$(Get-StatusValue -Object $uiHeartbeat -Name 'left'), $(Get-StatusValue -Object $uiHeartbeat -Name 'top')"
            Error = Get-StatusValue -Object $uiHeartbeat -Name "error"
        } | Format-List
    } catch {
        Write-Host "Live widget heartbeat could not be parsed: $($_.Exception.Message)"
    }
} else {
    Write-Host "No readable live widget heartbeat has been written."
}

Write-Host "=== Alert state ==="
$state = Read-CodexJson -Path (Join-Path $DataDir "state.json")
if ($null -ne $state) {
    $pending = Get-StatusValue -Object $state -Name "pending_alert"
    if ($null -ne $pending) {
        Write-Host "PENDING: $(Get-StatusValue -Object $pending -Name 'title' -Default 'Codex usage changed')"
        Write-Host (Get-StatusValue -Object $pending -Name "message" -Default "No message")
    } else {
        Write-Host "No pending alert."
    }

    $lastDelivered = Get-StatusValue -Object $state -Name "last_delivered_alert"
    if ($null -ne $lastDelivered) {
        Write-Host "Last delivered: $(Get-StatusValue -Object $lastDelivered -Name 'delivered_at' -Default 'unknown')"
        Write-Host (Get-StatusValue -Object $lastDelivered -Name "message" -Default "No message")
    }
} else {
    Write-Host "No readable state file has been written."
}
