Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Set-Variable -Name WindowsSdkVersion -Value '10.0.26100.0' -Option Constant -Scope Script

function Get-ReleaseWindowsSdkVersion {
    [CmdletBinding()]
    param()

    return $script:WindowsSdkVersion
}

function Find-WindowsSdkTool {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('makeappx.exe','makepri.exe','signtool.exe')]
        [string]$Name
    )

    $programFilesX86 = ${env:ProgramFiles(x86)}
    if ([string]::IsNullOrWhiteSpace($programFilesX86)) {
        throw "Windows SDK $script:WindowsSdkVersion tool '$Name' cannot be resolved because ProgramFiles(x86) is unavailable. Install the reviewed SDK before continuing."
    }

    $path = Join-Path $programFilesX86 "Windows Kits/10/bin/$script:WindowsSdkVersion/x64/$Name"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Windows SDK $script:WindowsSdkVersion x64 tool '$Name' is absent at the reviewed path. This is an explicit toolchain-upgrade stop."
    }

    $file = Get-Item -LiteralPath $path -Force
    if ($file -isnot [System.IO.FileInfo] -or
        ($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Required Windows SDK $script:WindowsSdkVersion x64 tool '$Name' is not a regular file at the reviewed path."
    }

    return $file
}

function Resolve-RegularFileUnderRoot {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedRoot
    )

    $root = [IO.Path]::GetFullPath($ExpectedRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Signed file is outside the expected root: $fullPath"
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Signed file is missing or is not a regular file: $fullPath"
    }

    $file = Get-Item -LiteralPath $fullPath -Force
    if ($file -isnot [IO.FileInfo] -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Signed file is not a regular non-reparse-point file: $fullPath"
    }

    $cursor = $file.Directory
    while ($null -ne $cursor -and $cursor.FullName.Length -ge $root.Length) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Signed file traverses a reparse point: $fullPath"
        }
        if ($cursor.FullName.TrimEnd('\') -eq $root.TrimEnd('\')) { break }
        $cursor = $cursor.Parent
    }

    return $file
}

function Get-VerifiedAuthenticodeIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedRoot,
        [Parameter(Mandatory)][string]$ExpectedSubject
    )

    if ([string]::IsNullOrWhiteSpace($ExpectedSubject)) { throw 'Expected Authenticode subject is required.' }
    $file = Resolve-RegularFileUnderRoot -Path $Path -ExpectedRoot $ExpectedRoot
    $signTool = Find-WindowsSdkTool -Name 'signtool.exe'
    $null = & $signTool.FullName verify /pa /all /v /tw $file.FullName 2>&1 | Out-String
    $signToolExitCode = $LASTEXITCODE
    if ($signToolExitCode -ne 0) {
        throw "SignTool signature verification failed for $($file.FullName)."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "PowerShell Authenticode signature verification failed for $($file.FullName): $($signature.Status)."
    }
    if ($null -eq $signature.TimeStamperCertificate) {
        throw "Authenticode timestamp certificate is absent for $($file.FullName)."
    }

    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($file.FullName)
    try {
        if ($certificate.Subject -cne $ExpectedSubject) {
            throw "Authenticode signer subject differs from the expected publisher for $($file.FullName)."
        }
        $leafThumbprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($certificate.RawData))
        if ($leafThumbprint -notmatch '^[0-9A-F]{64}$') {
            throw "Authenticode signer SHA-256 identity is invalid for $($file.FullName)."
        }

        return [pscustomobject]@{
            Path = $file.FullName
            Subject = $certificate.Subject
            LeafSha256Thumbprint = $leafThumbprint
            CertificateNotBeforeUtc = $certificate.NotBefore.ToUniversalTime().ToString('O')
            CertificateNotAfterUtc = $certificate.NotAfter.ToUniversalTime().ToString('O')
            TimestampCertificateSubject = $signature.TimeStamperCertificate.Subject
            TimestampCertificateNotBeforeUtc = $signature.TimeStamperCertificate.NotBefore.ToUniversalTime().ToString('O')
            TimestampCertificateNotAfterUtc = $signature.TimeStamperCertificate.NotAfter.ToUniversalTime().ToString('O')
        }
    }
    finally {
        $certificate.Dispose()
    }
}

