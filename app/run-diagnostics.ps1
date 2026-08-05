Set-StrictMode -Version Latest
$ErrorActionPreference = "Continue"
$InstallDir = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $InstallDir ".venv\Scripts\python.exe") (Join-Path $InstallDir "codex_usage_notifier.py") --diagnose
exit $LASTEXITCODE
