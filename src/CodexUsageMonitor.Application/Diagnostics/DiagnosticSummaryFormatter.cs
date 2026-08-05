using System.Globalization;
using System.Text;
using CodexUsageMonitor.Core.Diagnostics;

namespace CodexUsageMonitor.Application.Diagnostics;

public static class DiagnosticSummaryFormatter
{
    public static string Format(DiagnosticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Codex Usage Monitor {snapshot.ApplicationVersion}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Connection: {snapshot.ConnectionState}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Last confirmed read: {FormatTimestamp(snapshot.LastSuccessfulReadUtc)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Codex: {snapshot.CodexVersion ?? "Unavailable"}");
        foreach (var check in snapshot.Checks)
        {
            builder.Append(check.Passed ? "PASS " : "WARN ")
                .Append(check.Code)
                .Append(": ")
                .AppendLine(check.SafeDetail);
        }

        if (snapshot.Details is not null)
        {
            foreach (var pair in snapshot.Details.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                builder.Append(pair.Key).Append(": ").AppendLine(pair.Value);
            }
        }

        return SafeDiagnosticRedactor.Redact(builder.ToString()).TrimEnd();
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) => timestamp is null
        ? "Never"
        : timestamp.Value.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);
}
