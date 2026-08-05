namespace CodexUsageMonitor.Updater.Install;

public static class UpdatePublisherPins
{
    public const int MaximumCount = 8;

    public static IReadOnlyList<string> Normalize(
        IEnumerable<string> thumbprints,
        bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(thumbprints);
        var normalized = thumbprints
            .Select(NormalizeOne)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        ValidateCanonical(normalized, allowEmpty);
        return normalized;
    }

    public static void ValidateCanonical(
        IReadOnlyList<string>? thumbprints,
        bool allowEmpty = false)
    {
        if (thumbprints is null ||
            thumbprints.Count > MaximumCount ||
            (!allowEmpty && thumbprints.Count == 0))
        {
            throw new InvalidDataException("The update publisher pin set is invalid.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? previous = null;
        foreach (var thumbprint in thumbprints)
        {
            if (thumbprint is null ||
                thumbprint.Length is not (40 or 64) ||
                !thumbprint.All(char.IsAsciiHexDigit) ||
                !string.Equals(thumbprint, thumbprint.ToUpperInvariant(), StringComparison.Ordinal) ||
                !seen.Add(thumbprint) ||
                (previous is not null && string.CompareOrdinal(previous, thumbprint) >= 0))
            {
                throw new InvalidDataException("The update publisher pin set is invalid.");
            }

            previous = thumbprint;
        }
    }

    public static IReadOnlySet<string> ToSet(IReadOnlyList<string> thumbprints)
    {
        ValidateCanonical(thumbprints);
        return thumbprints.ToHashSet(StringComparer.Ordinal);
    }

    private static string NormalizeOne(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            throw new InvalidDataException("The update publisher pin set is invalid.");
        }

        var characters = new char[thumbprint.Length];
        var count = 0;
        foreach (var character in thumbprint)
        {
            if (char.IsAsciiHexDigit(character))
            {
                characters[count++] = char.ToUpperInvariant(character);
            }
            else if (!(char.IsWhiteSpace(character) || character is ':' or '-'))
            {
                throw new InvalidDataException("The update publisher pin set is invalid.");
            }
        }

        if (count is not (40 or 64))
        {
            throw new InvalidDataException("The update publisher pin set is invalid.");
        }

        return new string(characters, 0, count);
    }
}
