<p align="center">
  <img src="docs/logo.svg" width="112" alt="Codex Usage Monitor logo">
</p>

# Codex Usage Monitor

A lightweight native Windows widget for monitoring Codex usage limits and reset times through the locally installed Codex App Server.

[Website](https://saroo98.github.io/codex-usage-monitor/) · [Releases](https://github.com/saroo98/codex-usage-monitor/releases) · [Privacy](PRIVACY.md) · [Email security](EMAIL_SECURITY.md) · [Support](SUPPORT.md) · [Security](SECURITY.md)

## What it does

- Shows confirmed remaining usage and reset timing in a compact widget.
- Monitors multiple isolated local Codex profiles.
- Preserves the last valid reading when Codex is delayed, offline, or unavailable.
- Supports local history, Windows notifications, quiet hours, and optional self-only email alerts.
- Stores data locally with bounded, redacted diagnostics and no project-operated telemetry.
- Supports portable transactional updates with startup-health rollback.
- Supports light, dark, High Contrast, keyboard navigation, reduced motion, and 100–200% scaling.

## Requirements

- Windows 10 build 19041 or later, or Windows 11
- x64 or Arm64 Windows
- The official Codex CLI installed and signed in

## Download and install

The x64 self-contained portable ZIP is the recommended download for most Windows PCs. It includes the required .NET runtime. Use the [GitHub Releases page](https://github.com/saroo98/codex-usage-monitor/releases) until the verified `v6.0.0` asset has been published.

1. Download `CodexUsageMonitor-6.0.0-win-x64-portable-self-contained.zip` from the official release.
2. Right-click the ZIP and select **Extract All**.
3. Choose a writable folder such as `%LOCALAPPDATA%\Programs\CodexUsageMonitor`.
4. Open the extracted `CodexUsageMonitor` folder.
5. Extract the complete folder before starting CodexUsageMonitor.exe.
6. If Windows warns, verify the official release, `SHA256SUMS.txt`, and GitHub attestation before you decide whether to continue. Do not disable Windows security controls.
7. To uninstall, exit the app from the notification area and delete the extracted folder.

The archive contains `portable.mode`, so settings, history, logs, and update state remain in its `data` folder. Arm64 and framework-dependent ZIPs are secondary options. Framework-dependent packages require the matching .NET 10 Desktop Runtime. Update ZIPs are for the app's updater, not manual installation.

Windows will show an unverified or unknown publisher because these files are not Authenticode-signed. See [Release integrity](RELEASE_INTEGRITY.md) for hashes, deterministic portable builds, CycloneDX inventory, GitHub build provenance, and Ed25519 update authentication.

## Privacy and security

Codex authentication remains owned by the installed Codex CLI. The monitor does not request a ChatGPT password and is designed not to record prompts, conversations, repository contents, browser cookies, or Codex authentication tokens.

Optional email credentials use Windows-protected local storage. Email notifications are sent from the configured account back to that same account through a small, tested recipient boundary. See [EMAIL_SECURITY.md](EMAIL_SECURITY.md), [PRIVACY.md](PRIVACY.md), and [SECURITY.md](SECURITY.md).

## Build from source

The repository pins .NET SDK `10.0.302`.

```powershell
./eng/bootstrap.ps1
./eng/verify.ps1 -Configuration Debug -Architecture x64
```

Complete Release verification for both architectures:

```powershell
./eng/capture-verification-evidence.ps1 -Configuration Release -Architecture All
```

The repository pins SDK and NuGet versions, enables deterministic compiler output, generates `SHA256SUMS.txt` and `bom.json`, and independently reopens release artifacts. Release tooling tests byte-identical portable ZIP output from clean builds. Generated builds, reports, databases, logs, and local evidence remain under ignored paths.

## Repository layout

- `src/CodexUsageMonitor.Core`: platform-independent domain rules
- `src/CodexUsageMonitor.Application`: use cases, ports, and runtime state
- `src/CodexUsageMonitor.App`: WPF shell, views, view models, and composition
- `src/CodexUsageMonitor.*`: adapters for Codex, persistence, notifications, email, migration, updates, and Windows
- `tests`: unit, contract, integration, migration, packaging, performance, and UI tests
- `eng`: build, verification, packaging, privacy-audit, and release tooling
- `docs`: static public website

## License

MIT. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Disclaimer

This is an independent open-source project. It is not affiliated with, endorsed by, or maintained by OpenAI. Codex, ChatGPT, and OpenAI are trademarks of their respective owners.
