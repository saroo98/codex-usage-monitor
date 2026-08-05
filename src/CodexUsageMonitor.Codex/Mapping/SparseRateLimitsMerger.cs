using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexUsageMonitor.Codex.Mapping;

public sealed class SparseRateLimitsMerger
{
    private JsonNode? _current;

    public JsonElement Merge(JsonElement update)
    {
        if (update.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidDataException("Rate-limit updates must be JSON objects.");
        }

        var updateNode = JsonNode.Parse(update.GetRawText())
            ?? throw new InvalidDataException("Rate-limit update was empty.");
        _current = _current is null ? updateNode.DeepClone() : MergeNodes(_current, updateNode);
        return JsonSerializer.SerializeToElement(_current);
    }

    public void Reset(JsonElement fullSnapshot)
    {
        if (fullSnapshot.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidDataException("Rate-limit snapshots must be JSON objects.");
        }

        _current = JsonNode.Parse(fullSnapshot.GetRawText());
    }

    public void Clear() => _current = null;

    private static JsonNode MergeNodes(JsonNode current, JsonNode update)
    {
        if (current is JsonObject currentObject && update is JsonObject updateObject)
        {
            foreach (var property in updateObject)
            {
                if (property.Value is null)
                {
                    // Sparse protocol notifications have historically used null for unknown values.
                    // Preserve the last confirmed field rather than converting unknown to zero or absence.
                    continue;
                }

                if (currentObject[property.Key] is { } existing)
                {
                    currentObject[property.Key] = MergeNodes(existing, property.Value);
                }
                else
                {
                    currentObject[property.Key] = property.Value.DeepClone();
                }
            }

            return currentObject;
        }

        return update.DeepClone();
    }
}
