[CmdletBinding()]
param(
    [string]$InstallDir = (Split-Path -Parent $MyInvocation.MyCommand.Path),
    [switch]$DoNotStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$MainTaskName = "Codex Usage Notifier"
$UiTaskName = "Codex Usage Notifier UI"
$WatchdogTaskName = "Codex Usage Notifier Watchdog"
$PythonW = Join-Path $InstallDir ".venv\Scripts\pythonw.exe"
$Monitor = Join-Path $InstallDir "codex_usage_notifier.py"
$LiveWidget = Join-Path $InstallDir "live-widget.ps1"
$Watchdog = Join-Path $InstallDir "watchdog.ps1"
$PowerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"

foreach ($required in @(
    $PythonW,
    $Monitor,
    $LiveWidget,
    $Watchdog,
    $PowerShell,
    (Join-Path $InstallDir "ui-common.ps1"),
    (Join-Path $InstallDir "live-widget.xaml"),
    (Join-Path $InstallDir "popup.xaml")
)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required installed file was not found: $required"
    }
}

foreach ($taskName in @($MainTaskName, $UiTaskName, $WatchdogTaskName)) {
    $existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($existing) {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
}

$currentIdentity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$userId = $currentIdentity.Name
$principal = New-ScheduledTaskPrincipal `
    -UserId $userId `
    -LogonType Interactive `
    -RunLevel Limited

$mainArguments = '"{0}" --monitor' -f $Monitor
$mainAction = New-ScheduledTaskAction `
    -Execute $PythonW `
    -Argument $mainArguments `
    -WorkingDirectory $InstallDir
$mainTrigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
$mainSettings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 10 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew
$mainTask = New-ScheduledTask `
    -Action $mainAction `
    -Trigger $mainTrigger `
    -Principal $principal `
    -Settings $mainSettings `
    -Description "Monitors Codex rate-limit data and alerts when capacity returns."
Register-ScheduledTask -TaskName $MainTaskName -InputObject $mainTask -Force | Out-Null

$uiArguments = '-Sta -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}"' -f $LiveWidget
$uiAction = New-ScheduledTaskAction `
    -Execute $PowerShell `
    -Argument $uiArguments `
    -WorkingDirectory $InstallDir
$uiTrigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
$uiSettings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 10 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew
$uiTask = New-ScheduledTask `
    -Action $uiAction `
    -Trigger $uiTrigger `
    -Principal $principal `
    -Settings $uiSettings `
    -Description "Displays the compact live Codex usage widget."
Register-ScheduledTask -TaskName $UiTaskName -InputObject $uiTask -Force | Out-Null

$watchdogArguments = '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}"' -f $Watchdog
$watchdogAction = New-ScheduledTaskAction `
    -Execute $PowerShell `
    -Argument $watchdogArguments `
    -WorkingDirectory $InstallDir
$watchdogIntervalTrigger = New-ScheduledTaskTrigger `
    -Once `
    -At (Get-Date).AddMinutes(2) `
    -RepetitionInterval (New-TimeSpan -Minutes 5) `
    -RepetitionDuration (New-TimeSpan -Days 3650)
$watchdogLogonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
$watchdogSettings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 1) `
    -MultipleInstances IgnoreNew
$watchdogTask = New-ScheduledTask `
    -Action $watchdogAction `
    -Trigger @($watchdogLogonTrigger, $watchdogIntervalTrigger) `
    -Principal $principal `
    -Settings $watchdogSettings `
    -Description "Restarts the Codex usage monitor and live widget when their heartbeats are stale."
Register-ScheduledTask -TaskName $WatchdogTaskName -InputObject $watchdogTask -Force | Out-Null

if ($DoNotStart) {
    foreach ($name in @($MainTaskName, $UiTaskName, $WatchdogTaskName)) {
        Disable-ScheduledTask -TaskName $name | Out-Null
    }
} else {
    Start-ScheduledTask -TaskName $MainTaskName
    Start-ScheduledTask -TaskName $UiTaskName
    Start-Sleep -Seconds 2
}

foreach ($name in @($MainTaskName, $UiTaskName, $WatchdogTaskName)) {
    $task = Get-ScheduledTask -TaskName $name
    Write-Host "Scheduled task installed: $name ($($task.State))"
}
Write-Host "Interactive user: $userId"
