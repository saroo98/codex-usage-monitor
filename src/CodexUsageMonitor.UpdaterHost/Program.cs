using CodexUsageMonitor.Updater.Install;
using CodexUsageMonitor.Updater.Security;
using CodexUsageMonitor.Updater.Staging;

namespace CodexUsageMonitor.UpdaterHost;

internal static class Program
{
    private const int RequiredArgumentCount = 4;
    private const int MaximumArgumentCharacters = 4096;

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        try
        {
            ValidateArguments(args);

            var nonce = ReadRequiredOption(args, "--nonce");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var runningHostPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The updater host executable path is unavailable.");
            var transaction = new PortableUpdateTransaction();
            if (TryReadOption(args, "--request", out var installRequestPath))
            {
                var request = await UpdateInstallRequest.ReadAsync(installRequestPath, cancellation.Token).ConfigureAwait(false);
                request.Validate(nonce, runningHostPath, installRequestPath, DateTimeOffset.UtcNow);
                await CreateTrustPolicy().VerifyPreparedHostAsync(
                    runningHostPath,
                    request.UpdaterHostSha256,
                    request.PublisherThumbprints,
                    request.TrustMode,
                    cancellation.Token).ConfigureAwait(false);
                await request.VerifyPayloadAsync(cancellation.Token).ConfigureAwait(false);
                var result = await transaction.ExecuteAsync(request, cancellation.Token).ConfigureAwait(false);
                return ExitCode(result);
            }

            if (TryReadOption(args, "--rollback-request", out var rollbackRequestPath))
            {
                var request = await UpdateRollbackRequest.ReadAsync(rollbackRequestPath, cancellation.Token).ConfigureAwait(false);
                request.ValidateEnvelope(nonce, runningHostPath, rollbackRequestPath, DateTimeOffset.UtcNow);
                var journal = await UpdateTransactionJournal.ReadAsync(request.JournalPath, cancellation.Token).ConfigureAwait(false);
                request.ValidateAgainst(journal);
                await CreateTrustPolicy().VerifyPreparedHostAsync(
                    runningHostPath,
                    journal.UpdaterHostSha256,
                    journal.PublisherThumbprints,
                    journal.TrustMode,
                    cancellation.Token).ConfigureAwait(false);
                var result = await transaction.RollBackInterruptedAsync(request, journal, cancellation.Token).ConfigureAwait(false);
                return ExitCode(result);
            }

            throw new ArgumentException("An updater request argument is required.");
        }
        catch (OperationCanceledException)
        {
            UpdaterHostFailureLog.TryWrite("update.host_timeout");
            return 30;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            System.Security.Cryptography.CryptographicException or
            System.ComponentModel.Win32Exception)
        {
            UpdaterHostFailureLog.TryWrite(SafeErrorCode(exception));
            return 40;
        }
        catch (Exception)
        {
            // The updater host is a process boundary. Do not expose exception text or request data,
            // but always return a deterministic failure code and leave a bounded diagnostic record.
            UpdaterHostFailureLog.TryWrite(UpdaterHostFailureLog.UnclassifiedFailureCode);
            return 50;
        }
    }

    private static int ExitCode(UpdateTransactionResult result) =>
        result.Succeeded ? 0 : result.RolledBack ? 10 : 20;

    private static UpdateArtifactTrustPolicy CreateTrustPolicy() => new(
        new AuthenticodeSignatureVerifier(),
        UpdateTrustPolicyOptions.FromEnvironment());

    private static void ValidateArguments(IReadOnlyList<string> args)
    {
        if (args.Count != RequiredArgumentCount ||
            args.Any(static value => string.IsNullOrWhiteSpace(value) || value.Length > MaximumArgumentCharacters))
        {
            throw new ArgumentException("Updater arguments are invalid.");
        }

        var options = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Count; index += 2)
        {
            var option = args[index];
            var value = args[index + 1];
            if (value.StartsWith("--", StringComparison.Ordinal) ||
                !IsSupportedOption(option) ||
                !options.Add(option))
            {
                throw new ArgumentException("Updater arguments are invalid.");
            }
        }

        if (!options.Contains("--nonce") ||
            options.Contains("--request") == options.Contains("--rollback-request"))
        {
            throw new ArgumentException("Updater arguments are invalid.");
        }
    }

    private static string ReadRequiredOption(IReadOnlyList<string> args, string name) =>
        TryReadOption(args, name, out var value)
            ? value
            : throw new ArgumentException($"Required updater argument {name} is missing.");

    private static bool TryReadOption(IReadOnlyList<string> args, string name, out string value)
    {
        value = string.Empty;
        var found = false;
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found || index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Updater argument {name} is invalid.");
            }

            value = args[index + 1];
            found = true;
            index++;
        }

        return found;
    }

    private static bool IsSupportedOption(string option) =>
        string.Equals(option, "--request", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(option, "--rollback-request", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(option, "--nonce", StringComparison.OrdinalIgnoreCase);

    private static string SafeErrorCode(Exception exception) => exception switch
    {
        System.Security.Cryptography.CryptographicException => "update.host_trust_failed",
        UnauthorizedAccessException => "update.host_access_denied",
        InvalidDataException => "update.host_invalid_request",
        IOException => "update.host_io_failed",
        System.ComponentModel.Win32Exception => "update.host_windows_failed",
        InvalidOperationException => "update.host_invalid_state",
        _ => "update.host_invalid_arguments",
    };
}
