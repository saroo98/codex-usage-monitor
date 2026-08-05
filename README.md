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

The application source and tests are in [`app/`](app/). The portable release archive and SHA-256 checksum are published through the public GitHub Actions release workflow.

## Requirements

- Windows 10 or Windows 11
- Python 3.10 or newer
- Current Codex CLI
- A ChatGPT-backed Codex account signed in through Codex CLI

## Install

1. Download the ZIP from [GitHub Releases](https://github.com/saroo98/codex-usage-monitor/releases).
2. Verify the SHA-256 value in `SHA256SUMS.txt`.
3. Extract the archive.
4. Open `CodexUsageNotifier` and run `INSTALL.cmd`.

See [`app/README.md`](app/README.md) for complete installation, operation, diagnostics, and uninstall instructions.

## Tests

```powershell
$env:PYTHONPATH = "app"
python -m compileall -q app tools
python -m unittest discover -s app/tests -v
```

The public CI workflow runs the test suite on Windows with the minimum supported Python version and the current stable Python line. It also verifies deterministic packaging.

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
