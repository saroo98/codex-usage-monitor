using System.Text.Json.Serialization;

namespace CodexUsageMonitor.Core.Settings;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Default,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

public static class SettingsJson
{
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<AppSettings> TypeInfo =>
        SettingsJsonContext.Default.AppSettings;

    public static System.Text.Json.JsonSerializerOptions Options => SettingsJsonContext.Default.Options;
}
