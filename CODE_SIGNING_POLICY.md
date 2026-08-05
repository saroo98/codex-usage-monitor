# Code signing policy

Official production artifacts must be built from a reviewed public commit by the repository's protected release workflow.

The release workflow must use declared dependencies and lock files, run the complete build and test gates, generate checksums and a software bill of materials, sign update metadata, sign Windows packages with the approved production identity, and independently reopen and verify every artifact before publication.

The MSIX publisher must match the certificate subject. Portable update payloads must match the signed update manifest and configured trust anchor. Materialized signing inputs must exist only for the duration of the protected workflow and must never be committed or uploaded as build evidence.

Development certificates are not production trust. Unsigned or development-signed artifacts must be labeled clearly and must not be published as a stable production release.

Users should download only from the official GitHub Releases page, compare SHA-256 checksums, and verify the Authenticode signer for signed Windows packages.

If signing credentials, workflows, approval controls, or published artifacts may be compromised, publication stops until the incident is investigated and affected releases are revoked or replaced.