function Get-ControlDocument {
    param([Parameter(Mandatory)][string]$ControlPath)

    if (-not (Test-Path -LiteralPath $ControlPath -PathType Leaf)) { throw "Release control file is missing: $ControlPath" }
    return Get-Content -LiteralPath $ControlPath -Raw | ConvertFrom-Json
}

function Assert-ReleaseControlVersionMetadata {
    param(
        [Parameter(Mandatory)]$Control,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [switch]$RequireBinaryRecords
    )

    if ($ExpectedVersion -notmatch '^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$') {
        throw 'Expected release version must be canonical semantic version text.'
    }
    $expectedFileVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
    if ([string]$Control.version -cne $ExpectedVersion) {
        throw 'Release control version does not match the requested centralized release version.'
    }
    $controlProductVersion = [string]$Control.productVersion
    if ($controlProductVersion -cne $ExpectedVersion -and
        -not $controlProductVersion.StartsWith($ExpectedVersion + '+', [StringComparison]::Ordinal)) {
        throw 'Release control productVersion does not match the requested centralized release version.'
    }
    if ([string]$Control.fileVersion -cne $expectedFileVersion) {
        throw 'Release control fileVersion does not match the requested centralized release file version.'
    }
    if ($RequireBinaryRecords) {
        foreach ($record in @($Control.files | Where-Object { [string]$_.classification -ceq 'first-party-authenticode' })) {
            if ([string]$record.metadata.ProductVersion -cne [string]$Control.productVersion -or
                [string]$record.metadata.FileVersion -cne [string]$Control.fileVersion) {
                throw "First-party binary record version metadata differs from the release control: $($record.path)"
            }
        }
    }
}

