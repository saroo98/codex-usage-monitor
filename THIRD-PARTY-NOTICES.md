# Third-party notices

Codex Usage Monitor for Windows includes or depends on the following open-source components. The authoritative license files distributed with each component govern their use.

| Component | Purpose | License |
|---|---|---|
| MailKit and MimeKit | SMTP transport, MIME message generation, and OAuth SASL support | MIT |
| Microsoft.Data.Sqlite and SQLitePCLRaw dependencies | Durable local SQLite persistence | MIT / Public Domain or upstream component license |
| Microsoft.Extensions.Hosting, DependencyInjection, and Logging | Application hosting, dependency injection, and structured logging abstractions | MIT |
| Microsoft Windows App SDK | Native Windows application notifications | MIT |
| NSec.Cryptography | Ed25519 release-manifest verification | MIT |
| MSTest | Automated test infrastructure | MIT |
| CycloneDX for .NET | Build-time SBOM generation | Apache-2.0 |

The application also uses Windows operating-system APIs and the separately installed Codex CLI. Those components are not redistributed under this repository's MIT license.

Source and license information for restored NuGet dependencies is recorded in the release SBOM generated from the exact release build.
