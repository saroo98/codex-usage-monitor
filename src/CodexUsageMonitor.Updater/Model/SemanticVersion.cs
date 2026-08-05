using System.Globalization;

namespace CodexUsageMonitor.Updater.Model;

public readonly record struct SemanticVersion : IComparable<SemanticVersion>
{
    public SemanticVersion(int major, int minor, int patch, string? preRelease = null, string? build = null)
    {
        if (major < 0 || minor < 0 || patch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(major));
        }

        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = NormalizeIdentifiers(preRelease, allowLeadingZeroNumeric: false);
        Build = NormalizeIdentifiers(build, allowLeadingZeroNumeric: true);
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? PreRelease { get; }
    public string? Build { get; }

    public static SemanticVersion Parse(string value) =>
        TryParse(value, out var parsed) ? parsed : throw new FormatException("Semantic version is invalid.");

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value[0] is 'v' or 'V')
        {
            return false;
        }

        var buildSplit = value.Split('+', 2);
        var coreAndPre = buildSplit[0].Split('-', 2);
        var core = coreAndPre[0].Split('.');
        if (core.Length != 3 ||
            !TryParseCore(core[0], out var major) ||
            !TryParseCore(core[1], out var minor) ||
            !TryParseCore(core[2], out var patch))
        {
            return false;
        }

        var pre = coreAndPre.Length == 2 ? coreAndPre[1] : null;
        var build = buildSplit.Length == 2 ? buildSplit[1] : null;
        try
        {
            version = new SemanticVersion(major, minor, patch, pre, build);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public int CompareTo(SemanticVersion other)
    {
        var comparison = Major.CompareTo(other.Major);
        if (comparison != 0) return comparison;
        comparison = Minor.CompareTo(other.Minor);
        if (comparison != 0) return comparison;
        comparison = Patch.CompareTo(other.Patch);
        if (comparison != 0) return comparison;
        if (PreRelease is null) return other.PreRelease is null ? 0 : 1;
        if (other.PreRelease is null) return -1;
        var left = PreRelease.Split('.');
        var right = other.PreRelease.Split('.');
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            comparison = CompareIdentifier(left[index], right[index]);
            if (comparison != 0) return comparison;
        }

        return left.Length.CompareTo(right.Length);
    }

    public override string ToString()
    {
        var value = $"{Major}.{Minor}.{Patch}";
        if (PreRelease is not null) value += $"-{PreRelease}";
        if (Build is not null) value += $"+{Build}";
        return value;
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    private static bool TryParseCore(string value, out int number)
    {
        number = 0;
        return value.Length is > 0 and <= 10 &&
            (value.Length == 1 || value[0] != '0') &&
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 0;
    }

    private static string? NormalizeIdentifiers(string? value, bool allowLeadingZeroNumeric)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length == 0 || value.Length > 96)
        {
            throw new ArgumentException("Semantic version identifier is invalid.", nameof(value));
        }

        foreach (var segment in value.Split('.'))
        {
            if (segment.Length == 0 || segment.Any(static character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            {
                throw new ArgumentException("Semantic version identifier is invalid.", nameof(value));
            }

            if (!allowLeadingZeroNumeric && segment.Length > 1 && segment[0] == '0' && segment.All(char.IsAsciiDigit))
            {
                throw new ArgumentException("Numeric prerelease identifiers cannot contain leading zeroes.", nameof(value));
            }
        }

        return value;
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            return CompareNumericStrings(left, right);
        }

        if (leftNumeric) return -1;
        if (rightNumeric) return 1;
        return StringComparer.Ordinal.Compare(left, right);
    }

    private static int CompareNumericStrings(string left, string right)
    {
        left = left.TrimStart('0');
        right = right.TrimStart('0');
        if (left.Length == 0) left = "0";
        if (right.Length == 0) right = "0";
        var length = left.Length.CompareTo(right.Length);
        return length != 0 ? length : StringComparer.Ordinal.Compare(left, right);
    }
}
