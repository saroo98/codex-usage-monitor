using System.ComponentModel;
using System.Net.Mail;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using CodexUsageMonitor.Core.Security;
using CodexUsageMonitor.Core.Settings;
using CodexUsageMonitor.Email.Security;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.App.Services;

public enum EmailCredentialState
{
    NotStored,
    Stored,
    StoredWithCleanupWarning,
    Unavailable,
}

public sealed record EmailCredentialStatus(EmailCredentialState State, string SafeMessageCode)
{
    public bool IsStored => State is EmailCredentialState.Stored or EmailCredentialState.StoredWithCleanupWarning;

    public static EmailCredentialStatus NotStored { get; } = new(EmailCredentialState.NotStored, "email.password_not_stored");

    public static EmailCredentialStatus Stored { get; } = new(EmailCredentialState.Stored, "email.password_stored");
}

/// <summary>
/// Owns the SMTP-password lifecycle. Secret material crosses the WPF boundary only as a
/// <see cref="SecureString"/>, is converted into a bounded temporary UTF-8 buffer, and is
/// cleared immediately after the Windows credential operation completes.
/// </summary>
public sealed class EmailCredentialService
{
    private const int MaximumPasswordCharacters = 1024;
    private const int MaximumPasswordBytes = 2560;
    private readonly ApplicationSettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly ILogger<EmailCredentialService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EmailCredentialService(
        ApplicationSettingsService settings,
        ISecretStore secrets,
        ILogger<EmailCredentialService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<EmailCredentialStatus> GetSmtpPasswordStatusAsync(
        string? senderAddress,
        CancellationToken cancellationToken)
    {
        string? reference;
        try
        {
            reference = ResolveReference(senderAddress, _settings.Current.Email.CredentialReference);
        }
        catch (FormatException)
        {
            return EmailCredentialStatus.NotStored;
        }

        if (reference is null)
        {
            return EmailCredentialStatus.NotStored;
        }

        byte[]? secret = null;
        try
        {
            secret = await _secrets.GetAsync(reference, cancellationToken).ConfigureAwait(false);
            return secret is { Length: > 0 } ? EmailCredentialStatus.Stored : EmailCredentialStatus.NotStored;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            _logger.LogWarning(exception, "SMTP credential status could not be read.");
            return new EmailCredentialStatus(EmailCredentialState.Unavailable, "email.credential_status_unavailable");
        }
        finally
        {
            if (secret is not null)
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }
    }

    public async Task<EmailCredentialStatus> StoreSmtpPasswordAsync(
        string senderAddress,
        SecureString password,
        CancellationToken cancellationToken) =>
        await StoreSmtpPasswordAsync(EmailProviderMode.OtherSmtp, senderAddress, password, cancellationToken).ConfigureAwait(false);

    public async Task<EmailCredentialStatus> StoreSmtpPasswordAsync(
        EmailProviderMode provider,
        string senderAddress,
        SecureString password,
        CancellationToken cancellationToken)
    {
        if (provider is not (EmailProviderMode.OtherSmtp or EmailProviderMode.ProtonMailBridge))
        {
            throw new ArgumentOutOfRangeException(nameof(provider));
        }

        var normalizedSender = NormalizeSender(senderAddress);
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length is <= 0 or > MaximumPasswordCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(password), "The SMTP password length is outside the supported range.");
        }

        var newReference = EmailSecretKeyFactory.SmtpPassword(normalizedSender);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = _settings.Current.Email;
            byte[]? priorAtNewReference = null;
            var secretBytes = ToUtf8Bytes(password);
            try
            {
                priorAtNewReference = await _secrets.GetAsync(newReference, cancellationToken).ConfigureAwait(false);
                await _secrets.SetAsync(newReference, secretBytes, cancellationToken).ConfigureAwait(false);
                try
                {
                    await _settings.UpdateAsync(settings => settings with
                    {
                        Email = settings.Email with
                        {
                            Provider = provider,
                            Enabled = false,
                            ConnectedAddress = null,
                            SenderAddress = normalizedSender,
                            CredentialReference = newReference,
                            OAuthTokenReference = null,
                            OAuthRegistrationId = null,
                        },
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await RestoreSecretAsync(newReference, priorAtNewReference, cancellationToken).ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secretBytes);
                if (priorAtNewReference is not null)
                {
                    CryptographicOperations.ZeroMemory(priorAtNewReference);
                }
            }

            if (!string.IsNullOrWhiteSpace(before.CredentialReference) &&
                !string.Equals(before.CredentialReference, newReference, StringComparison.Ordinal))
            {
                try
                {
                    await _secrets.DeleteAsync(before.CredentialReference, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
                {
                    _logger.LogWarning(exception, "An obsolete SMTP credential reference could not be removed.");
                    return new EmailCredentialStatus(
                        EmailCredentialState.StoredWithCleanupWarning,
                        "email.password_stored_cleanup_pending");
                }
            }

            return EmailCredentialStatus.Stored;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EmailCredentialStatus> RemoveSmtpPasswordAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = _settings.Current.Email;
            var reference = ResolveReference(before.SenderAddress, before.CredentialReference);
            if (reference is null)
            {
                return EmailCredentialStatus.NotStored;
            }

            var priorSecret = await _secrets.GetAsync(reference, cancellationToken).ConfigureAwait(false);
            try
            {
                await _secrets.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
                try
                {
                    await _settings.UpdateAsync(settings => settings with
                    {
                        Email = settings.Email with { CredentialReference = null },
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    if (priorSecret is { Length: > 0 })
                    {
                        await _secrets.SetAsync(reference, priorSecret, cancellationToken).ConfigureAwait(false);
                    }

                    throw;
                }
            }
            finally
            {
                if (priorSecret is not null)
                {
                    CryptographicOperations.ZeroMemory(priorSecret);
                }
            }

            return EmailCredentialStatus.NotStored;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task DeleteReferenceAsync(string? reference, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(reference)
            ? Task.CompletedTask
            : _secrets.DeleteAsync(reference.Trim(), cancellationToken);

    private static string? ResolveReference(string? senderAddress, string? configuredReference)
    {
        if (!string.IsNullOrWhiteSpace(configuredReference))
        {
            return configuredReference.Trim();
        }

        return string.IsNullOrWhiteSpace(senderAddress)
            ? null
            : EmailSecretKeyFactory.SmtpPassword(NormalizeSender(senderAddress));
    }

    private static string NormalizeSender(string senderAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(senderAddress);
        var trimmed = senderAddress.Trim();
        var parsed = new MailAddress(trimmed);
        if (!string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("The sender email address is invalid.");
        }

        return parsed.Address;
    }

    private async Task RestoreSecretAsync(
        string reference,
        byte[]? priorSecret,
        CancellationToken cancellationToken)
    {
        if (priorSecret is { Length: > 0 })
        {
            await _secrets.SetAsync(reference, priorSecret, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _secrets.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] ToUtf8Bytes(SecureString secret)
    {
        var pointer = nint.Zero;
        var characters = new char[secret.Length];
        try
        {
            pointer = Marshal.SecureStringToGlobalAllocUnicode(secret);
            Marshal.Copy(pointer, characters, 0, characters.Length);
            var byteCount = Encoding.UTF8.GetByteCount(characters.AsSpan());
            if (byteCount is <= 0 or > MaximumPasswordBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(secret), "The encoded SMTP password is outside the supported range.");
            }

            var bytes = new byte[byteCount];
            Encoding.UTF8.GetBytes(characters.AsSpan(), bytes.AsSpan());
            return bytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
            if (pointer != nint.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(pointer);
            }
        }
    }
}
