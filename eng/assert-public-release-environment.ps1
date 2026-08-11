[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$ExpectedWorkflowPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepositoryRoot

& "$PSScriptRoot/assert-public-release-context.ps1" -Version $Version -ExpectedWorkflowPath $ExpectedWorkflowPath | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Public release context validation failed.' }

$anchorPath = Join-Path $RepositoryRoot 'packaging\update\update-trust-anchor.txt'
if (-not (Test-Path -LiteralPath $anchorPath -PathType Leaf)) { throw 'The committed update trust anchor is missing.' }
$trustAnchor = (Get-Content -LiteralPath $anchorPath -Raw).Trim()
$privateKey = [Environment]::GetEnvironmentVariable('UPDATE_PRIVATE_KEY_BASE64')
if ([string]::IsNullOrWhiteSpace($privateKey)) { throw 'UPDATE_PRIVATE_KEY_BASE64 is missing.' }

$publicBytes = $null
$privateBytes = $null
$developmentBytes = $null
try {
    try { $publicBytes = [Convert]::FromBase64String($trustAnchor) }
    catch [FormatException] { throw 'The committed update trust anchor is not valid base64.' }
    try { $privateBytes = [Convert]::FromBase64String($privateKey) }
    catch [FormatException] { throw 'UPDATE_PRIVATE_KEY_BASE64 is not valid base64.' }
    $developmentBytes = [Convert]::FromBase64String('11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=')
    if ($publicBytes.Length -ne 32 -or $privateBytes.Length -ne 32) { throw 'The update keypair must use exact 32-byte keys.' }
    if ([Security.Cryptography.CryptographicOperations]::FixedTimeEquals($publicBytes, [byte[]]::new(32)) -or
        [Security.Cryptography.CryptographicOperations]::FixedTimeEquals($publicBytes, $developmentBytes)) {
        throw 'The committed update trust anchor is not permitted for a public release.'
    }

    & dotnet run --project tools/CodexUsageMonitor.ReleaseTool/CodexUsageMonitor.ReleaseTool.csproj --configuration Release `
        -p:UpdatePublicKeyBase64=$trustAnchor -- `
        validate-keypair --trust-anchor $trustAnchor --private-key-env UPDATE_PRIVATE_KEY_BASE64
    if ($LASTEXITCODE -ne 0) { throw 'The update private key does not match the committed trust anchor.' }
}
finally {
    [Environment]::SetEnvironmentVariable('UPDATE_PRIVATE_KEY_BASE64', $null, 'Process')
    $privateKey = $null
    if ($privateBytes) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($privateBytes) }
    if ($publicBytes) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($publicBytes) }
    if ($developmentBytes) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($developmentBytes) }
}

[pscustomobject]@{ Version = $Version; TrustAnchor = $trustAnchor; EnvironmentValidated = $true }
