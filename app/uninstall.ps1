[CmdletBinding()]
param([switch]$RemoveData)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$InstallDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$DataDir = Join-Path $env:LOCALAPPDATA "CodexUsageNotifier"

foreach ($name in @(
    "Codex Usage Notifier Watchdog",
    "Codex Usage Notifier UI",
    "Codex Usage Notifier"
)) {
    Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
}

if ($RemoveData -and (Test-Path -LiteralPath $DataDir)) {
    Remove-Item -LiteralPath $DataDir -Recurse -Force
}

Write-Host "Scheduled Tasks removed."
if ($RemoveData) { Write-Host "Configuration, state, UI position, heartbeats, and logs removed." }
Write-Host "Close this window, then delete the installed folder if it remains:"
Write-Host $InstallDir
