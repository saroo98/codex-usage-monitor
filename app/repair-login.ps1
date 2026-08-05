[CmdletBinding()]
param([switch]$UseDeviceCode)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$InstallDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$DataDir = Join-Path $env:LOCALAPPDATA "CodexUsageNotifier"
$ConfigPath = Join-Path $DataDir "config.json"
$Python = Join-Path $InstallDir ".venv\Scripts\python.exe"
$Monitor = Join-Path $InstallDir "codex_usage_notifier.py"
$MainTask = "Codex Usage Notifier"
$WatchdogTask = "Codex Usage Notifier Watchdog"

if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "Configuration was not found. Run install.ps1 first."
}
$config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$codex = [string]$config.codex_command
if (-not $codex) { throw "codex_command is missing from config.json." }

foreach ($name in @($WatchdogTask, $MainTask)) {
    Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
}
Disable-ScheduledTask -TaskName $WatchdogTask -ErrorAction SilentlyContinue | Out-Null

try {
    & $codex logout 2>$null
    if ($UseDeviceCode) {
        & $codex login --device-auth
    } else {
        & $codex login
    }
    if ($LASTEXITCODE -ne 0) { throw "Codex login failed." }

    & $Python $Monitor --verify-account --json
    if ($LASTEXITCODE -ne 0) { throw "The new login is not ChatGPT-backed." }
    & $Python $Monitor --status
    if ($LASTEXITCODE -ne 0) { throw "The live rate-limit read failed." }
    & $Python $Monitor --baseline
    if ($LASTEXITCODE -ne 0) { throw "Could not save a new usage baseline." }

    Enable-ScheduledTask -TaskName $WatchdogTask -ErrorAction SilentlyContinue | Out-Null
    Enable-ScheduledTask -TaskName $MainTask -ErrorAction SilentlyContinue | Out-Null
    Start-ScheduledTask -TaskName $MainTask
    Write-Host "Authentication repaired and the monitor restarted."
} finally {
    Enable-ScheduledTask -TaskName $WatchdogTask -ErrorAction SilentlyContinue | Out-Null
}
