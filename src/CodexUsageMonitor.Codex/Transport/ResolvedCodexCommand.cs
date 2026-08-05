namespace CodexUsageMonitor.Codex.Transport;

public sealed record ResolvedCodexCommand(
    string ExecutablePath,
    IReadOnlyList<string> PrefixArguments,
    string DiscoverySource);
