# Email notification security

Email notifications are optional and self-only. Codex Usage Monitor constructs messages from a notification body plus the identity of the configured account. Its normal sending API has no recipient, Cc, or Bcc input.

## Providers and permissions

- **Gmail** uses the Gmail API with installed-application OAuth and PKCE. It requests `openid`, `email`, and `https://www.googleapis.com/auth/gmail.send`. It does not request mailbox read, search, modification, deletion, or full `mail.google.com` access.
- **Outlook / Microsoft 365** uses Microsoft Graph with public-client OAuth and PKCE. It requests `openid`, `email`, `offline_access`, delegated `User.Read`, and delegated `Mail.Send`. It does not request `Mail.Read`.
- **Proton Mail** connects only to the local Proton Mail Bridge SMTP endpoint using Bridge-generated email-client credentials. The normal Proton account password must not be entered. TLS or STARTTLS is mandatory.
- **Other email (SMTP) [Advanced]** uses the configured SMTP account with TLS or STARTTLS. Plaintext SMTP is rejected.
- **Off** prevents automatic email delivery and test delivery.

Connecting an OAuth account does not send email or enable notifications. A message is sent only after the user explicitly enables email notifications and a configured condition occurs, or after the user explicitly requests a test message.

## Self-recipient boundary

The providers can technically send to other recipients, so Codex Usage Monitor enforces the self-only rule locally:

1. The authenticated or configured account identity is validated.
2. `ISelfNotificationSender.SendSelfNotificationAsync(SelfNotification, ...)` accepts content but no destination.
3. `SelfOnlyMessageFactory` derives both From and To from that account identity.
4. MIME and Graph message builders add exactly one self recipient and no Cc or Bcc fields.
5. Header-bearing values reject CR, LF, and NUL characters.

Automated tests reject public recipient parameters, alternate recipients, Cc/Bcc construction, header injection, and legacy arbitrary-recipient message types.

## Local credentials and privacy

OAuth refresh tokens, SMTP app passwords, and Proton Bridge credentials are stored through Windows Credential Manager. Outbox message payloads use Windows DPAPI. Secrets are not written to settings JSON, logs, diagnostics, or support bundles. Email addresses, notification contents, tokens, and credentials are not sent to the project maintainer.

Disconnect removes local OAuth material and attempts Google token revocation. Migrated broad OAuth authorizations are disabled, require reconnection, and their obsolete local credential references are cleaned during startup. Users may also revoke provider access from their Google or Microsoft account security pages.

## Audit map

Reviewers can inspect the complete security path in these files:

- Authenticated identity: `src/CodexUsageMonitor.Email/OAuth/ProviderEmailAccountIdentityResolver.cs`
- OAuth permissions and PKCE: `src/CodexUsageMonitor.Email/OAuth/GooglePkceAuthorizationFlow.cs` and `MicrosoftPkceAuthorizationFlow.cs`
- Recipient enforcement: `src/CodexUsageMonitor.Email/Security/SelfOnlyMessageFactory.cs`
- Message construction: `src/CodexUsageMonitor.Email/Transport/SelfOnlyMimeMessageBuilder.cs`
- Protected credential storage: `src/CodexUsageMonitor.Windows/Security/WindowsCredentialSecretStore.cs`, `src/CodexUsageMonitor.Email/OAuth/OAuthTokenStore.cs`, and `src/CodexUsageMonitor.Email/Outbox/EmailOutboxPayloadCodec.cs`
- Provider transmission: `GmailApiSelfNotificationTransport.cs`, `MicrosoftGraphSelfNotificationTransport.cs`, and `SmtpEmailTransport.cs` under `src/CodexUsageMonitor.Email/Transport`
- UI and opt-in orchestration: `src/CodexUsageMonitor.App/Services/OAuthConnectionService.cs`, `EmailCredentialService.cs`, `TransitionEmailDispatcher.cs`, and `src/CodexUsageMonitor.App/Runtime/RuntimeActionService.cs`
- Security tests: `tests/CodexUsageMonitor.UnitTests/SelfNotificationSecurityTests.cs`, `EmailProviderTransportTests.cs`, `OAuthPermissionAndIdentityTests.cs`, and the OAuth/credential tests under `tests/CodexUsageMonitor.UiTests`

OAuth client IDs are public application registration identifiers, not client secrets. Release builds can embed them through the documented MSBuild/package parameters. If a provider registration is absent, that provider remains unavailable rather than asking users for a client ID.
