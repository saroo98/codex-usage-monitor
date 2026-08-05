[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri]$BaseUri,
    [string]$Version,
    [string]$IdentityName = 'saroo98.CodexUsageMonitor',
    [Parameter(Mandatory)][string]$Publisher,
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepositoryRoot
. "$PSScriptRoot/ProductVersion.ps1"
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = Get-ProductVersion -RepositoryRoot $RepositoryRoot }
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$') { throw 'Version must be semantic major.minor.patch.' }
$packageVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
$base = $BaseUri.AbsoluteUri.TrimEnd('/') + '/'
if ($BaseUri.Scheme -ne 'https') { throw 'App Installer feeds must use HTTPS.' }
$fileName = "CodexUsageMonitor-$Version.appinstaller"
$bundleName = "CodexUsageMonitor-$Version.msixbundle"
$template = Get-Content packaging/templates/msix/CodexUsageMonitor.appinstaller -Raw
$template = $template.Replace('@@APPINSTALLER_URI@@', [Security.SecurityElement]::Escape($base + $fileName))
$template = $template.Replace('@@BUNDLE_URI@@', [Security.SecurityElement]::Escape($base + $bundleName))
$template = $template.Replace('@@IDENTITY_NAME@@', [Security.SecurityElement]::Escape($IdentityName))
$template = $template.Replace('@@PUBLISHER@@', [Security.SecurityElement]::Escape($Publisher))
$template = $template.Replace('@@PACKAGE_VERSION@@', $packageVersion)
if ($template.Contains('@@')) { throw 'App Installer manifest contains unresolved tokens.' }
$releaseDirectory = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $RepositoryRoot 'artifacts/release'
} else {
    [IO.Path]::GetFullPath($OutputRoot, $RepositoryRoot)
}
$output = Join-Path $releaseDirectory $fileName
New-Item (Split-Path -Parent $output) -ItemType Directory -Force | Out-Null
[IO.File]::WriteAllText($output, $template, [Text.UTF8Encoding]::new($false))
[xml](Get-Content $output -Raw) | Out-Null
$output
