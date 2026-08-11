Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ReleaseHttpsUri {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$ExpectedRepository
    )
    if ($ExpectedRepository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Expected repository identifier is invalid.' }
    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -cne 'https' -or
        -not $uri.Host.Equals('github.com', [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Fragment) -or
        -not $uri.AbsolutePath.StartsWith("/$ExpectedRepository/", [StringComparison]::Ordinal)) {
        throw 'Release URL does not match the immutable HTTPS repository boundary.'
    }
    return $uri
}

function Assert-ReleasePin {
    param([Parameter(Mandatory)][string]$Pin)
    if ($Pin -cnotmatch '^[0-9A-F]{64}$') {
        throw 'Publisher thumbprint must be one uppercase SHA-256 value.'
    }
}

function ConvertTo-CanonicalCommit {
    param([Parameter(Mandatory)][string]$Value)
    if ($Value -cnotmatch '^(?:[0-9A-Fa-f]{40}|[0-9A-Fa-f]{64})$') {
        throw 'Build metadata commit object ID is invalid.'
    }
    return $Value.ToLowerInvariant()
}

function Get-ExactJsonObject {
    param(
        [Parameter(Mandatory)][Text.Json.JsonElement]$Element,
        [Parameter(Mandatory)][string[]]$Properties,
        [Parameter(Mandatory)][string]$Context
    )
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw "$Context has an invalid JSON type." }
    $allowed = [Collections.Generic.HashSet[string]]::new($Properties, [StringComparer]::Ordinal)
    $values = [Collections.Generic.Dictionary[string,Text.Json.JsonElement]]::new([StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
        if (-not $allowed.Contains($property.Name) -or -not $values.TryAdd($property.Name, $property.Value)) {
            throw "$Context does not match the exact allowed property schema."
        }
    }
    if ($values.Count -ne $allowed.Count) { throw "$Context does not match the exact allowed property schema." }
    return $values
}

function Get-BoundedJsonString {
    param(
        [Parameter(Mandatory)][Collections.Generic.Dictionary[string,Text.Json.JsonElement]]$Object,
        [Parameter(Mandatory)][string]$Name,
        [int]$MaximumLength = 512,
        [switch]$AllowNull
    )
    $element = $Object[$Name]
    if ($AllowNull -and $element.ValueKind -eq [Text.Json.JsonValueKind]::Null) { return $null }
    if ($element.ValueKind -ne [Text.Json.JsonValueKind]::String) { throw 'BUILD-METADATA.json contains a property with an invalid type.' }
    $value = $element.GetString()
    if ([string]::IsNullOrWhiteSpace($value) -or $value.Length -gt $MaximumLength -or $value -match '[\r\n]') {
        throw 'BUILD-METADATA.json contains an invalid bounded string.'
    }
    return $value
}

function Assert-JsonBoolean {
    param(
        [Parameter(Mandatory)][Text.Json.JsonElement]$Element,
        [Parameter(Mandatory)][bool]$Expected
    )
    $requiredKind = if ($Expected) { [Text.Json.JsonValueKind]::True } else { [Text.Json.JsonValueKind]::False }
    if ($Element.ValueKind -ne $requiredKind) { throw 'BUILD-METADATA.json production must be an actual JSON Boolean.' }
}

function Get-JsonStringArray {
    param(
        [Parameter(Mandatory)][Text.Json.JsonElement]$Element,
        [Parameter(Mandatory)][string]$Context
    )
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Array) { throw "$Context has an invalid JSON type." }
    $values = [Collections.Generic.List[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $Element.EnumerateArray()) {
        if ($item.ValueKind -ne [Text.Json.JsonValueKind]::String) { throw "$Context contains a non-string value." }
        $value = $item.GetString()
        if ([string]::IsNullOrWhiteSpace($value) -or $value.Length -gt 64 -or -not $seen.Add($value)) { throw "$Context is invalid." }
        $values.Add($value)
    }
    if ($values.Count -eq 0) { throw "$Context is empty." }
    return $values.ToArray()
}

