# Portable distribution

Portable packages are produced by `eng/package-portable.ps1` from clean .NET publish outputs.

Each archive contains the primary `CodexUsageMonitor.exe`, the transactional updater helper, required runtime files, the MIT license, third-party notices, public README, and a machine-readable `BUILD-INFO.json`. The packaging tool sorts entries, fixes timestamps, rejects duplicate or traversal paths, reopens every member, and writes SHA-256 checksums.

The framework-dependent packages require the matching .NET Desktop Runtime. Self-contained packages include the runtime and do not require administrator rights.
