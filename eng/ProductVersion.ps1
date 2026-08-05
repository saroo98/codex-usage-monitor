function Get-ProductVersion {
    [CmdletBinding()]
    param(
        [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
    )

    $propsPath = Join-Path $RepositoryRoot 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) {
        throw "Product version source was not found: $propsPath"
    }

    [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    $versionNode = $props.SelectSingleNode('/Project/PropertyGroup/VersionPrefix')
    if ($null -eq $versionNode) {
        throw 'Directory.Build.props does not define VersionPrefix.'
    }
    $version = ([string]$versionNode.InnerText).Trim()
    if ($version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
        throw "Directory.Build.props contains an invalid VersionPrefix: $version"
    }

    return $version
}