function Get-ZipEntrySha256 {
    param([Parameter(Mandatory)][IO.Compression.ZipArchiveEntry]$Entry)

    $stream = $Entry.Open()
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($algorithm.ComputeHash($stream)) }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function Get-NormalizedContentTypes {
    param([Parameter(Mandatory)][IO.Compression.ZipArchiveEntry]$Entry)

    $reader = [IO.StreamReader]::new($Entry.Open(), [Text.UTF8Encoding]::new($false), $true)
    try { $content = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $document = [Xml.XmlDocument]::new()
    $document.PreserveWhitespace = $true
    try { $document.LoadXml($content) }
    catch { throw '[Content_Types].xml is not well-formed XML.' }
    $namespace = 'http://schemas.openxmlformats.org/package/2006/content-types'
    if ($document.DocumentElement.LocalName -cne 'Types' -or $document.DocumentElement.NamespaceURI -cne $namespace) {
        throw '[Content_Types].xml has an unexpected root element or namespace.'
    }

    $documentDeclaration = $null
    foreach ($topLevelNode in @($document.ChildNodes)) {
        if ($topLevelNode.NodeType -eq [Xml.XmlNodeType]::XmlDeclaration) {
            if ($null -ne $documentDeclaration) { throw '[Content_Types].xml contains duplicate XML declarations.' }
            $documentDeclaration = "$($topLevelNode.Version)|$($topLevelNode.Encoding)|$($topLevelNode.Standalone)"
        }
        elseif ($topLevelNode -ne $document.DocumentElement) {
            throw '[Content_Types].xml contains an unreviewed top-level node.'
        }
    }

    $rootAttributes = [System.Collections.Generic.List[string]]::new()
    foreach ($attribute in @($document.DocumentElement.Attributes)) {
        $rootAttributes.Add("$($attribute.Name)|$($attribute.NamespaceURI)|$($attribute.Value)")
    }
    $rootAttributeArray = [string[]]@($rootAttributes)
    [Array]::Sort($rootAttributeArray, [StringComparer]::Ordinal)

    $records = [System.Collections.Generic.List[string]]::new()
    foreach ($node in @($document.DocumentElement.ChildNodes)) {
        if ($node.NodeType -ne [Xml.XmlNodeType]::Element -or $node.NamespaceURI -cne $namespace -or
            $node.LocalName -notin @('Default','Override')) {
            throw '[Content_Types].xml contains an unreviewed node.'
        }
        if ($node.ChildNodes.Count -ne 0) {
            throw '[Content_Types].xml declarations must be empty and contain no text or nested nodes.'
        }
        $keyAttribute = if ($node.LocalName -ceq 'Default') { 'Extension' } else { 'PartName' }
        $allowedAttributes = @($keyAttribute, 'ContentType')
        if ($node.Name -cne $node.LocalName -or $node.Attributes.Count -ne 2 -or
            @($node.Attributes | Where-Object { $_.Name -notin $allowedAttributes -or -not [string]::IsNullOrEmpty($_.NamespaceURI) }).Count -gt 0 -or
            [string]::IsNullOrWhiteSpace($node.GetAttribute($keyAttribute)) -or
            [string]::IsNullOrWhiteSpace($node.GetAttribute('ContentType'))) {
            throw '[Content_Types].xml contains an unreviewed declaration shape.'
        }
        $attributes = [string[]]@($node.Attributes | ForEach-Object { "$($_.Name)|$($_.NamespaceURI)|$($_.Value)" })
        [Array]::Sort($attributes, [StringComparer]::Ordinal)
        $records.Add("$($node.Name)|$($node.NamespaceURI)|$($attributes -join [char]0x1f)")
    }

    return [pscustomobject]@{
        declaration = $documentDeclaration
        rootName = $document.DocumentElement.Name
        rootNamespace = $document.DocumentElement.NamespaceURI
        rootAttributes = $rootAttributeArray
        declarations = [string[]]@($records)
    }
}

function Get-ReviewedPackageArchiveEntries {
    param([Parameter(Mandatory)][string]$PackagePath)

    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw "Package archive is missing: $PackagePath" }
    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $records = [System.Collections.Generic.List[object]]::new()
        $foldedNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in @($archive.Entries)) {
            $name = $entry.FullName
            if ([string]::IsNullOrWhiteSpace($name) -or $name.EndsWith('/', [StringComparison]::Ordinal) -or
                $name.Contains('\', [StringComparison]::Ordinal) -or $name.StartsWith('/', [StringComparison]::Ordinal) -or
                $name -match '(^|/)\.\.(/|$)' -or -not $foldedNames.Add($name)) {
                throw "Package archive contains an unsafe, directory, or duplicate entry: $name"
            }
            $record = [ordered]@{
                path = $name
                size = $entry.Length
                sha256 = Get-ZipEntrySha256 -Entry $entry
                contentTypes = $null
            }
            if ($name -ceq '[Content_Types].xml') { $record.contentTypes = Get-NormalizedContentTypes -Entry $entry }
            $records.Add([pscustomobject]$record)
        }
        return @($records | Sort-Object { $_.path })
    }
    finally { $archive.Dispose() }
}

function Get-PackagePayloadControl {
    param([Parameter(Mandatory)][string]$PackagePath)

    $entries = @(Get-ReviewedPackageArchiveEntries -PackagePath $PackagePath)
    if (@($entries | Where-Object { [string]$_.path -ceq 'AppxSignature.p7x' }).Count -gt 0) {
        throw 'Unsigned package control input already contains AppxSignature.p7x.'
    }
    if (@($entries | Where-Object { [string]$_.path -ceq '[Content_Types].xml' }).Count -ne 1) {
        throw 'Unsigned package control input must contain exactly one [Content_Types].xml.'
    }
    return [pscustomobject]@{ entries = $entries }
}

