namespace CodexUsageMonitor.Application.Runtime;

public interface IStartupHealthClassificationSource
{
    event EventHandler? ClassificationChanged;

    bool IsClassified { get; }
}

public sealed class StartupHealthQualification
{
    public static readonly TimeSpan DefaultClassificationTimeout = TimeSpan.FromSeconds(30);

    private readonly ApplicationStartupState _startup;
    private readonly IStartupHealthClassificationSource _classification;
    private readonly TimeSpan _timeout;

    public StartupHealthQualification(
        ApplicationStartupState startup,
        IStartupHealthClassificationSource classification)
        : this(startup, classification, DefaultClassificationTimeout)
    {
    }

    public StartupHealthQualification(
        ApplicationStartupState startup,
        IStartupHealthClassificationSource classification,
        TimeSpan timeout)
    {
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _classification = classification ?? throw new ArgumentNullException(nameof(classification));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _timeout = timeout;
    }

    public async Task<bool> WaitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_startup.IsUpdateHealthEligible)
        {
            return false;
        }

        if (_classification.IsClassified)
        {
            return true;
        }

        var classified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClassificationChanged(object? sender, EventArgs args)
        {
            if (_classification.IsClassified)
            {
                classified.TrySetResult();
            }
        }

        _classification.ClassificationChanged += OnClassificationChanged;
        try
        {
            if (!_startup.IsUpdateHealthEligible)
            {
                return false;
            }

            if (_classification.IsClassified)
            {
                return true;
            }

            using var timeout = new CancellationTokenSource(_timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            try
            {
                await classified.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                timeout.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            return _startup.IsUpdateHealthEligible && _classification.IsClassified;
        }
        finally
        {
            _classification.ClassificationChanged -= OnClassificationChanged;
        }
    }
}
