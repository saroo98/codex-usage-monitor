# Code signing policy

This policy defines how official release artifacts for Usage Monitor for Codex are built, reviewed, approved, and signed.

## Project roles

- **Author/committer:** [@saroo98](https://github.com/saroo98)
- **Reviewer:** [@saroo98](https://github.com/saroo98)
- **Release approver:** [@saroo98](https://github.com/saroo98)

External pull requests must be reviewed before merging. A release-signing request may be approved only after the release commit, workflow, tests, dependencies, and generated artifacts have been checked.

## Trusted source

The canonical source repository is:

`https://github.com/saroo98/codex-usage-monitor`

Only commits present in this public repository may be used to produce official signed artifacts. Private planning documents, manually substituted binaries, unpublished source files, and local-only patches are not permitted in the release build.

## Build and provenance

Official artifacts must be created by the repository's public GitHub Actions release workflow from a tagged commit. The workflow must:

1. Check out the exact tagged commit.
2. Restore dependencies from declared project files and lock files.
3. Build and test from source on a clean hosted runner.
4. Produce SHA-256 checksums and build provenance or attestations.
5. Submit the workflow-produced artifacts for signing without manual binary replacement.
6. Publish the signed artifacts and checksums on the matching GitHub Release.

## Approval requirements

Before approving a signing request, the release approver verifies:

- The tag points to the intended reviewed commit.
- Required automated tests passed.
- The build used the public workflow and declared dependencies.
- Artifact names, versions, architectures, and hashes are consistent.
- No secrets, private data, planning files, or development-only material are included.
- Security and license checks have no unresolved release-blocking findings.

## Signing service

The project intends to use SignPath Foundation for eligible open-source release signing. Until signing is enabled, any unsigned release must be clearly labeled as unsigned. Development certificates are never used to represent a public production release.

## Verification

Users should download only from the official GitHub Releases page and verify the published checksum. Signed releases should also be checked for a valid Authenticode signature and expected signer.

## Incident handling

If signing credentials, workflows, release artifacts, or approval controls may be compromised, signing and publication must stop. Affected releases must be investigated, revoked or replaced when necessary, and documented through a security advisory.

## Policy changes

Changes to this policy are public repository changes and must be reviewed with the same care as release infrastructure.
