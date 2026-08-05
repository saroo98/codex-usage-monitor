# Release artifacts

Official portable Windows packages are generated from the public source by [the release workflow](../.github/workflows/release.yml) and published on the repository's [GitHub Releases page](https://github.com/saroo98/codex-usage-monitor/releases).

Current verified artifacts:

- Primary: `Usage-Monitor-for-Codex-5.0.0-Windows.zip`
- Backup: `Usage-Monitor-for-Codex-5.0.0-Windows-BACKUP.zip`
- Size: `66,363 bytes` each
- SHA-256: `7152958049cce22e70380810cd7d5e8fd525577e73e50a958c45f8fc7f07ae00`

Both ZIP files are byte-for-byte identical. The release workflow downloads both published assets again, reopens them, performs ZIP integrity tests, verifies their SHA-256 values, and records the result in [`RELEASED.md`](RELEASED.md).

The archives contain the public Python and PowerShell-based Windows application, its installation utilities, and its automated tests. The binaries are attached to the GitHub Release rather than committed to the source tree.
