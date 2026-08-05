# Usage Monitor for Codex

A lightweight Windows utility that monitors Codex usage limits and reset availability through the locally installed Codex App Server.

[Website](https://saroo98.github.io/codex-usage-monitor/) · [Releases](https://github.com/saroo98/codex-usage-monitor/releases) · [Privacy](PRIVACY.md) · [Support](SUPPORT.md) · [Code signing policy](CODE_SIGNING_POLICY.md)

## Current public release

The current source is the script-based **Codex Usage Notifier 5.0.0 for Windows**. It includes:

- Structured Codex rate-limit reads without browser scraping
- A compact always-available WPF usage widget
- A modern desktop event popup
- Windows toast, tray balloon, sound, and taskbar notification fallbacks
- Two-read freshness checks and confirmation of apparent usage increases
- Atomic redundant state files and pending-alert recovery
- Scheduled Task installation, watchdog checks, diagnostics, and uninstall tools

The application source and tests are in [`app/`](app/).

### Downloads

- [Primary portable ZIP](https://github.com/saroo98/codex-usage-monitor/releases/download/v5.0.0/Usage-Monitor-for-Codex-5.0.0-Windows.zip)
- [Backup portable ZIP](https://github.com/saroo98/codex-usage-monitor/releases/download/v5.0.0/Usage-Monitor-for-Codex-5.0.0-Windows-BACKUP.zip)
- [SHA-256 checksums](https://github.com/saroo98/codex-usage-monitor/releases/download/v5.0.0/SHA256SUMS.txt)

Both archives are **66,363 bytes** and are byte-for-byte identical.

SHA-256:

```text
7152958049cce22e70380810cd7d5e8fd525577e73e50a958c45f8fc7f07ae00
```

The public release workflow downloads the published assets again, reopens both ZIP files, performs integrity tests, and verifies their hashes before recording the release as complete.

## Requirements

- Windows 10 or Windows 11
- Python 3.10 or newer
- Current Codex CLI
- A ChatGPT-backed Codex account signed in through Codex CLI

## Install

1. Download the ZIP from the links above or the [GitHub Releases page](https://github.com/saroo98/codex-usage-monitor/releases/tag/v5.0.0).
2. Verify the SHA-256 value.
3. Extract the archive.
4. Open `CodexUsageNotifier` and run `INSTALL.cmd`.

See [`app/README.md`](app/README.md) for complete installation, operation, diagnostics, and uninstall instructions.

## Tests

```powershell
$env:PYTHONPATH = "app"
python -m compileall -q app tools
python -m unittest discover -s app/tests -v
```

The public CI workflow runs the test suite on Windows with Python 3.10 and Python 3.14. It also verifies deterministic packaging.

## Privacy

Processing is local by default. The application does not collect prompts, conversations, repository contents, browser cookies, passwords, or authentication tokens. See [PRIVACY.md](PRIVACY.md).

## Security

Do not include credentials, tokens, account identifiers, or private logs in public issues. Follow [SECURITY.md](SECURITY.md) for vulnerability reports.

## Code signing

The release process and SignPath-oriented approval rules are documented in [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md). Unsigned artifacts must remain clearly identified until production signing is enabled.

## License

MIT. See [LICENSE](LICENSE).

## Disclaimer

This is an independent open-source project. It is not affiliated with, endorsed by, or maintained by OpenAI. Codex, ChatGPT, and OpenAI are trademarks of their respective owner.
