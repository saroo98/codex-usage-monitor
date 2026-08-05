using System.Text.Json;
using CodexUsageMonitor.Codex.Contracts;
using CodexUsageMonitor.Codex.Protocol;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.Codex;

public sealed class AppServerClient : IAppServerClient
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private readonly JsonRpcConnection _connection;
    private readonly ILogger<AppServerClient> _logger;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private int _initialized;

    public AppServerClient(JsonRpcConnection connection, ILogger<AppServerClient> logger)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connection.NotificationReceived += OnNotificationReceived;
    }

    public bool IsInitialized => Volatile.Read(ref _initialized) != 0;

    public event EventHandler<RateLimitsReadResult>? RateLimitsUpdated;

    public async Task<AppServerInitialization> InitializeAsync(CancellationToken cancellationToken)
    {
        if (IsInitialized)
        {
            return new AppServerInitialization(null, null, null, default);
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsInitialized)
            {
                return new AppServerInitialization(null, null, null, default);
            }

            var parameters = JsonSerializer.SerializeToElement(new
            {
                clientInfo = new
                {
                    name = "codex_usage_monitor_windows",
                    title = "Codex Usage Monitor for Windows",
                    version = "1.0.0",
                },
                capabilities = new
                {
                    experimentalApi = false,
                },
            });
            var result = await _connection
                .InvokeElementAsync("initialize", parameters, DefaultTimeout, cancellationToken)
                .ConfigureAwait(false);
            await _connection
                .SendNotificationElementAsync("initialized", EmptyObject(), cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _initialized, 1);
            return new AppServerInitialization(
                ReadString(result, "codexHome"),
                ReadString(result, "platform"),
                ReadString(result, "userAgent"),
                result);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<AccountReadResult> ReadAccountAsync(bool refreshToken, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var result = await _connection
            .InvokeElementAsync(
                "account/read",
                JsonSerializer.SerializeToElement(new { refreshToken }),
                DefaultTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return new AccountReadResult(result);
    }

    public async Task<RateLimitsReadResult> ReadRateLimitsAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var result = await _connection
            .InvokeElementAsync("account/rateLimits/read", EmptyObject(), DefaultTimeout, cancellationToken)
            .ConfigureAwait(false);
        return new RateLimitsReadResult(result);
    }

    public async Task<UsageReadResult> ReadUsageAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        try
        {
            var result = await _connection
                .InvokeElementAsync("account/usage/read", EmptyObject(), DefaultTimeout, cancellationToken)
                .ConfigureAwait(false);
            return new UsageReadResult(true, result);
        }
        catch (AppServerRpcException exception) when (exception.RpcCode is -32601)
        {
            _logger.LogDebug("Codex App Server does not expose account/usage/read.");
            return new UsageReadResult(false, default);
        }
    }

    public async Task<ResetCreditConsumeResult> ConsumeResetCreditAsync(
        string? creditId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        if (creditId is { Length: > 512 })
        {
            throw new ArgumentOutOfRangeException(nameof(creditId), "Reset credit identifiers are limited to 512 characters.");
        }

        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("Idempotency key cannot be empty.", nameof(idempotencyKey));
        }

        var parameters = new Dictionary<string, object?>
        {
            ["idempotencyKey"] = idempotencyKey.ToString("D"),
        };
        if (!string.IsNullOrWhiteSpace(creditId))
        {
            parameters["creditId"] = creditId.Trim();
        }

        var result = await _connection
            .InvokeElementAsync(
                "account/rateLimitResetCredit/consume",
                JsonSerializer.SerializeToElement(parameters),
                TimeSpan.FromSeconds(30),
                cancellationToken)
            .ConfigureAwait(false);
        return ResetCreditConsumeResult.FromRaw(result);
    }

    public async ValueTask DisposeAsync()
    {
        _connection.NotificationReceived -= OnNotificationReceived;
        _initializationLock.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private void OnNotificationReceived(object? sender, JsonRpcNotificationEventArgs eventArgs)
    {
        if (!StringComparer.Ordinal.Equals(eventArgs.Method, "account/rateLimits/updated"))
        {
            return;
        }

        RateLimitsUpdated?.Invoke(this, new RateLimitsReadResult(eventArgs.Parameters));
    }

    private void EnsureInitialized()
    {
        if (!IsInitialized)
        {
            throw new InvalidOperationException("Codex App Server must be initialized before normal requests.");
        }
    }

    private static JsonElement EmptyObject() => JsonSerializer.SerializeToElement(new { });

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind is JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBoolean(JsonElement element, string propertyName) =>
        element.ValueKind is JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind is JsonValueKind.True;
}
