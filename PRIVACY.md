# Privacy Policy

Last updated: August 5, 2026

Usage Monitor for Codex is a local Windows utility maintained by [@saroo98](https://github.com/saroo98).

## Data processed locally

The current application may read and store locally:

- Codex usage-limit percentages and reset times
- The minimum account and plan metadata needed to validate the active Codex session
- Application configuration, confirmed monitor state, pending notification state, widget position, heartbeats, and diagnostic logs

The application is not designed to collect prompts, conversations, repository contents, browser cookies, session cookies, passwords, or authentication tokens.

## Network activity

The utility communicates with the locally installed Codex App Server. Codex itself may contact OpenAI as required for authentication and usage data. The project website and GitHub release pages are contacted only when the user opens them.

## Telemetry

The application contains no project-operated telemetry, analytics, advertising, or behavioral tracking. The project does not sell user data.

## Credentials

Authentication remains managed by Codex CLI. This application does not request or store a ChatGPT password and must not write authentication tokens to its settings, logs, support output, or repository.

## Local retention and deletion

Users can remove local application data by uninstalling the notifier and deleting its local data folder after all notifier processes and Scheduled Tasks have stopped. This does not control data retained independently by Codex, OpenAI, GitHub, or Windows.

## Support

Public issues must not contain personal data, credentials, authentication material, account identifiers, or unredacted logs. For sensitive security matters, follow [SECURITY.md](SECURITY.md).

## Changes

Material policy changes are recorded in this repository's public history.