function Read-ReleaseBuildMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$PublicUnsigned
    )
    try {
        $file = Get-Item -LiteralPath $Path -Force
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $file.Length -le 0 -or $file.Length -gt 64KB) {
            throw 'BUILD-METADATA.json has an invalid file shape or size.'
        }
        $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($file.FullName, [Text.UTF8Encoding]::new($false, $true)))
    }
    catch {
        throw 'BUILD-METADATA.json is not valid bounded JSON.'
    }
    try {
        if ($PublicUnsigned) {
            $top = Get-ExactJsonObject $document.RootElement @(
                'product','version','commit','sdk','configuration','architectures','releaseMode',
                'windowsAuthenticode','attestationProvider','generatedAtUtc') 'Public unsigned BUILD-METADATA.json'
            $product = Get-BoundedJsonString $top 'product' 160
            $version = Get-BoundedJsonString $top 'version' 64
            if ($top['commit'].ValueKind -ne [Text.Json.JsonValueKind]::String) { throw 'Build metadata commit object ID is invalid.' }
            $commit = ConvertTo-CanonicalCommit $top['commit'].GetString()
            $sdk = Get-BoundedJsonString $top 'sdk' 128
            $configuration = Get-BoundedJsonString $top 'configuration' 32
            $architectures = Get-JsonStringArray $top['architectures'] 'Public unsigned metadata architectures'
            $releaseMode = Get-BoundedJsonString $top 'releaseMode' 64
            Assert-JsonBoolean $top['windowsAuthenticode'] $false
            $attestationProvider = Get-BoundedJsonString $top 'attestationProvider' 128
            $generatedAt = Get-BoundedJsonString $top 'generatedAtUtc' 64
            if ($product -cne 'Codex Usage Monitor for Windows' -or $configuration -cne 'Release' -or
                $releaseMode -cne 'public-unsigned' -or $attestationProvider -cne 'GitHub Actions' -or
                $architectures.Count -ne 2 -or $architectures[0] -cne 'arm64' -or $architectures[1] -cne 'x64') {
                throw 'Public unsigned BUILD-METADATA.json fixed release values are invalid.'
            }
            $parsedTime = [DateTimeOffset]::MinValue
            if (-not [DateTimeOffset]::TryParse($generatedAt, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$parsedTime)) {
                throw 'Public unsigned BUILD-METADATA.json generation time is invalid.'
            }
            return [pscustomobject]@{
                product = $product; version = $version; commit = $commit; sdk = $sdk
                configuration = $configuration; architectures = $architectures; production = $false
                releaseMode = $releaseMode; windowsAuthenticode = $false
                attestationProvider = $attestationProvider; generatedAtUtc = $generatedAt
            }
        }
        $top = Get-ExactJsonObject $document.RootElement @(
            'product','version','commit','sdk','configuration','architectures','production','generatedAtUtc') 'Nonproduction BUILD-METADATA.json'
        $product = Get-BoundedJsonString $top 'product' 160
        $version = Get-BoundedJsonString $top 'version' 64
        if ($top['commit'].ValueKind -ne [Text.Json.JsonValueKind]::String) { throw 'Build metadata commit object ID is invalid.' }
        $commit = ConvertTo-CanonicalCommit $top['commit'].GetString()
        $sdk = Get-BoundedJsonString $top 'sdk' 128
        $configuration = Get-BoundedJsonString $top 'configuration' 32
        $architectures = Get-JsonStringArray $top['architectures'] 'Nonproduction metadata architectures'
        Assert-JsonBoolean $top['production'] $false
        $generatedAt = Get-BoundedJsonString $top 'generatedAtUtc' 64
        if ($product -cne 'Codex Usage Monitor for Windows' -or $configuration -notin @('Debug','Release') -or
            @($architectures | Where-Object { $_ -notin @('x64','arm64') }).Count -ne 0) {
            throw 'Nonproduction BUILD-METADATA.json fixed release values are invalid.'
        }
        $parsedTime = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse($generatedAt, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$parsedTime)) {
            throw 'Nonproduction BUILD-METADATA.json generation time is invalid.'
        }
        return [pscustomobject]@{
            product = $product; version = $version; commit = $commit; sdk = $sdk
            configuration = $configuration; architectures = $architectures; production = $false; generatedAtUtc = $generatedAt
        }
    }
    finally { $document.Dispose() }
}

