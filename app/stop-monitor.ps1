Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$Names = @(
    "Codex Usage Notifier Watchdog",
    "Codex Usage Notifier UI",
    "Codex Usage Notifier"
)
foreach ($name in $Names) {
    Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    Disable-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue | Out-Null
}
Write-Host "Stopped and disabled the monitor, live widget, and watchdog."
Write-Host "Run start-monitor.ps1 to resume."
