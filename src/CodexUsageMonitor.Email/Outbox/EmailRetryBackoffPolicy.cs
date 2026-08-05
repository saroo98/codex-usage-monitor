using CodexUsageMonitor.Core.Scheduling;

namespace CodexUsageMonitor.Email.Outbox;

/// <summary>
/// Computes bounded full-jitter delays for durable email delivery retries.
/// </summary>
public sealed class EmailRetryBackoffPolicy
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromHours(1);
    private readonly IRandomSource _random;

    public EmailRetryBackoffPolicy(IRandomSource random)
    {
        _random = random ?? throw new ArgumentNullException(nameof(random));
    }

    public TimeSpan DelayForAttempt(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);

        var exponent = Math.Clamp(attempt - 1, 0, 8);
        var ceilingSeconds = Math.Min(
            MaximumDelay.TotalSeconds,
            InitialDelay.TotalSeconds * Math.Pow(2d, exponent));
        var sample = Math.Clamp(_random.NextDouble(), 0d, 1d);
        var jitteredSeconds = 1d + (sample * Math.Max(0d, ceilingSeconds - 1d));
        return TimeSpan.FromSeconds(jitteredSeconds);
    }
}
