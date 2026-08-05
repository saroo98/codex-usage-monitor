using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CodexUsageMonitor.Codex.Transport;

public sealed class ProcessJsonLineTransport : IJsonLineTransport
{
    public const int MaximumLineCharacters = 1024 * 1024;
    private readonly Process _process;
    private readonly IProcessContainment _containment;
    private readonly ILogger<ProcessJsonLineTransport> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;

    private ProcessJsonLineTransport(
        Process process,
        IProcessContainment containment,
        ILogger<ProcessJsonLineTransport> logger)
    {
        _process = process;
        _containment = containment;
        _logger = logger;
    }

    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && !_process.HasExited;

    public static ProcessJsonLineTransport Start(
        ResolvedCodexCommand command,
        string? codexHome,
        IProcessContainment containment,
        ILogger<ProcessJsonLineTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(containment);
        ArgumentNullException.ThrowIfNull(logger);
        var info = CodexExecutableResolver.CreateStartInfo(command, ["app-server", "--stdio"]);
        info.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            info.Environment["CODEX_HOME"] = Path.GetFullPath(codexHome);
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("Codex App Server did not start.");
        }

        try
        {
            containment.Attach(process);
            return new ProcessJsonLineTransport(process, containment, logger);
        }
        catch
        {
            TryTerminate(process);
            process.Dispose();
            containment.Dispose();
            throw;
        }
    }

    public async IAsyncEnumerable<string> ReadLinesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length > MaximumLineCharacters)
            {
                throw new InvalidDataException("Codex App Server emitted an oversized JSON line.");
            }

            yield return line;
        }

        if (!_process.HasExited)
        {
            yield break;
        }

        var stderr = await ReadBoundedErrorAsync(_process.StandardError, cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("Codex App Server exited with code {ExitCode}. Diagnostic category: {DiagnosticCategory}",
            _process.ExitCode,
            string.IsNullOrWhiteSpace(stderr) ? "none" : "stderr-present");
    }

    public async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        if (line.Length > MaximumLineCharacters || line.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new InvalidDataException("Outbound JSON-RPC messages must be one bounded line.");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process.HasExited)
            {
                throw new EndOfStreamException("Codex App Server is no longer running.");
            }

            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _process.StandardInput.Close();
            if (!_process.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TryTerminate(_process);
                }
            }
        }
        finally
        {
            _writeLock.Dispose();
            _process.Dispose();
            _containment.Dispose();
        }
    }

    private static async Task<string> ReadBoundedErrorAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        return read == 0 ? string.Empty : new string(buffer, 0, read);
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
