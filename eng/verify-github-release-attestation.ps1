[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$VerificationJsonPath,
    [Parameter(Mandatory)][string]$ReleaseAssetsJsonPath,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$TagCommit
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-ExactObject {
    param(
        [Parameter(Mandatory)][Text.Json.JsonElement]$Element,
        [Parameter(Mandatory)][string[]]$Properties
    )
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
        throw 'GitHub release attestation JSON has an invalid object type.'
    }
    $allowed = [Collections.Generic.HashSet[string]]::new($Properties, [StringComparer]::Ordinal)
    $values = [Collections.Generic.Dictionary[string,Text.Json.JsonElement]]::new([StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
        if (-not $allowed.Contains($property.Name) -or -not $values.TryAdd($property.Name, $property.Value)) {
            throw 'GitHub release attestation JSON does not match the exact property schema.'
        }
    }
    if ($values.Count -ne $allowed.Count) {
        throw 'GitHub release attestation JSON does not match the exact property schema.'
    }
    return $values
}

function Get-ExactString {
    param(
        [Parameter(Mandatory)][Collections.Generic.Dictionary[string,Text.Json.JsonElement]]$Object,
        [Parameter(Mandatory)][string]$Name,
        [int]$MaximumLength = 1024
    )
    $element = $Object[$Name]
    if ($element.ValueKind -ne [Text.Json.JsonValueKind]::String) {
        throw 'GitHub release attestation JSON has an invalid string type.'
    }
    $value = $element.GetString()
    if ([string]::IsNullOrWhiteSpace($value) -or $value.Length -gt $MaximumLength -or $value -match '[\r\n]') {
        throw 'GitHub release attestation JSON has an invalid string value.'
    }
    return $value
}

function Assert-JsonKind {
    param(
        [Parameter(Mandatory)][Text.Json.JsonElement]$Element,
        [Parameter(Mandatory)][Text.Json.JsonValueKind]$Kind
    )
    if ($Element.ValueKind -ne $Kind) {
        throw 'GitHub release attestation JSON has an invalid structural type.'
    }
}

function Get-ExactPositiveUInt64String {
    param(
        [Parameter(Mandatory)][Collections.Generic.Dictionary[string,Text.Json.JsonElement]]$Object,
        [Parameter(Mandatory)][string]$Name
    )
    $value = Get-ExactString $Object $Name 20
    $parsed = [uint64]0
    if ($value -cnotmatch '^[1-9][0-9]{0,19}$' -or
        -not [uint64]::TryParse(
            $value,
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed)) {
        throw 'GitHub release attestation predicate identifier is invalid.'
    }
    return $value
}

if ($Version -cnotmatch '^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$') {
    throw 'GitHub release attestation version is invalid.'
}
if ($TagCommit -cnotmatch '^[0-9a-f]{40}$') {
    throw 'GitHub release attestation tag commit must be one lowercase SHA-1 object ID.'
}
foreach ($path in @($VerificationJsonPath,$ReleaseAssetsJsonPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw 'GitHub release attestation input file is missing.'
    }
}

$verificationDocument = $null
$assetsDocument = $null
try {
    try {
        $verificationDocument = [Text.Json.JsonDocument]::Parse(
            [IO.File]::ReadAllText($VerificationJsonPath, [Text.UTF8Encoding]::new($false, $true)))
        $assetsDocument = [Text.Json.JsonDocument]::Parse(
            [IO.File]::ReadAllText($ReleaseAssetsJsonPath, [Text.UTF8Encoding]::new($false, $true)))
    }
    catch [Text.Json.JsonException] {
        throw 'GitHub release attestation input is not strict JSON.'
    }

    $root = Get-ExactObject $verificationDocument.RootElement @('attestation','verificationResult')
    $attestation = Get-ExactObject $root['attestation'] @('bundle','bundle_url','initiator')
    Assert-JsonKind $attestation['bundle'] ([Text.Json.JsonValueKind]::Object)
    $bundleUrl = Get-ExactString $attestation 'bundle_url' 2048
    $parsedBundleUrl = $null
    if (-not [Uri]::TryCreate($bundleUrl, [UriKind]::Absolute, [ref]$parsedBundleUrl) -or
        $parsedBundleUrl.Scheme -cne 'https' -or -not [string]::IsNullOrEmpty($parsedBundleUrl.UserInfo) -or
        -not [string]::IsNullOrEmpty($parsedBundleUrl.Fragment)) {
        throw 'GitHub release attestation bundle URL is invalid.'
    }
    if ((Get-ExactString $attestation 'initiator' 32) -cne 'github') {
        throw 'GitHub release attestation initiator is invalid.'
    }

    $verification = Get-ExactObject $root['verificationResult'] @(
        'mediaType','statement','signature','verifiedTimestamps','verifiedIdentity'
    )
    if ((Get-ExactString $verification 'mediaType' 128) -cne
        'application/vnd.dev.sigstore.verificationresult+json;version=0.1') {
        throw 'GitHub release attestation verification media type is invalid.'
    }
    Assert-JsonKind $verification['signature'] ([Text.Json.JsonValueKind]::Object)
    Assert-JsonKind $verification['verifiedTimestamps'] ([Text.Json.JsonValueKind]::Array)
    Assert-JsonKind $verification['verifiedIdentity'] ([Text.Json.JsonValueKind]::Object)

    $statement = Get-ExactObject $verification['statement'] @('_type','subject','predicateType','predicate')
    if ((Get-ExactString $statement '_type' 128) -cne 'https://in-toto.io/Statement/v1') {
        throw 'GitHub release attestation statement type is invalid.'
    }
    if ((Get-ExactString $statement 'predicateType' 160) -cne
        'https://in-toto.io/attestation/release/v0.2') {
        throw 'GitHub release attestation predicate type is invalid.'
    }
    $tag = "v$Version"
    $expectedPurl = "pkg:github/saroo98/codex-usage-monitor@$tag"
    $predicate = Get-ExactObject $statement['predicate'] @(
        'databaseId','ownerId','packageId','purl','repository','repositoryId','tag'
    )
    $null = Get-ExactPositiveUInt64String $predicate 'databaseId'
    $null = Get-ExactPositiveUInt64String $predicate 'ownerId'
    $packageId = Get-ExactPositiveUInt64String $predicate 'packageId'
    $repositoryId = Get-ExactPositiveUInt64String $predicate 'repositoryId'
    if ((Get-ExactString $predicate 'purl' 512) -cne $expectedPurl -or
        (Get-ExactString $predicate 'repository' 256) -cne 'saroo98/codex-usage-monitor' -or
        (Get-ExactString $predicate 'tag' 128) -cne $tag -or
        $packageId -cne $repositoryId) {
        throw 'GitHub release attestation predicate identity is invalid.'
    }

    Assert-JsonKind $assetsDocument.RootElement ([Text.Json.JsonValueKind]::Array)
    $expectedAssets = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($assetElement in $assetsDocument.RootElement.EnumerateArray()) {
        $asset = Get-ExactObject $assetElement @('name','digest')
        $name = Get-ExactString $asset 'name' 255
        $digest = Get-ExactString $asset 'digest' 71
        if ($name -in @('.','..') -or $name -match '[/\\]' -or $digest -cnotmatch '^sha256:[0-9a-f]{64}$' -or
            -not $expectedAssets.TryAdd($name, $digest.Substring(7))) {
            throw 'GitHub release attestation API asset contract is invalid.'
        }
    }
    if ($expectedAssets.Count -eq 0) {
        throw 'GitHub release attestation API asset contract is empty.'
    }

    Assert-JsonKind $statement['subject'] ([Text.Json.JsonValueKind]::Array)
    $actualAssets = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $releaseSubjectCount = 0
    $subjectCount = 0
    foreach ($subjectElement in $statement['subject'].EnumerateArray()) {
        $subjectCount++
        $propertyNames = @($subjectElement.EnumerateObject() | ForEach-Object Name)
        if ($propertyNames -ccontains 'uri') {
            $subject = Get-ExactObject $subjectElement @('uri','digest')
            $digest = Get-ExactObject $subject['digest'] @('sha1')
            if ((Get-ExactString $subject 'uri' 512) -cne $expectedPurl -or
                (Get-ExactString $digest 'sha1' 40) -cne $TagCommit) {
                throw 'GitHub release attestation release subject is invalid.'
            }
            $releaseSubjectCount++
        }
        else {
            $subject = Get-ExactObject $subjectElement @('name','digest')
            $digest = Get-ExactObject $subject['digest'] @('sha256')
            $name = Get-ExactString $subject 'name' 255
            $hash = Get-ExactString $digest 'sha256' 64
            if ($hash -cnotmatch '^[0-9a-f]{64}$' -or -not $actualAssets.TryAdd($name, $hash)) {
                throw 'GitHub release attestation asset subject is invalid.'
            }
        }
    }
    if ($releaseSubjectCount -ne 1 -or $subjectCount -ne ($expectedAssets.Count + 1) -or
        $actualAssets.Count -ne $expectedAssets.Count) {
        throw 'GitHub release attestation subject set is incomplete or duplicated.'
    }
    foreach ($entry in $expectedAssets.GetEnumerator()) {
        $actualDigest = $null
        if (-not $actualAssets.TryGetValue($entry.Key, [ref]$actualDigest) -or $actualDigest -cne $entry.Value) {
            throw 'GitHub release attestation asset subject set or digest is invalid.'
        }
    }

    [pscustomobject]@{
        Version = $Version
        Tag = $tag
        Commit = $TagCommit
        AssetCount = $expectedAssets.Count
        Verified = $true
    }
}
finally {
    if ($null -ne $assetsDocument) { $assetsDocument.Dispose() }
    if ($null -ne $verificationDocument) { $verificationDocument.Dispose() }
}
