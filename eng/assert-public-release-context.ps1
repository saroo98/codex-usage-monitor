[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$ExpectedWorkflowPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepositoryRoot
. "$PSScriptRoot/ProductVersion.ps1"

if ($Version -cne (Get-ProductVersion -RepositoryRoot $RepositoryRoot)) { throw 'Version does not match the centralized product version.' }
if ($ExpectedWorkflowPath -cne '.github/workflows/native-public-release.yml') { throw 'ExpectedWorkflowPath must identify the public release workflow.' }

$requiredContext = @(
    'GITHUB_REPOSITORY','GITHUB_REF','GITHUB_REF_TYPE','GITHUB_REF_NAME','GITHUB_RUN_ATTEMPT',
    'GITHUB_WORKFLOW_REF','GITHUB_WORKFLOW_SHA'
)
$missing = @($requiredContext | Where-Object { [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_)) })
if ($missing.Count -gt 0) { throw "Missing public release context: $($missing -join ', ')" }
if ($env:GITHUB_REPOSITORY -cne 'saroo98/codex-usage-monitor') { throw 'GITHUB_REPOSITORY must identify the reviewed repository.' }

$tag = "v$Version"
if ($env:GITHUB_REF -cne "refs/tags/$tag" -or $env:GITHUB_REF_TYPE -cne 'tag' -or $env:GITHUB_REF_NAME -cne $tag) {
    throw 'The workflow must run from the exact version tag.'
}
if ($env:GITHUB_RUN_ATTEMPT -cne '1') { throw 'Public release workflow reruns are forbidden; dispatch a new run.' }
$expectedWorkflowRef = "$env:GITHUB_REPOSITORY/$ExpectedWorkflowPath@refs/tags/$tag"
if ($env:GITHUB_WORKFLOW_REF -cne $expectedWorkflowRef) { throw 'GITHUB_WORKFLOW_REF does not identify the tagged public workflow.' }

$head = (& git rev-parse HEAD 2>$null).Trim()
$peeledTag = (& git rev-parse "$tag^{}" 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($head) -or $peeledTag -cne $head) {
    throw 'The exact version tag must peel to HEAD.'
}
if ($env:GITHUB_WORKFLOW_SHA -cne $head) { throw 'GITHUB_WORKFLOW_SHA must equal HEAD.' }
$workflowType = (& git cat-file -t "$tag`:$ExpectedWorkflowPath" 2>$null)
if ($LASTEXITCODE -ne 0 -or ([string]$workflowType).Trim() -cne 'blob') { throw 'The tagged tree does not contain the exact public workflow.' }
& git merge-base --is-ancestor HEAD origin/main 2>$null
if ($LASTEXITCODE -ne 0) { throw 'HEAD must be reachable from origin/main.' }
$status = @(& git status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0) { throw 'The public release worktree must be clean.' }

[pscustomobject]@{ Version = $Version; Tag = $tag; Commit = $head }
