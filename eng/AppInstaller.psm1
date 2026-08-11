Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Xml.Linq

$script:AppInstallerNamespace = 'http://schemas.microsoft.com/appx/appinstaller/2018'

function Assert-ExactAttributes {
    param(
        [Parameter(Mandatory)][System.Xml.Linq.XElement]$Element,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Names,
        [switch]$AllowDefaultNamespaceDeclaration
    )

    $namespaceDeclarations = @($Element.Attributes() | Where-Object IsNamespaceDeclaration)
    if ($AllowDefaultNamespaceDeclaration) {
        if ($namespaceDeclarations.Count -ne 1 -or
            $namespaceDeclarations[0].Name.LocalName -cne 'xmlns' -or
            $namespaceDeclarations[0].Value -cne $script:AppInstallerNamespace) {
            throw "The $($Element.Name.LocalName) element must have only the required default namespace declaration."
        }
    } elseif ($namespaceDeclarations.Count -ne 0) {
        throw "The $($Element.Name.LocalName) element contains an unexpected namespace declaration."
    }

    $attributes = @($Element.Attributes() | Where-Object { -not $_.IsNamespaceDeclaration })
    if ($attributes.Count -ne $Names.Count) {
        throw "The $($Element.Name.LocalName) element has an unexpected attribute count."
    }
    foreach ($attribute in $attributes) {
        if (-not [string]::IsNullOrEmpty($attribute.Name.NamespaceName) -or $Names -cnotcontains $attribute.Name.LocalName) {
            throw "The $($Element.Name.LocalName) element contains an unexpected attribute."
        }
    }
    foreach ($name in $Names) {
        if ($null -eq $Element.Attribute($name)) {
            throw "The $($Element.Name.LocalName) element is missing a required attribute."
        }
    }
}

function Assert-ElementNodes {
    param(
        [Parameter(Mandatory)][System.Xml.Linq.XElement]$Element,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$ChildNames
    )

    foreach ($node in $Element.Nodes()) {
        if ($node -is [System.Xml.Linq.XElement]) { continue }
        if ($node -is [System.Xml.Linq.XText] -and [string]::IsNullOrWhiteSpace($node.Value)) { continue }
        throw "The $($Element.Name.LocalName) element contains unexpected content."
    }
    $children = @($Element.Elements())
    if ($children.Count -ne $ChildNames.Count) {
        throw "The $($Element.Name.LocalName) element has an unexpected child count."
    }
    for ($index = 0; $index -lt $ChildNames.Count; $index++) {
        $expectedName = "{$script:AppInstallerNamespace}$($ChildNames[$index])"
        if ($children[$index].Name.ToString() -cne $expectedName) {
            throw "The $($Element.Name.LocalName) element contains an unexpected child."
        }
    }
}

function Assert-HttpsUri {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$ExpectedFileName
    )

    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri)) {
        throw "$Label must be an absolute URI."
    }
    if ($uri.Scheme -cne [Uri]::UriSchemeHttps) { throw "$Label must use HTTPS." }
    if (-not [string]::IsNullOrEmpty($uri.UserInfo)) { throw "$Label must not contain user information." }
    if (-not [string]::IsNullOrEmpty($uri.Fragment)) { throw "$Label must not contain a fragment." }
    if (-not [string]::IsNullOrEmpty($uri.Query)) { throw "$Label must not contain a query." }
    $lastSlash = $uri.AbsolutePath.LastIndexOf('/')
    $finalPathSegment = if ($lastSlash -ge 0) { $uri.AbsolutePath.Substring($lastSlash + 1) } else { $uri.AbsolutePath }
    if ($finalPathSegment -cne $ExpectedFileName) {
        throw "$Label must end with the exact required filename."
    }
    return $uri
}