function Assert-PackageArchiveMatchesControl {
    param(
        [Parameter(Mandatory)][string]$SignedPackagePath,
        [Parameter(Mandatory)]$PayloadControl
    )

    $expectedEntries = @($PayloadControl.entries)
    $actualEntries = @(Get-ReviewedPackageArchiveEntries -PackagePath $SignedPackagePath)
    $expectedNames = [string[]]@($expectedEntries | ForEach-Object { [string]$_.path })
    $actualNames = [string[]]@($actualEntries | ForEach-Object { [string]$_.path })
    if (@($expectedNames | Group-Object { $_.ToLowerInvariant() } | Where-Object Count -gt 1).Count -gt 0 -or
        @($expectedNames | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -ceq 'AppxSignature.p7x' }).Count -gt 0 -or
        @($expectedNames | Where-Object { $_ -ceq '[Content_Types].xml' }).Count -ne 1) {
        throw 'Unsigned package payload control contains an unsafe, duplicate, or pre-signed entry set.'
    }
    $reviewedActualNames = [string[]]@($actualNames | Where-Object { $_ -cne 'AppxSignature.p7x' })
    if (@(Compare-Object -ReferenceObject $expectedNames -DifferenceObject $reviewedActualNames -CaseSensitive).Count -gt 0 -or
        @($actualEntries | Where-Object { [string]$_.path -ceq 'AppxSignature.p7x' -and [long]$_.size -gt 0 }).Count -ne 1) {
        throw 'Signed package archive entry set differs beyond the reviewed AppxSignature.p7x addition.'
    }

    $signatureAttributes = [string[]]@(
        'ContentType||application/vnd.ms-appx.signature',
        'PartName||/AppxSignature.p7x'
    )
    [Array]::Sort($signatureAttributes, [StringComparer]::Ordinal)
    $signatureContentType = "Override|http://schemas.openxmlformats.org/package/2006/content-types|$($signatureAttributes -join [char]0x1f)"
    foreach ($expected in $expectedEntries) {
        $actual = @($actualEntries | Where-Object { [string]$_.path -ceq [string]$expected.path })
        if ($actual.Count -ne 1) { throw "Signed package archive entry set is ambiguous: $($expected.path)" }
        $actual = $actual[0]
        if ([string]$expected.path -ceq '[Content_Types].xml') {
            $expectedTypes = $expected.contentTypes
            $actualTypes = $actual.contentTypes
            foreach ($property in @('declaration','rootName','rootNamespace')) {
                if ([string]$expectedTypes.$property -cne [string]$actualTypes.$property) {
                    throw "Signed package [Content_Types].xml changed its $property."
                }
            }
            if (@(Compare-Object -ReferenceObject ([string[]]@($expectedTypes.rootAttributes)) -DifferenceObject ([string[]]@($actualTypes.rootAttributes)) -CaseSensitive -SyncWindow 0).Count -gt 0) {
                throw 'Signed package [Content_Types].xml changed its root attributes.'
            }
            $expectedDeclarations = [string[]]@($expectedTypes.declarations)
            if ($expectedDeclarations -contains $signatureContentType) { throw 'Unsigned package control already declares the signature content type.' }
            $actualDeclarations = [System.Collections.Generic.List[string]]::new()
            foreach ($value in @($actualTypes.declarations)) { $actualDeclarations.Add([string]$value) }
            if (-not $actualDeclarations.Remove($signatureContentType)) {
                throw 'Signed package [Content_Types].xml lacks the reviewed signature-content-type declaration.'
            }
            if ($actualDeclarations.Contains($signatureContentType)) {
                throw 'Signed package [Content_Types].xml contains duplicate signature declarations.'
            }
            if (@(Compare-Object -ReferenceObject $expectedDeclarations -DifferenceObject ([string[]]@($actualDeclarations)) -CaseSensitive -SyncWindow 0).Count -gt 0) {
                throw 'Signed package [Content_Types].xml changed beyond the reviewed signature declaration.'
            }
        }
        elseif ([long]$actual.size -ne [long]$expected.size -or [string]$actual.sha256 -cne [string]$expected.sha256) {
            throw "Signed package payload entry changed outside Authenticode signing: $($expected.path)"
        }
    }
}

