using System.Security.Cryptography;

namespace CodexUsageMonitor.Updater.Install;

public static class UpdateFileIntegrity
{
    public const int Sha256HexLength = 64;

    public static bool IsSha256(string? value) =>
        value is { Length: Sha256HexLength } && value.All(char.IsAsciiHexDigit);

    public static bool FixedTimeEquals(string expectedSha256, string actualSha256)
    {
        if (!IsSha256(expectedSha256) || !IsSha256(actualSha256))
        {
            return false;
        }

        var expected = Convert.FromHexString(expectedSha256);
        var actual = Convert.FromHexString(actualSha256);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = UpdatePathLayout.NormalizePath(path);
        UpdatePathSecurity.EnsureRegularFile(fullPath, "The update file could not be inspected safely.");
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    public static async Task VerifySha256Async(
        string path,
        string expectedSha256,
        string safeFailureMessage,
        CancellationToken cancellationToken)
    {
        if (!IsSha256(expectedSha256))
        {
            throw new InvalidDataException("The expected update file hash is invalid.");
        }

        var actual = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        if (!FixedTimeEquals(expectedSha256, actual))
        {
            throw new System.Security.Cryptography.CryptographicException(safeFailureMessage);
        }
    }
}
