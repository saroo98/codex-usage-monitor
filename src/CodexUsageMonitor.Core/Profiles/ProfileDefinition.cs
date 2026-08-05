namespace CodexUsageMonitor.Core.Profiles;

public sealed record ProfileDefinition
{
    public ProfileDefinition(
        Guid id,
        string name,
        string? codexHome,
        bool enabled = true,
        bool monitorInBackground = false)
    {
        Id = id == Guid.Empty ? throw new ArgumentException("Profile ID cannot be empty.", nameof(id)) : id;
        Name = NormalizeName(name);
        CodexHome = NormalizePath(codexHome);
        Enabled = enabled;
        MonitorInBackground = monitorInBackground;
    }

    public Guid Id { get; }

    public string Name { get; }

    public string? CodexHome { get; }

    public bool Enabled { get; }

    public bool MonitorInBackground { get; }

    public static ProfileDefinition CreateDefault() =>
        new(Guid.Parse("7f8f9f52-5df8-45ae-9977-6629a38a3537"), "Default", null, enabled: true, monitorInBackground: true);

    private static string NormalizeName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length > 80 || normalized.IndexOfAny(['\r', '\n', '\t']) >= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return normalized;
    }

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()));
        return full.Length <= 512 ? full : throw new ArgumentOutOfRangeException(nameof(value));
    }
}
