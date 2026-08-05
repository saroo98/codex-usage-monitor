using System.Security.Cryptography;
using CodexUsageMonitor.Updater.Manifest;

namespace CodexUsageMonitor.Updater.Network;

public sealed record DownloadedAsset(string FilePath, long SizeBytes, string Sha256);

public sealed class UpdateAssetDownloader
{
    private readonly HttpClient _httpClient;

    public UpdateAssetDownloader(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<DownloadedAsset> DownloadAsync(
        UpdateAsset asset,
        string destinationDirectory,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var source = new Uri(asset.Url, UriKind.Absolute);
        ValidateHttps(source);
        Directory.CreateDirectory(destinationDirectory);
        var finalPath = Path.Combine(destinationDirectory, asset.FileName);
        var temporaryPath = finalPath + $".{Guid.NewGuid():N}.download";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            request.Headers.UserAgent.ParseAdd("CodexUsageMonitor/1.0");
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            ValidateHttps(response.RequestMessage?.RequestUri ?? source);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != asset.SizeBytes)
            {
                throw new InvalidDataException("Update asset Content-Length does not match the signed manifest.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > asset.SizeBytes)
                {
                    throw new InvalidDataException("Update asset exceeded its signed size.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report(Math.Clamp(total / (double)asset.SizeBytes, 0, 1));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (total != asset.SizeBytes)
            {
                throw new InvalidDataException("Update asset size does not match the signed manifest.");
            }

            var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(digest),
                    System.Text.Encoding.ASCII.GetBytes(asset.Sha256.ToLowerInvariant())))
            {
                throw new CryptographicException("Update asset digest does not match the signed manifest.");
            }

            File.Move(temporaryPath, finalPath, overwrite: true);
            progress?.Report(1);
            return new DownloadedAsset(finalPath, total, digest);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ValidateHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("Update asset endpoints must use HTTPS.");
        }
    }
}
