using System.Collections.Concurrent;
using System.Text.Json;
using CodexUsageMonitor.Codex.Transport;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.Codex.Protocol;

public sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly IJsonLineTransport _transport;
    private readonly ILogger<JsonRpcConnection> _logger;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _readerTask;
    private long _nextRequestId;
    private int _disposed;

    public JsonRpcConnection(IJsonLineTransport transport, ILogger<JsonRpcConnection> logger)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _readerTask = Task.Run(() => ReadLoopAsync(_lifetime.Token));
    }

    public event EventHandler<JsonRpcNotificationEventArgs>? NotificationReceived;

    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _transport.IsConnected;

    public async Task<TResponse> InvokeAsync<TParams, TResponse>(
        string method,
        TParams parameters,
        JsonTypeInfoPair<TParams, TResponse> typeInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("A duplicate JSON-RPC request ID was generated.");
        }

        try
        {
            var envelope = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = JsonSerializer.SerializeToElement(parameters, typeInfo.Parameters),
            };
            var line = JsonSerializer.Serialize(envelope);
            await _transport.WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            linked.CancelAfter(timeout);
            var result = await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            return result.Deserialize(typeInfo.Response)
                ?? throw new InvalidDataException("Codex App Server returned an empty response body.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException($"Codex App Server method '{method}' exceeded its response deadline.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task<JsonElement> InvokeElementAsync(
        string method,
        JsonElement parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("A duplicate JSON-RPC request ID was generated.");
        }

        try
        {
            var envelope = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters,
            };
            await _transport.WriteLineAsync(JsonSerializer.Serialize(envelope), cancellationToken).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            linked.CancelAfter(timeout);
            return await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException($"Codex App Server method '{method}' exceeded its response deadline.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public ValueTask SendNotificationElementAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var envelope = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters,
        };
        return _transport.WriteLineAsync(JsonSerializer.Serialize(envelope), cancellationToken);
    }

    public ValueTask SendNotificationAsync<TParams>(
        string method,
        TParams parameters,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> typeInfo,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var envelope = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = JsonSerializer.SerializeToElement(parameters, typeInfo),
        };
        return _transport.WriteLineAsync(JsonSerializer.Serialize(envelope), cancellationToken);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? terminalError = null;
        try
        {
            await foreach (var line in _transport.ReadLinesAsync(cancellationToken).ConfigureAwait(false))
            {
                ProcessLine(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            terminalError = exception;
            _logger.LogWarning(exception, "Codex App Server JSON-RPC reader stopped.");
        }
        finally
        {
            var failure = terminalError is null
                ? new EndOfStreamException("Codex App Server connection closed.")
                : new IOException("Codex App Server connection failed.", terminalError);
            foreach (var completion in _pending.Values)
            {
                completion.TrySetException(failure);
            }
        }
    }

    private void ProcessLine(string line)
    {
        using var document = JsonDocument.Parse(line, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        var root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object)
        {
            throw new JsonException("JSON-RPC messages must be objects.");
        }

        if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id))
        {
            if (!_pending.TryGetValue(id, out var completion))
            {
                _logger.LogDebug("Ignoring unmatched Codex App Server response {RequestId}.", id);
                return;
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind is JsonValueKind.Object)
            {
                var rpcCode = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsed)
                    ? parsed
                    : (int?)null;
                completion.TrySetException(new AppServerRpcException("rpc_error", rpcCode));
                return;
            }

            if (!root.TryGetProperty("result", out var result))
            {
                completion.TrySetException(new JsonException("JSON-RPC response did not contain a result."));
                return;
            }

            completion.TrySetResult(result.Clone());
            return;
        }

        if (root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind is JsonValueKind.String)
        {
            var method = methodElement.GetString();
            var parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement.Clone()
                : default;
            NotificationReceived?.Invoke(this, new JsonRpcNotificationEventArgs(method ?? string.Empty, parameters));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        await _transport.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _readerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _lifetime.Dispose();
    }
}

public sealed record JsonRpcNotificationEventArgs(string Method, JsonElement Parameters);

public sealed record JsonTypeInfoPair<TParams, TResponse>(
    System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> Parameters,
    System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> Response);
