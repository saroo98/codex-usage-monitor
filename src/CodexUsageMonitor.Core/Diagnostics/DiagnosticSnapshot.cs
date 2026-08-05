namespace CodexUsageMonitor.Core.Diagnostics;

public sealed record DiagnosticCheck(string Code, bool Passed, string SafeDetail);

public sealed record DiagnosticSnapshot(
    DateTimeOffset CapturedAtUtc,
    string ApplicationVersion,
    string OperatingSystem,
    string Architecture,
    string RuntimeVersion,
    bool IsPackaged,
    bool IsPortable,
    string ConnectionState,
    DateTimeOffset? LastSuccessfulReadUtc,
    string? CodexVersion,
    string? SafeLastErrorCode,
    IReadOnlyList<DiagnosticCheck> Checks,
    IReadOnlyDictionary<string, string>? Details = null);
