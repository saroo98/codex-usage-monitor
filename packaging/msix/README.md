# MSIX packaging

`eng/package-msix.ps1` creates architecture-specific MSIX packages and an MSIX bundle from self-contained publish outputs. The script validates semantic and package versions, emits manifests from public templates, verifies non-empty outputs, and optionally signs and verifies each package with `signtool.exe`.

Unsigned or development-signed packages are suitable only for packaging validation. Public packages must be signed by the approved SignPath Foundation or other trusted release identity, and the manifest publisher must match that signing identity exactly.

`eng/generate-appinstaller.ps1` creates an App Installer feed descriptor only after an HTTPS release base URI and exact publisher identity are supplied.

For users, the recommended asset is the signed `CodexUsageMonitor-6.0.0.msixbundle` (or the signed x64 MSIX on x64-only systems). Open the package and follow the Windows installation prompt. To uninstall, open Windows Settings > Apps > Installed apps, select Codex Usage Monitor, and choose Uninstall. Public release pages must label unsigned or development-signed packages as preview artifacts and must not present them as the stable download.
