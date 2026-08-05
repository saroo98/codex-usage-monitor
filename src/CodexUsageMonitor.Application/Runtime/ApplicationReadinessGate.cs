namespace CodexUsageMonitor.Application.Runtime;

public sealed class ApplicationReadinessGate
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => _ready.Task.IsCompletedSuccessfully;

    public Task WaitAsync(CancellationToken cancellationToken) =>
        _ready.Task.WaitAsync(cancellationToken);

    public void SignalReady() => _ready.TrySetResult();
}