function Assert-AppInstallerFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][uri]$ExpectedAppInstallerUri,
        [Parameter(Mandatory)][uri]$ExpectedBundleUri,
        [Parameter(Mandatory)][string]$ExpectedIdentityName,
        [Parameter(Mandatory)][string]$ExpectedPublisher
    )

    if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$') { throw 'Version must be semantic major.minor.patch.' }
    if ([string]::IsNullOrWhiteSpace($ExpectedIdentityName)) { throw 'ExpectedIdentityName must not be empty.' }
    if ([string]::IsNullOrWhiteSpace($ExpectedPublisher)) { throw 'ExpectedPublisher must not be empty.' }
    $packageVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw 'The App Installer file is missing.' }
    $bytes = [IO.File]::ReadAllBytes($fullPath)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw 'The App Installer file must be UTF-8 without a BOM.'
    }
    try {
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    }
    catch {
        throw 'The App Installer file must contain valid UTF-8.'
    }
    if ($text.Contains('@@', [StringComparison]::Ordinal)) { throw 'The App Installer file contains an unresolved token.' }

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $stringReader = [IO.StringReader]::new($text)
    $xmlReader = [Xml.XmlReader]::Create($stringReader, $settings)
    try {
        $document = [System.Xml.Linq.XDocument]::Load($xmlReader, [System.Xml.Linq.LoadOptions]::PreserveWhitespace)
    }
    catch {
        throw 'The App Installer file is not valid XML.'
    }
    finally {
        $xmlReader.Dispose()
        $stringReader.Dispose()
    }

    if ($null -eq $document.Declaration -or $document.Declaration.Version -cne '1.0' -or
        $document.Declaration.Encoding -cne 'utf-8' -or -not [string]::IsNullOrEmpty($document.Declaration.Standalone)) {
        throw 'The App Installer XML declaration is invalid.'
    }
    foreach ($node in $document.Nodes()) {
        if ($node -is [System.Xml.Linq.XElement]) { continue }
        if ($node -is [System.Xml.Linq.XText] -and [string]::IsNullOrWhiteSpace($node.Value)) { continue }
        throw 'The App Installer document contains unexpected top-level content.'
    }
    $root = $document.Root
    if ($null -eq $root -or $root.Name.ToString() -cne "{$script:AppInstallerNamespace}AppInstaller") {
        throw 'The App Installer root element or namespace is invalid.'
    }
    Assert-ExactAttributes -Element $root -Names @('Uri', 'Version') -AllowDefaultNamespaceDeclaration
    Assert-ElementNodes -Element $root -ChildNames @('MainBundle', 'UpdateSettings')

    $mainBundle = @($root.Elements())[0]
    Assert-ExactAttributes -Element $mainBundle -Names @('Name', 'Publisher', 'Version', 'Uri')
    Assert-ElementNodes -Element $mainBundle -ChildNames @()
    $updateSettings = @($root.Elements())[1]
    Assert-ExactAttributes -Element $updateSettings -Names @()
    Assert-ElementNodes -Element $updateSettings -ChildNames @('OnLaunch', 'AutomaticBackgroundTask')
    $onLaunch = @($updateSettings.Elements())[0]
    Assert-ExactAttributes -Element $onLaunch -Names @('HoursBetweenUpdateChecks')
    Assert-ElementNodes -Element $onLaunch -ChildNames @()
    $backgroundTask = @($updateSettings.Elements())[1]
    Assert-ExactAttributes -Element $backgroundTask -Names @()
    Assert-ElementNodes -Element $backgroundTask -ChildNames @()

    $appInstallerUriValue = $root.Attribute('Uri').Value
    $bundleUriValue = $mainBundle.Attribute('Uri').Value
    $null = Assert-HttpsUri -Value $appInstallerUriValue `
        -Label 'App Installer self URI' -ExpectedFileName 'CodexUsageMonitor.appinstaller'
    $null = Assert-HttpsUri -Value $bundleUriValue `
        -Label 'App Installer bundle URI' -ExpectedFileName "CodexUsageMonitor-$Version.msixbundle"
    if ($appInstallerUriValue -cne $ExpectedAppInstallerUri.AbsoluteUri) { throw 'The App Installer self URI is not the exact expected release URI.' }
    if ($bundleUriValue -cne $ExpectedBundleUri.AbsoluteUri) { throw 'The App Installer bundle URI is not the exact expected release URI.' }
    if ($root.Attribute('Version').Value -cne $packageVersion -or $mainBundle.Attribute('Version').Value -cne $packageVersion) {
        throw 'The App Installer package version does not match the release version.'
    }
    if ($mainBundle.Attribute('Name').Value -cne $ExpectedIdentityName) { throw 'The App Installer identity does not match the package identity.' }
    if ($mainBundle.Attribute('Publisher').Value -cne $ExpectedPublisher) { throw 'The App Installer publisher does not match the signed package publisher.' }
    if ($onLaunch.Attribute('HoursBetweenUpdateChecks').Value -cne '24') { throw 'The App Installer launch update interval must be exactly 24 hours.' }
}

Export-ModuleMember -Function Assert-AppInstallerFile
