namespace CodexUsageMonitor.Core.Usage;

public static class LimitLabelResolver
{
    public static string Resolve(LimitKind kind, string? model, long? windowSeconds, string? serverLabel)
    {
        if (!string.IsNullOrWhiteSpace(serverLabel))
        {
            return serverLabel.Trim().Length <= 96 ? serverLabel.Trim() : serverLabel.Trim()[..96];
        }

        return kind switch
        {
            LimitKind.FiveHour => "5 hour",
            LimitKind.Weekly => "Weekly",
            LimitKind.ModelSpecific when !string.IsNullOrWhiteSpace(model) => model.Trim(),
            LimitKind.Credits => "Credits",
            _ when windowSeconds is > 0 => FormatWindow(TimeSpan.FromSeconds(windowSeconds.Value)),
            _ => "Usage limit",
        };
    }

    private static string FormatWindow(TimeSpan window)
    {
        if (window.TotalDays >= 1 && window.TotalDays % 1 == 0)
        {
            return window.TotalDays == 1 ? "1 day" : $"{window.TotalDays:0} days";
        }

        if (window.TotalHours >= 1)
        {
            return window.TotalHours == 1 ? "1 hour" : $"{window.TotalHours:0.#} hours";
        }

        return window.TotalMinutes == 1 ? "1 minute" : $"{Math.Max(1, window.TotalMinutes):0} minutes";
    }
}
