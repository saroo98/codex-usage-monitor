[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository = 'saroo98/codex-usage-monitor',
    [string]$EnvironmentName = 'native-production-release',
    [Parameter(Mandatory)][string]$PrivateBackupDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) { throw 'configure-update-signing.ps1 is supported only on Windows.' }
if ($Repository -cne 'saroo98/codex-usage-monitor') { throw 'The update signing setup is restricted to saroo98/codex-usage-monitor.' }
if ($EnvironmentName -cne 'native-production-release') { throw 'The update signing setup requires the native-production-release environment.' }

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$trustAnchorDirectory = Join-Path $repositoryRoot 'packaging\update'
$trustAnchorPath = Join-Path $trustAnchorDirectory 'update-trust-anchor.txt'
if (Test-Path -LiteralPath $trustAnchorPath) { throw 'The repository update trust anchor already exists.' }

$privateKeyPath = Join-Path ([IO.Path]::GetFullPath($PrivateBackupDirectory)) 'codex-usage-monitor-update-ed25519.key'
$backupPublicKeyPath = Join-Path ([IO.Path]::GetFullPath($PrivateBackupDirectory)) 'codex-usage-monitor-update-ed25519-public.txt'
$privateBytes = $null
$privateBase64 = $null
$generated = $false
$temporaryTrustAnchorPath = $null

try {
    & "$PSScriptRoot/new-update-signing-key.ps1" -OutputDirectory $PrivateBackupDirectory | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Update signing key generation failed.' }
    $generated = $true

    $trustAnchor = (Get-Content -LiteralPath $backupPublicKeyPath -Raw).Trim()
    $publicBytes = [Convert]::FromBase64String($trustAnchor)
    try {
        if ($publicBytes.Length -ne 32) { throw 'The generated update trust anchor is invalid.' }
    }
    finally { [Security.Cryptography.CryptographicOperations]::ZeroMemory($publicBytes) }

    & gh auth status | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI authentication is not available.' }

    $privateBytes = [IO.File]::ReadAllBytes($privateKeyPath)
    if ($privateBytes.Length -ne 32) { throw 'The generated update private key is invalid.' }
    $privateBase64 = [Convert]::ToBase64String($privateBytes)
    $privateBase64 | & gh secret set UPDATE_PRIVATE_KEY_BASE64 --repo $Repository --env $EnvironmentName
    if ($LASTEXITCODE -ne 0) { throw 'GitHub rejected the update signing secret write.' }

    New-Item -ItemType Directory -Path $trustAnchorDirectory -Force | Out-Null
    $temporaryTrustAnchorPath = Join-Path $trustAnchorDirectory ('.update-trust-anchor-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($temporaryTrustAnchorPath, $trustAnchor + "`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporaryTrustAnchorPath, $trustAnchorPath, $false)
    $temporaryTrustAnchorPath = $null

    Write-Host 'Update signing configuration completed.'
    Write-Host "Public trust anchor: $trustAnchorPath"
    Write-Host "Private backup: $privateKeyPath"
}
catch {
    $failure = $_
    if ($temporaryTrustAnchorPath -and (Test-Path -LiteralPath $temporaryTrustAnchorPath)) {
        Remove-Item -LiteralPath $temporaryTrustAnchorPath -Force -ErrorAction SilentlyContinue
    }
    if ($generated -and -not (Test-Path -LiteralPath $trustAnchorPath)) {
        foreach ($path in @($backupPublicKeyPath, $privateKeyPath)) {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        }
    }
    throw $failure
}
finally {
    if ($privateBytes) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($privateBytes) }
    $privateBytes = $null
    $privateBase64 = $null
}
