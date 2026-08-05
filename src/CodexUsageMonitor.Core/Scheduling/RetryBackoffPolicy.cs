namespace CodexUsageMonitor.Core.Scheduling;

public interface IRandomSource
{
    double NextDouble();
}

public sealed class RandomSource : IRandomSource
{
    public static RandomSource Shared { get; } = new();

    private RandomSource()
    {
    }

    public double NextDouble() => Random.Shared.NextDouble();
}

public sealed class RetryBackoffPolicy
{
    private readonly IRandomSource _random;

    public RetryBackoffPolicy(IRandomSource random)
    {
        _random = random ?? throw new ArgumentNullException(nameof(random));
    }

    public TimeSpan DelayFor(int consecutiveFailures)
    {
        var exponent = Math.Clamp(consecutiveFailures - 1, 0, 7);
        var baseSeconds = Math.Min(120d, Math.Pow(2d, exponent));
        var jitter = 0.8d + (_random.NextDouble() * 0.4d);
        return TimeSpan.FromSeconds(baseSeconds * jitter);
    }
}
