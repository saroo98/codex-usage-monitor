# Release integrity

Codex Usage Monitor publishes portable Windows ZIP files from the official GitHub repository. The Windows executables are not Authenticode-signed. Windows will show an unverified or unknown publisher because these files are not Authenticode-signed.

## What each check proves

- **Build repeatability:** the release pipeline requires deterministic portable ZIPs. It builds the four portable variants twice and requires their bytes to match, which makes unexpected build drift detectable.
- **SHA-256 integrity:** `SHA256SUMS.txt` binds each public asset name to its exact bytes. A matching hash detects download corruption or substitution after the checksum was created.
- **GitHub build provenance:** GitHub artifact attestations bind release assets to the tagged workflow run in `saroo98/codex-usage-monitor`. GitHub attestations prove build provenance; they do not make an unsigned Windows publisher trusted.
- **Dependency inventory:** `bom.json` is a CycloneDX software bill of materials for inspecting packaged dependencies. It is an inventory, not a malware guarantee.
- **Automatic update authenticity:** the project-owned Ed25519 key signs `update-manifest.json`. Public unsigned builds contain the matching public trust anchor and verify the manifest and every staged file hash before an update is installed. This is separate from Windows publisher trust.

None of these controls gives an unsigned executable an Authenticode identity or removes Windows security warnings. Do not disable Windows security controls.

## Initial download trust boundary

The initial download trust boundary is the official GitHub repository and its release page: `https://github.com/saroo98/codex-usage-monitor/releases`. Confirm the repository owner, release tag, asset name, SHA-256 value, and GitHub artifact attestation before you run a download. Extract the complete folder before starting `CodexUsageMonitor.exe`.

## Incident response

If the GitHub account, workflow, tag, update key, attestation, or release artifact might be compromised, stop publication and automatic update rollout. Preserve the tag, workflow logs, hashes, attestations, and public state as evidence. Keep `v5.0.0` available while the incident is investigated. Delete or revoke a compromised draft or attestation only with explicit authorization. Do not silently replace published bytes or move a release tag. A compromised Ed25519 update key requires a reviewed key-rotation and client-migration plan.
