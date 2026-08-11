# MSIX packaging

MSIX generation is a local packaging validation capability only. `eng/package-msix.ps1` creates architecture-specific packages and a bundle so maintainers can validate manifests, package identity, architecture, content, and Windows SDK tooling.

No unsigned MSIX, bundle, or AppInstaller is a public release asset. The public `6.0.0` release publishes portable ZIPs. The x64 self-contained portable ZIP is recommended for most Windows PCs; Arm64 and framework-dependent ZIPs are secondary.

`eng/generate-appinstaller.ps1` remains available for local format and verifier tests. It does not create a public update feed. Local validation outputs must remain under ignored artifact directories and must not be described as trusted Windows packages.
