[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri]$AppInstallerUri,
    [Parameter(Mandatory)][uri]$BundleUri,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$IdentityName,
    [Parameter(Mandatory)][string]$Publisher,
    [Parameter(Mandatory)][string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepositoryRoot

function Assert-AppInstallerUri {
    param(
        [Parameter(Mandatory)][uri]$Uri,
        [Parameter(Mandatory)][string]$ExpectedFileName,
        [Parameter(Mandatory)][string]$Label
    )

    if (-not $Uri.IsAbsoluteUri) { throw "$Label must be an absolute URI." }
    if ($Uri.Scheme -cne [Uri]::UriSchemeHttps) { throw "$Label must use HTTPS." }
    if (-not [string]::IsNullOrEmpty($Uri.UserInfo)) { throw "$Label must not contain user information." }
    if (-not [string]::IsNullOrEmpty($Uri.Fragment)) { throw "$Label must not contain a fragment." }
    if (-not [string]::IsNullOrEmpty($Uri.Query)) { throw "$Label must not contain a query." }
    $lastSlash = $Uri.AbsolutePath.LastIndexOf('/')
    $finalPathSegment = if ($lastSlash -ge 0) { $Uri.AbsolutePath.Substring($lastSlash + 1) } else { $Uri.AbsolutePath }
    if ($finalPathSegment -cne $ExpectedFileName) {
        throw "$Label must end with the exact required filename."
    }
}

if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$') { throw 'Version must be semantic major.minor.patch.' }
if ([string]::IsNullOrWhiteSpace($IdentityName)) { throw 'IdentityName must not be empty.' }
if ([string]::IsNullOrWhiteSpace($Publisher)) { throw 'Publisher must not be empty.' }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { throw 'OutputRoot must not be empty.' }
$packageVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
$fileName = 'CodexUsageMonitor.appinstaller'
$bundleFileName = "CodexUsageMonitor-$Version.msixbundle"
Assert-AppInstallerUri -Uri $AppInstallerUri -ExpectedFileName $fileName -Label 'AppInstallerUri'
Assert-AppInstallerUri -Uri $BundleUri -ExpectedFileName $bundleFileName -Label 'BundleUri'
$template = Get-Content packaging/templates/msix/CodexUsageMonitor.appinstaller -Raw
$template = $template.Replace('@@APPINSTALLER_URI@@', [Security.SecurityElement]::Escape($AppInstallerUri.AbsoluteUri))
$template = $template.Replace('@@BUNDLE_URI@@', [Security.SecurityElement]::Escape($BundleUri.AbsoluteUri))
$template = $template.Replace('@@IDENTITY_NAME@@', [Security.SecurityElement]::Escape($IdentityName))
$template = $template.Replace('@@PUBLISHER@@', [Security.SecurityElement]::Escape($Publisher))
$template = $template.Replace('@@PACKAGE_VERSION@@', $packageVersion)
if ($template.Contains('@@')) { throw 'App Installer manifest contains unresolved tokens.' }
[xml]$template | Out-Null
$releaseDirectory = [IO.Path]::GetFullPath($OutputRoot, $RepositoryRoot)
$output = Join-Path $releaseDirectory $fileName
New-Item (Split-Path -Parent $output) -ItemType Directory -Force | Out-Null
[IO.File]::WriteAllText($output, $template, [Text.UTF8Encoding]::new($false))
$output