function New-ReleaseVerificationReport {
    param(
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$MetadataCommit,
        [Parameter(Mandatory)][ValidateRange(0,[int]::MaxValue)][int]$ArtifactCount,
        [Parameter(Mandatory)][ValidateRange(0,[int]::MaxValue)][int]$ChecksumCount,
        [Parameter(Mandatory)][ValidateSet('x64','arm64')][string[]]$Architectures
    )
    $commit = ConvertTo-CanonicalCommit $MetadataCommit
    return [ordered]@{
        version = $Version
        commit = $commit
        verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        production = $false
        artifactCount = $ArtifactCount
        checksumCount = $ChecksumCount
        architectures = @($Architectures)
        status = 'passed'
    }
}

function Read-PackageXml {
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][string]$EntryName)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Path))
    try {
        $matches = @($archive.Entries | Where-Object { $_.FullName.Equals($EntryName, [StringComparison]::OrdinalIgnoreCase) })
        if ($matches.Count -ne 1 -or $matches[0].FullName -cne $EntryName) { throw 'Package contains a missing or case-ambiguous manifest entry.' }
        if ($matches[0].Length -le 0 -or $matches[0].Length -gt 1MB) { throw 'Package manifest has an invalid size.' }
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $stream = $matches[0].Open()
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        try {
            $document = [Xml.XmlDocument]::new()
            $document.XmlResolver = $null
            $document.Load($reader)
            return $document
        }
        finally { $reader.Dispose(); $stream.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Assert-MsixReleaseIdentity {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$Architecture,
        [Parameter(Mandatory)][string]$ExpectedPublisher
    )
    $document = Read-PackageXml -Path $Path -EntryName 'AppxManifest.xml'
    $identity = $document.Package.Identity
    if ($null -eq $identity -or [string]$identity.Name -cne 'saroo98.CodexUsageMonitor' -or
        [string]$identity.Publisher -cne $ExpectedPublisher -or [string]$identity.Version -cne "$Version.0" -or
        [string]$identity.ProcessorArchitecture -cne $Architecture) {
        throw 'MSIX manifest identity, publisher, version, or architecture is invalid.'
    }
}

function Assert-MsixBundleIdentity {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$ExpectedPublisher
    )
    $document = Read-PackageXml -Path $Path -EntryName 'AppxMetadata/AppxBundleManifest.xml'
    $identity = $document.Bundle.Identity
    if ($null -eq $identity -or [string]$identity.Name -cne 'saroo98.CodexUsageMonitor' -or
        [string]$identity.Publisher -cne $ExpectedPublisher -or [string]$identity.Version -cne "$Version.0") {
        throw 'MSIX bundle identity, publisher, or version is invalid.'
    }
    $packages = @($document.Bundle.Packages.Package)
    if ($packages.Count -ne 2) { throw 'MSIX bundle must contain exactly two application packages.' }
    $actual = @($packages | ForEach-Object { [string]$_.Architecture } | Sort-Object)
    if (@(Compare-Object @('arm64','x64') $actual -CaseSensitive).Count -ne 0) { throw 'MSIX bundle architecture matrix is invalid.' }
    foreach ($package in $packages) {
        $architecture = [string]$package.Architecture
        if ([string]$package.Type -cne 'application' -or [string]$package.Version -cne "$Version.0" -or
            [string]$package.FileName -cne "CodexUsageMonitor-$Version-$architecture.msix") {
            throw 'MSIX bundle package metadata is invalid.'
        }
    }
}

