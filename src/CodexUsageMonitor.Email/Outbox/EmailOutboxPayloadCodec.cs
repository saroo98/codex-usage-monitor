using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexUsageMonitor.Core.Security;
using CodexUsageMonitor.Email.Models;

namespace CodexUsageMonitor.Email.Outbox;

public sealed class EmailOutboxPayloadCodec
{
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("CodexUsageMonitor.EmailOutbox.v1");
    private const int MaximumPayloadBytes = 512 * 1024;
    private readonly IProtectedDataStore _protectedData;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public EmailOutboxPayloadCodec(IProtectedDataStore protectedData)
    {
        _protectedData = protectedData ?? throw new ArgumentNullException(nameof(protectedData));
    }

    public string Encode(SelfNotification message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(message, _jsonOptions);
        if (plaintext.Length > MaximumPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException("Email payload is too large.");
        }

        try
        {
            var encrypted = _protectedData.Protect(plaintext, Purpose);
            try
            {
                return Convert.ToBase64String(encrypted);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public SelfNotification Decode(string encoded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoded);
        if (encoded.Length > MaximumPayloadBytes * 2)
        {
            throw new InvalidDataException("Protected email payload is too large.");
        }

        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Protected email payload is malformed.", exception);
        }

        try
        {
            var plaintext = _protectedData.Unprotect(encrypted, Purpose);
            try
            {
                if (plaintext.Length is <= 0 or > MaximumPayloadBytes)
                {
                    throw new InvalidDataException("Email payload has an invalid size.");
                }

                return JsonSerializer.Deserialize<SelfNotification>(plaintext, _jsonOptions)
                    ?? throw new InvalidDataException("Email payload is empty.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }
}
