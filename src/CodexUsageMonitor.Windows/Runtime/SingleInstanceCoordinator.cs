using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.Windows.Runtime;

public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    private const int MaximumMessageBytes = 16 * 1024;
    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly ILogger<SingleInstanceCoordinator> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private Mutex? _mutex;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listenerTask;

    public SingleInstanceCoordinator(string applicationId, ILogger<SingleInstanceCoordinator> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var safeId = string.Concat(applicationId.Where(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-'));
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sid)))[..16];
        _mutexName = $"Local\\{safeId}.{hash}";
        _pipeName = $"{safeId}.{hash}.activation";
    }

    public bool TryAcquirePrimary()
    {
        if (_mutex is not null)
        {
            return true;
        }

        var mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return false;
        }

        _mutex = mutex;
        return true;
    }

    public async Task<bool> ForwardAsync(ActivationMessage message, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await using var client = new NamedPipeClientStream(
                serverName: ".",
                pipeName: _pipeName,
                direction: PipeDirection.InOut,
                options: PipeOptions.Asynchronous | PipeOptions.WriteThrough);
            await client.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
            var payload = JsonSerializer.SerializeToUtf8Bytes(message, _jsonOptions);
            if (payload.Length > MaximumMessageBytes)
            {
                throw new InvalidDataException("Activation payload exceeds the accepted limit.");
            }

            await client.WriteAsync(BitConverter.GetBytes(payload.Length), timeoutSource.Token).ConfigureAwait(false);
            await client.WriteAsync(payload, timeoutSource.Token).ConfigureAwait(false);
            await client.FlushAsync(timeoutSource.Token).ConfigureAwait(false);
            var acknowledgment = new byte[1];
            await client.ReadExactlyAsync(acknowledgment, timeoutSource.Token).ConfigureAwait(false);
            return acknowledgment[0] == 1;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            _logger.LogDebug(exception, "The existing instance did not accept activation.");
            return false;
        }
    }

    public void StartListening(Func<ActivationMessage, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_mutex is null, this);
        if (_listenerTask is not null)
        {
            throw new InvalidOperationException("Activation listener has already started.");
        }

        _listenerCancellation = new CancellationTokenSource();
        _listenerTask = ListenAsync(handler, _listenerCancellation.Token);
    }

    private async Task ListenAsync(Func<ActivationMessage, CancellationToken, Task> handler, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = CreateServer();
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var lengthBytes = new byte[sizeof(int)];
                await server.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
                var length = BitConverter.ToInt32(lengthBytes);
                if (length is <= 0 or > MaximumMessageBytes)
                {
                    throw new InvalidDataException("Activation message length is invalid.");
                }

                var payload = new byte[length];
                await server.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
                var message = JsonSerializer.Deserialize<ActivationMessage>(payload, _jsonOptions)
                    ?? throw new InvalidDataException("Activation message is empty.");
                if (!message.TryValidate(out var safeErrorCode))
                {
                    _logger.LogWarning("Rejected local activation message with code {SafeErrorCode}.", safeErrorCode);
                    await server.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
                    await server.FlushAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await handler(message, cancellationToken).ConfigureAwait(false);
                await server.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
                await server.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                _logger.LogWarning(exception, "Rejected an invalid local activation message.");
            }
        }
    }

    private NamedPipeServerStream CreateServer() => new(
        _pipeName,
        PipeDirection.InOut,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly,
        inBufferSize: MaximumMessageBytes,
        outBufferSize: 1);

    public async ValueTask DisposeAsync()
    {
        if (_listenerCancellation is not null)
        {
            await _listenerCancellation.CancelAsync().ConfigureAwait(false);
        }

        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _listenerCancellation?.Dispose();
        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _mutex.Dispose();
        }
    }
}
