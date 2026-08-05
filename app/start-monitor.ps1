Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$MainTaskName = "Codex Usage Notifier"
$UiTaskName = "Codex Usage Notifier UI"
$WatchdogTaskName = "Codex Usage Notifier Watchdog"

foreach ($name in @($MainTaskName, $UiTaskName, $WatchdogTaskName)) {
    $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    if (-not $task) { throw "Scheduled task '$name' is not installed." }
    Enable-ScheduledTask -TaskName $name | Out-Null
}

Start-ScheduledTask -TaskName $MainTaskName
Start-ScheduledTask -TaskName $UiTaskName
Start-ScheduledTask -TaskName $WatchdogTaskName
Start-Sleep -Seconds 2
Get-ScheduledTask -TaskName @($MainTaskName, $UiTaskName, $WatchdogTaskName) |
    Format-Table TaskName, State
