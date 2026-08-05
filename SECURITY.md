# Security Policy

## Supported releases

Security fixes are provided for the latest public release. Users should update before reporting an issue already fixed in a newer version.

## Reporting a vulnerability

Do not publish vulnerability details, credentials, tokens, logs containing personal data, or proof-of-concept exploits in a public issue.

Use GitHub Private Vulnerability Reporting from the repository's **Security** tab when available. If that option is unavailable, open a minimal public issue requesting a private reporting channel without including technical details.

Include only information needed to reproduce and assess the issue:

- Affected version and architecture
- Windows version
- Impact and attack prerequisites
- Reproduction steps or a minimal proof of concept
- Suggested mitigation, when known

## Response targets

The maintainer will aim to acknowledge a valid report within seven days. Resolution time depends on severity, reproducibility, and upstream dependencies.

## Scope

Relevant reports include credential exposure, unsafe update behavior, signature or release-provenance failures, account-data mixing, arbitrary code execution, privilege escalation, and sensitive information written to logs or support bundles.

Reports that require intentionally disabling Windows security controls or modifying an official release after download may be closed as out of scope unless they reveal a broader weakness.
