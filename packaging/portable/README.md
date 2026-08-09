# Portable distribution

Portable packages are produced by `eng/package-portable.ps1` from clean .NET publish outputs.

Each archive contains the primary `CodexUsageMonitor.exe`, the transactional updater helper, required runtime files, the MIT license, third-party notices, public README, `INSTALL.txt`, `UNINSTALL.txt`, a machine-readable `BUILD-INFO.json`, and an empty `portable.mode` marker. The marker makes the package fully portable: settings, history, logs, and update state are stored in the extracted folder's `data` directory instead of the user's local application-data directory.

Extract the ZIP to a writable folder and run `CodexUsageMonitor.exe`. Do not run it from inside the ZIP. No administrator rights are required. The self-contained x64 ZIP is the recommended portable choice because it includes the .NET runtime. The framework-dependent ZIP requires the matching .NET 10 Desktop Runtime.

To remove a portable installation, exit the app from the notification-area menu and delete the extracted folder. See `INSTALL.txt` and `UNINSTALL.txt` for the short user instructions. A signed MSIX installation has a separate uninstall path in Windows Settings.

The packaging tool sorts entries, fixes timestamps, rejects duplicate or traversal paths, reopens every member, and writes SHA-256 checksums. Update payload ZIPs intentionally omit `portable.mode`; the updater preserves the existing marker and data directory during a transactional update.
