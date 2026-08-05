Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$InstallDir = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $InstallDir ".venv\Scripts\python.exe") (Join-Path $InstallDir "codex_usage_notifier.py") --test-reset-alert
if ($LASTEXITCODE -ne 0) { throw "Simulated reset alert failed." }
