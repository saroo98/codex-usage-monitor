using System.Text.Json;
using System.Threading.Channels;
using CodexUsageMonitor.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.Persistence.Diagnostics;

public sealed class RedactingFileLoggerProvider : ILoggerProvider, ISupportExternalScope, IAsyncDisposable
{
    private readonly string _directory;
    private readonly long _maximumFileBytes;
    private readonly int _retainedFiles;
    private readonly LogLevel _minimumLevel;
    private readonly Channel<SafeLogEntry> _entries;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _writerTask;
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    private int _disposed;

    public RedactingFileLoggerProvider(
        string directory,
        LogLevel minimumLevel = LogLevel.Information,
        long maximumFileBytes = 2 * 1024 * 1024,
        int retainedFiles = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _maximumFileBytes = Math.Clamp(maximumFileBytes, 64 * 1024, 32L * 1024 * 1024);
        _retainedFiles = Math.Clamp(retainedFiles, 1, 20);
        _minimumLevel = minimumLevel;
        _entries = Channel.CreateBounded<SafeLogEntry>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        Directory.CreateDirectory(_directory);
        _writerTask = WriteLoopAsync(_cancellation.Token);
    }

    public ILogger CreateLogger(string categoryName) => new RedactingLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
        _scopes = scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider));

    internal bool IsEnabled(LogLevel level) => level >= _minimumLevel && level != LogLevel.None;

    internal IDisposable? PushScope<TState>(TState state) where TState : notnull => _scopes.Push(state);

    internal void Enqueue<TState>(
        string category,
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level) || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var scopes = new List<string>(4);
        _scopes.ForEachScope(static (scope, values) =>
        {
            var redacted = SafeDiagnosticRedactor.Redact(scope?.ToString());
            if (redacted.Length > 0 && values.Count < 8) values.Add(redacted);
        }, scopes);
        var message = SafeDiagnosticRedactor.Redact(formatter(state, exception));
        var safeException = exception is null
            ? null
            : $"{exception.GetType().Name}: {SafeDiagnosticRedactor.Redact(exception.Message)}";
        _entries.Writer.TryWrite(new SafeLogEntry(
            DateTimeOffset.UtcNow,
            level.ToString(),
            SafeDiagnosticRedactor.Redact(category),
            eventId.Id,
            SafeDiagnosticRedactor.Redact(eventId.Name),
            message,
            safeException,
            scopes));
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        var logPath = Path.Combine(_directory, "monitor.log.jsonl");
        try
        {
            await foreach (var entry in _entries.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                RotateIfNeeded(logPath);
                var line = JsonSerializer.Serialize(entry, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                await File.AppendAllTextAsync(logPath, line + Environment.NewLine, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Logging stopped: {exception.GetType().Name}");
        }
    }

    private void RotateIfNeeded(string activePath)
    {
        if (!File.Exists(activePath) || new FileInfo(activePath).Length < _maximumFileBytes)
        {
            return;
        }

        for (var index = _retainedFiles - 1; index >= 1; index--)
        {
            var source = index == 1 ? activePath : $"{activePath}.{index - 1}";
            var destination = $"{activePath}.{index}";
            if (File.Exists(source)) File.Move(source, destination, overwrite: true);
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _entries.Writer.TryComplete();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await _writerTask.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private sealed class RedactingLogger(RedactingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.PushScope(state);
        public bool IsEnabled(LogLevel logLevel) => owner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Enqueue(category, logLevel, eventId, state, exception, formatter);
    }

    private sealed record SafeLogEntry(
        DateTimeOffset TimestampUtc,
        string Level,
        string Category,
        int EventId,
        string EventName,
        string Message,
        string? Exception,
        IReadOnlyList<string> Scopes);
}
