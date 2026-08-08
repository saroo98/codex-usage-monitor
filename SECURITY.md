# Security policy

## Supported releases

Security fixes are provided for the latest public release. Update before reporting an issue already resolved by a newer release.

## Report a vulnerability

Use GitHub Private Vulnerability Reporting from the repository's **Security** tab. If it is unavailable, open a minimal public issue requesting a private reporting channel without technical details.

Do not publish credentials, tokens, certificates, personal data, unredacted logs, or working exploits in a public issue.

Useful reports include the affected version and architecture, Windows version, impact, prerequisites, minimal reproduction steps, and a suggested mitigation when known.

## Scope

Relevant reports include credential exposure, unsafe update behavior, signature or release-provenance failures, account-data mixing, arbitrary code execution, privilege escalation, path traversal, and sensitive information written to logs or support bundles.

## Email notification security

Email delivery is self-only and uses a destination-free application API. Provider permissions, credential storage, recipient enforcement, message construction, transmission adapters, and exact audit files are documented in [EMAIL_SECURITY.md](EMAIL_SECURITY.md).
