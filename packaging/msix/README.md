# MSIX packaging

`eng/package-msix.ps1` creates architecture-specific MSIX packages and an MSIX bundle from self-contained publish outputs. The script validates semantic and package versions, emits manifests from public templates, verifies non-empty outputs, and optionally signs and verifies each package with `signtool.exe`.

Unsigned or development-signed packages are suitable only for packaging validation. Public packages must be signed by the approved SignPath Foundation or other trusted release identity, and the manifest publisher must match that signing identity exactly.

`eng/generate-appinstaller.ps1` creates an App Installer feed descriptor only after an HTTPS release base URI and exact publisher identity are supplied.
