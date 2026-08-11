# Releasing Codex Usage Monitor

This runbook covers the public unsigned GitHub release model. It does not require a paid signing service, a Windows certificate, or a separate attestation account. GitHub Actions builds the tagged source, verifies the exact asset matrix, creates GitHub artifact attestations, creates a draft, downloads and verifies the draft, and publishes only after a second explicit confirmation.

Tag creation, tag push, update-secret setup, workflow dispatch, draft deletion, and publication are external writes. Each requires explicit authorization for that action.

## One-time update-key setup

The project-owned Ed25519 key authenticates automatic update manifests. It does not sign Windows executables.

1. Choose an encrypted directory outside every repository and synced folder.
2. Run `./eng/configure-update-signing.ps1` only after authorization to create the GitHub Environment secret.
3. Keep the private backup outside all workspaces. Never print, email, commit, log, or pass the private key on a command line.
4. Commit only the public value written to `packaging/update/update-trust-anchor.txt`.
5. Confirm the GitHub Environment `native-production-release` contains only the secret `UPDATE_PRIVATE_KEY_BASE64` for this release path. OAuth client IDs can remain optional public variables.

The workflow uses the Environment only for the build job. GitHub creates provenance and SBOM attestations through its automatic OIDC token. No additional attestation secret is needed.

## Credential-free local verification

Run from the reviewed worktree:

```powershell
./eng/bootstrap.ps1
./eng/verify.ps1 -Configuration Debug -Architecture x64 -SkipUi
./eng/capture-verification-evidence.ps1 -Configuration Release -Architecture All -SkipUi
./eng/test.ps1 -Suite Packaging -Configuration Debug
python eng/verify-static.py
python eng/audit-publication.py
python eng/verify-site.py
dotnet format CodexUsageMonitor.slnx --verify-no-changes --no-restore
git diff --check
```

Build public-mode artifacts only with a temporary local Ed25519 key and an exact local `v6.0.0` tag. Verify both output roots with `eng/verify-release.ps1 -ReleaseMode PublicUnsigned`. Require all four portable ZIP hashes to match across clean builds. Remove the temporary key and clear `UPDATE_PRIVATE_KEY_BASE64` afterward.

## Authorized tag and draft

The reviewed commit must already be on `main`. The tree must be clean. The version must be `6.0.0`. The tag must not already exist. Under separate authorization, run:

```powershell
git tag v6.0.0
git push origin v6.0.0
gh workflow run native-public-release.yml --ref v6.0.0 -f version=6.0.0 -f publish_confirmed=false
```

The first dispatch must leave a draft. Inspect the downloaded x64 self-contained ZIP. Confirm its exact filename, hash, attestation, extracted layout, startup behavior, notification-area exit, and portable uninstall path. Do not use locally built files for this acceptance check.

## Authorized publication

Enable GitHub immutable releases and configure the `native-production-release` Environment to allow only tags matching `v*` before publication. After the draft and downloaded package pass review, authorize and run:

```powershell
gh workflow run native-public-release.yml --ref v6.0.0 -f version=6.0.0 -f publish_confirmed=true
```

The second run must rebuild the same tagged source, require the existing draft bytes to match, verify all attestations, confirm release immutability, publish as latest stable, then download and verify the public release again.

## Incident response and rollback

- If preflight, packaging, hashes, SBOM, or attestation verification fails, publish nothing.
- If a compromised draft exists, retain evidence and delete the draft only after explicit authorization.
- If an attestation is compromised or invalid, stop publication and revoke or delete it only through the supported GitHub process and with explicit authorization.
- If publication already occurred, do not replace immutable assets or move the tag. Publish a corrected higher version after review.
- If the update key is exposed, stop automatic releases and prepare a versioned key-rotation migration. Do not silently replace the trust anchor trusted by installed clients.
- Preserve public `v5.0.0` as the Legacy release and rollback option.
