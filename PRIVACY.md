# Privacy policy

Last updated: August 8, 2026

Codex Usage Monitor is designed as a local Windows utility.

## Local data

The application may store the following on the user's computer:

- Codex usage percentages, reset times, and minimal account metadata needed to keep profiles separate
- Application settings, confirmed monitor state, pending notification state, and widget placement
- Local usage history, bounded diagnostic logs, and update transaction state
- Optional email configuration and encrypted credential references

Codex authentication remains managed by the installed Codex CLI. Optional OAuth tokens, SMTP app passwords, and Proton Mail Bridge credentials are protected using Windows security facilities and are not written to ordinary settings, logs, diagnostics, crash reports, or support bundles. Email addresses and notification contents are also omitted from diagnostics and support bundles.

## Data the project does not collect

The application contains no project-operated telemetry, analytics, advertising, or behavioral tracking. It is not designed to collect prompts, conversations, repository contents, browser cookies, ChatGPT passwords, or Codex authentication tokens.

## Network activity

The application communicates with the locally installed Codex App Server. Codex itself may contact OpenAI as required for authentication and usage information. Optional email delivery contacts only the provider configured by the user and sends from that account back to the same account. It does not route credentials, account information, or notification contents through a project-maintainer service. Update checks occur only when enabled and a trusted release feed is configured.

Gmail and Microsoft connections request send-only provider permissions plus the minimum identity permissions needed to determine the signed-in address. The application does not read the mailbox. See [EMAIL_SECURITY.md](EMAIL_SECURITY.md) for exact scopes and auditable source files.

The public website contains no analytics, tracking scripts, remote fonts, or cookies.

## Retention and deletion

History retention is configurable. Uninstall the application and remove its local data folder to delete application data after all monitor and updater processes have stopped.

## Support

Never include credentials, OAuth codes, account identifiers, private paths, or unredacted logs in public issues. Use [SECURITY.md](SECURITY.md) for sensitive reports.