function Test-ReviewedFirstPartyBinaryPath {
    param([Parameter(Mandatory)][string]$RelativePath)

    $segments = $RelativePath.Replace('\','/').Split('/')
    if ($segments.Count -ne 3 -or
        $segments[0] -notin @('win-x64','win-arm64') -or
        $segments[1] -notin @('self-contained','framework-dependent')) {
        return $false
    }
    $name = $segments[2]
    return $name -in @('CodexUsageMonitor.exe','CodexUsageMonitor.UpdaterHost.exe') -or
        ($name.EndsWith('.dll', [StringComparison]::Ordinal) -and
            [IO.Path]::GetFileNameWithoutExtension($name).StartsWith('CodexUsageMonitor', [StringComparison]::Ordinal))
}

function Get-SafeTreeFiles {
    param([Parameter(Mandatory)][string]$Root)

    $resolvedRoot = [IO.Path]::GetFullPath($Root)
    if (-not (Test-Path -LiteralPath $resolvedRoot -PathType Container)) { throw "Signed tree is missing: $resolvedRoot" }
    $rootItem = Get-Item -LiteralPath $resolvedRoot -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Signed tree root is a reparse point: $resolvedRoot" }

    $items = @(Get-ChildItem -LiteralPath $resolvedRoot -Force -Recurse)
    $reparsePoints = @($items | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
    if ($reparsePoints.Count -gt 0) { throw "Signed tree contains a reparse point: $($reparsePoints[0].FullName)" }
    return @($items | Where-Object { $_ -is [IO.FileInfo] } | Sort-Object { [IO.Path]::GetRelativePath($resolvedRoot, $_.FullName) })
}

function Assert-SignedTreeMatchesControl {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SignedRoot,
        [Parameter(Mandatory)][string]$ControlPath,
        [Parameter(Mandatory)][string]$ExpectedSubject,
        [Parameter(Mandatory)][string]$ExpectedVersion
    )

    $root = [IO.Path]::GetFullPath($SignedRoot)
    $control = Get-ControlDocument -ControlPath $ControlPath
    if ([int]$control.schemaVersion -ne 1 -or [string]$control.kind -cne 'production-binaries') {
        throw 'Binary signing control file has an unsupported schema or kind.'
    }
    Assert-ReleaseControlVersionMetadata -Control $control -ExpectedVersion $ExpectedVersion -RequireBinaryRecords
    $expected = @($control.files)
    $actualFiles = @(Get-SafeTreeFiles -Root $root)
    $actualPaths = @($actualFiles | ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\','/') })
    $expectedPaths = @($expected | ForEach-Object { [string]$_.path })
    $differences = @(Compare-Object -ReferenceObject $expectedPaths -DifferenceObject $actualPaths -CaseSensitive)
    if ($differences.Count -gt 0) {
        throw 'Signed binary return has extra, missing, renamed, or case-changed files.'
    }

    $identities = [System.Collections.Generic.List[object]]::new()
    foreach ($record in $expected) {
        $relativePath = [string]$record.path
        $file = Resolve-RegularFileUnderRoot -Path (Join-Path $root $relativePath) -ExpectedRoot $root
        switch ([string]$record.classification) {
            'immutable' {
                if (Test-ReviewedFirstPartyBinaryPath -RelativePath $relativePath) {
                    throw "Reviewed first-party binary is not classified as mutable-by-signing: $relativePath"
                }
                $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
                if ($hash -cne [string]$record.sha256 -or $file.Length -ne [long]$record.size) {
                    throw "Non-signable file changed in the signed binary return: $relativePath"
                }
            }
            'first-party-authenticode' {
                if (-not (Test-ReviewedFirstPartyBinaryPath -RelativePath $relativePath)) {
                    throw "Binary control permits an unreviewed path to mutate during signing: $relativePath"
                }
                $version = $file.VersionInfo
                foreach ($property in @('ProductName','CompanyName','ProductVersion','FileVersion')) {
                    if ([string]$version.$property -cne [string]$record.metadata.$property) {
                        throw "First-party binary metadata changed after signing: $relativePath ($property)"
                    }
                }
                $identities.Add((Get-VerifiedAuthenticodeIdentity -Path $file.FullName -ExpectedRoot $root -ExpectedSubject $ExpectedSubject))
            }
            default { throw "Unknown binary control classification for $relativePath." }
        }
    }

    if ($identities.Count -eq 0) { throw 'Signed binary return contains no verified first-party files.' }
    $subjects = @($identities.Subject | Sort-Object -Unique -CaseSensitive)
    $thumbprints = @($identities.LeafSha256Thumbprint | Sort-Object -Unique -CaseSensitive)
    if ($subjects.Count -ne 1 -or $thumbprints.Count -ne 1) {
        throw 'Signed binary return contains multiple signer subjects or SHA-256 leaf thumbprints; stop for provider investigation.'
    }

    return [pscustomobject]@{
        Root = $root
        Subject = $subjects[0]
        LeafSha256Thumbprint = $thumbprints[0]
        Files = @($identities)
    }
}

function Assert-SignedPackageSetMatchesControl {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SignedPackagesRoot,
        [Parameter(Mandatory)][string]$ControlPath,
        [Parameter(Mandatory)][string]$ExpectedSubject,
        [Parameter(Mandatory)][ValidatePattern('^[0-9A-F]{64}$')][string]$ExpectedLeafSha256Thumbprint,
        [Parameter(Mandatory)][string]$ExpectedVersion
    )

    $root = [IO.Path]::GetFullPath($SignedPackagesRoot)
    $control = Get-ControlDocument -ControlPath $ControlPath
    if ([int]$control.schemaVersion -ne 1 -or [string]$control.kind -cne 'production-packages') {
        throw 'Package signing control file has an unsupported schema or kind.'
    }
    Assert-ReleaseControlVersionMetadata -Control $control -ExpectedVersion $ExpectedVersion
    $expectedNames = @(
        "CodexUsageMonitor-$($control.version)-x64.msix",
        "CodexUsageMonitor-$($control.version)-arm64.msix",
        "CodexUsageMonitor-$($control.version).msixbundle"
    )
    $actualFiles = @(Get-SafeTreeFiles -Root $root)
    $actualNames = @($actualFiles | ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\','/') })
    if (@(Compare-Object -ReferenceObject $expectedNames -DifferenceObject $actualNames -CaseSensitive).Count -gt 0) {
        throw 'Signed package return must contain exactly the reviewed x64, Arm64, and bundle names with no extra files.'
    }
    $controlledNames = @($control.files | ForEach-Object { [string]$_.path })
    if (@(Compare-Object -ReferenceObject $expectedNames -DifferenceObject $controlledNames -CaseSensitive).Count -gt 0) {
        throw 'Package signing control does not describe exactly the expected package set.'
    }

    $identities = foreach ($name in $expectedNames) {
        $packageControl = @($control.files | Where-Object { [string]$_.path -ceq $name })
        if ($packageControl.Count -ne 1 -or $null -eq $packageControl[0].payload) {
            throw "Package signing control payload inventory is missing or ambiguous: $name"
        }
        Assert-PackageArchiveMatchesControl -SignedPackagePath (Join-Path $root $name) -PayloadControl $packageControl[0].payload
        $identity = Get-VerifiedAuthenticodeIdentity -Path (Join-Path $root $name) -ExpectedRoot $root -ExpectedSubject $ExpectedSubject
        if ($identity.LeafSha256Thumbprint -cne $ExpectedLeafSha256Thumbprint) {
            throw "Signing provider changed its certificate between release requests: $name"
        }
        $identity
    }
    $subjects = @($identities.Subject | Sort-Object -Unique -CaseSensitive)
    $thumbprints = @($identities.LeafSha256Thumbprint | Sort-Object -Unique -CaseSensitive)
    if ($subjects.Count -ne 1 -or $thumbprints.Count -ne 1) {
        throw 'Signed package return contains multiple signer subjects or SHA-256 leaf thumbprints.'
    }

    return [pscustomobject]@{
        Root = $root
        Subject = $subjects[0]
        LeafSha256Thumbprint = $thumbprints[0]
        Files = @($identities)
    }
}

Export-ModuleMember -Function Get-ReleaseWindowsSdkVersion, Find-WindowsSdkTool, Get-VerifiedAuthenticodeIdentity, Assert-SignedTreeMatchesControl, Assert-SignedPackageSetMatchesControl