function Test-WindowsUnsafeZipName {
    param([Parameter(Mandatory)][string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name.EndsWith('/', [StringComparison]::Ordinal) -or
        $Name.Contains('\') -or [IO.Path]::IsPathRooted($Name)) { return $true }
    $segments = $Name.Split('/')
    foreach ($segment in $segments) {
        if ([string]::IsNullOrEmpty($segment) -or $segment -in @('.','..') -or $segment.EndsWith(' ') -or $segment.EndsWith('.') -or
            $segment.IndexOfAny([char[]]':*?"<>|') -ge 0 -or @($segment.ToCharArray() | Where-Object { [int]$_ -lt 32 }).Count -gt 0) { return $true }
        $base = $segment.Split('.')[0].ToUpperInvariant()
        if ($base -in @('CON','PRN','AUX','NUL') -or $base -match '^(COM|LPT)[1-9]$') { return $true }
    }
    return $false
}

function Assert-ExtractedUpdateManifest {
    param(
        [Parameter(Mandatory)][string]$ExtractionRoot,
        [Parameter(Mandatory)][ValidateSet('Update','Portable')][string]$ArchiveKind,
        [Parameter(Mandatory)][string]$Version
    )
    $payloadRoot = if ($ArchiveKind -eq 'Portable') { Join-Path $ExtractionRoot 'CodexUsageMonitor' } else { $ExtractionRoot }
    $manifestPath = Join-Path $payloadRoot 'update-files.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Archive update-files.json is missing.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if (@($manifest.PSObject.Properties.Name).Count -ne 3 -or
        @($manifest.PSObject.Properties.Name | Where-Object { $_ -notin @('schemaVersion','version','files') }).Count -ne 0 -or
        [int]$manifest.schemaVersion -ne 1 -or [string]$manifest.version -cne $Version) {
        throw 'Archive update-files.json metadata is invalid.'
    }
    $actual = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($file in Get-ChildItem -LiteralPath $payloadRoot -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($payloadRoot, $file.FullName).Replace('\','/')
        if ($relative -eq 'update-files.json' -or ($ArchiveKind -eq 'Portable' -and $relative -in @('INSTALL.txt','UNINSTALL.txt','portable.mode'))) { continue }
        $actual[$relative] = $file
    }
    $previous = $null
    $declared = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($manifest.files)) {
        if (@($entry.PSObject.Properties.Name).Count -ne 3 -or
            @($entry.PSObject.Properties.Name | Where-Object { $_ -notin @('path','sizeBytes','sha256') }).Count -ne 0) {
            throw 'Archive update-files.json entry schema is invalid.'
        }
        $name = [string]$entry.path
        if (Test-WindowsUnsafeZipName $name) { throw 'Archive update-files.json contains an unsafe path.' }
        if ($null -ne $previous -and [StringComparer]::Ordinal.Compare($previous, $name) -ge 0) { throw 'Archive update-files.json is not ordinally sorted.' }
        if (-not $declared.Add($name) -or -not $actual.ContainsKey($name)) { throw 'Archive update-files.json does not exactly cover extracted bytes.' }
        $file = $actual[$name]
        if ([long]$entry.sizeBytes -ne $file.Length -or [string]$entry.sha256 -cne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()) {
            throw 'Archive update-files.json byte metadata does not match the extracted file.'
        }
        $previous = $name
    }
    if ($declared.Count -ne $actual.Count) { throw 'Archive update-files.json omits extracted payload bytes.' }
    foreach ($required in @('CodexUsageMonitor.exe','CodexUsageMonitor.UpdaterHost.exe')) {
        if (-not $declared.Contains($required)) { throw 'Archive update-files.json omits a required executable.' }
    }
}

function Test-ReleaseArchive {
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$TemporaryBase,
        [Parameter(Mandatory)][ValidateSet('Update','Portable')][string]$ArchiveKind,
        [Parameter(Mandatory)][string]$Version
    )
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $base = [IO.Path]::GetFullPath($TemporaryBase).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $base -PathType Container)) { throw 'Verification temporary base is missing.' }
    $extraction = Join-Path $base ('release-verification-' + [Guid]::NewGuid().ToString('N'))
    New-Item -Path $extraction -ItemType Directory | Out-Null
    try {
        $archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath))
        try {
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            $totalBytes = [long]0
            foreach ($entry in $archive.Entries) {
                if ($archive.Entries.Count -gt 4097 -or $entry.Length -lt 0 -or $entry.Length -gt 512MB) { throw 'Release archive exceeds its entry size or count limit.' }
                $totalBytes += $entry.Length
                if ($totalBytes -gt 1GB) { throw 'Release archive exceeds its uncompressed size limit.' }
                $name = $entry.FullName
                if (Test-WindowsUnsafeZipName $name) { throw 'Release archive contains an unsafe or non-canonical path.' }
                if (-not $names.Add($name)) { throw 'Release archive contains a case-insensitive duplicate path.' }
                $unixType = (($entry.ExternalAttributes -shr 16) -band 0xF000)
                if ($unixType -eq 0xA000 -or ($entry.ExternalAttributes -band 0x400) -ne 0) { throw 'Release archive contains a reparse-point entry.' }
                $logical = if ($ArchiveKind -eq 'Portable' -and $name.StartsWith('CodexUsageMonitor/', [StringComparison]::Ordinal)) { $name.Substring(18) } else { $name }
                if ($ArchiveKind -eq 'Portable' -and -not $name.StartsWith('CodexUsageMonitor/', [StringComparison]::Ordinal)) { throw 'Portable archive entry is outside its fixed top-level directory.' }
                if ($logical.Equals('UNSIGNED-RELEASE-CANDIDATE.txt', [StringComparison]::OrdinalIgnoreCase) -or
                    $logical.Equals('portable.mode', [StringComparison]::OrdinalIgnoreCase) -and $ArchiveKind -eq 'Update' -or
                    $logical.Equals('INSTALL.txt', [StringComparison]::OrdinalIgnoreCase) -and $ArchiveKind -eq 'Update' -or
                    $logical.Equals('UNINSTALL.txt', [StringComparison]::OrdinalIgnoreCase) -and $ArchiveKind -eq 'Update' -or
                    $logical.StartsWith('data/', [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Release archive contains reserved local-state or release-marker content.'
                }
                $target = [IO.Path]::GetFullPath((Join-Path $extraction $name.Replace('/', [IO.Path]::DirectorySeparatorChar)))
                $prefix = $extraction.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
                if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release archive entry escapes the extraction root.' }
                New-Item -Path (Split-Path -Parent $target) -ItemType Directory -Force | Out-Null
                $input = $entry.Open()
                $output = [IO.FileStream]::new($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
            }
        }
        finally { $archive.Dispose() }
        foreach ($item in Get-ChildItem -LiteralPath $extraction -Recurse -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Extracted release archive contains a reparse point.' }
        }
        Assert-ExtractedUpdateManifest -ExtractionRoot $extraction -ArchiveKind $ArchiveKind -Version $Version
        [pscustomobject]@{ EntryCount = $names.Count; Kind = $ArchiveKind }
    }
    finally {
        $resolved = [IO.Path]::GetFullPath($extraction).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $allowed = $base + [IO.Path]::DirectorySeparatorChar
        if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Temporary extraction cleanup escaped its intended base.' }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}

Export-ModuleMember -Function Assert-ReleaseHttpsUri,Assert-ReleasePin,ConvertTo-CanonicalCommit,Read-ReleaseBuildMetadata,New-ReleaseVerificationReport,Assert-MsixReleaseIdentity,Assert-MsixBundleIdentity,Assert-ExtractedUpdateManifest,Test-ReleaseArchive
