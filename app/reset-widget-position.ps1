Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $ScriptDir "ui-common.ps1")

$DataDir = Get-CodexDataDir
$state = Get-CodexUiState -DataDir $DataDir
$state.left = $null
$state.top = $null
Save-CodexUiState -State $state -DataDir $DataDir

$taskName = "Codex Usage Notifier UI"
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($task -and [string]$task.State -ne "Disabled") {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Start-ScheduledTask -TaskName $taskName
}

Write-Host "The live usage widget position was reset."
Write-Host "It will appear near the lower-right edge of the active desktop."
