Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Python = Join-Path $Root ".venv\Scripts\python.exe"
if (-not (Test-Path -LiteralPath $Python -PathType Leaf)) {
    $Python = (Get-Command python.exe -ErrorAction Stop).Source
}
Push-Location $Root
try {
    & $Python -m compileall -q .
    if ($LASTEXITCODE -ne 0) { throw "Python compilation failed." }
    & $Python -m unittest discover -s tests -v
    if ($LASTEXITCODE -ne 0) { throw "Automated tests failed." }
} finally {
    Pop-Location
}
