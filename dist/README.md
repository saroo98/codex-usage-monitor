# Release artifacts

Official portable Windows packages are generated from the public source by [the release workflow](../.github/workflows/release.yml) and published on the repository's [GitHub Releases page](https://github.com/saroo98/codex-usage-monitor/releases).

`SHA256SUMS.txt` records the expected SHA-256 digest for the current deterministic package. The ZIP itself is not committed to the source tree; it is attached to the matching GitHub Release and retained as a workflow artifact.

Current package:

- `Usage-Monitor-for-Codex-5.0.0-Windows.zip`
- SHA-256: `cab4b97b9f9a7386db357684ef2f83472310a64ab26942d83a250c8bcdcde824`

The package contains the public Python and PowerShell-based Windows application, its installation utilities, and its automated tests.
