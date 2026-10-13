namespace Cotabby.Core.Metrics;

/// <summary>
/// One reading of the machine's GPU counters. Every value is optional so an
/// adapter that cannot read a counter reports "no data" rather than a
/// misleading zero. Windows equivalent of upstream's <c>GPUStatisticsReading</c>.
/// </summary>
public sealed record GpuUsageSample
{
    /// <summary>Busiest adapter's busiest physical engine, 0-100.</summary>
    public double? DeviceUtilizationPercent { get; init; }

    /// <summary>This process's GPU share since the previous sample, 0-100.
    /// Null on the first sample, when the counters are unavailable, or when the
    /// counter shrank (a command queue was released).</summary>
    public double? ProcessUtilizationPercent { get; init; }

    /// <summary>GPU memory in use across the machine, in bytes.</summary>
    public long? DeviceMemoryBytes { get; init; }

    /// <summary>This process's cumulative GPU engine time in 100 ns ticks,
    /// summed over all parsed engine types. Feeds the tracker's share
    /// arithmetic; null when the process has never touched the GPU.</summary>
    public long? ProcessEngineTicks { get; init; }

    /// <summary>Per-instance cumulative ticks preserve identity so a reset on one
    /// engine cannot be hidden by growth on another. Null when unavailable.</summary>
    public IReadOnlyDictionary<string, long>? ProcessEngineTicksByInstance { get; init; }

    public static readonly GpuUsageSample Unavailable = new();
}

/// <summary>Reads the current GPU counters; implementations must not throw.</summary>
public interface IGpuUsageProvider
{
    /// <summary>The next reading; never throws, returns <see cref="GpuUsageSample.Unavailable"/> instead.</summary>
    GpuUsageSample Read();
}

/// <summary>Provider whose paired raw-counter baselines can be released/reset.</summary>
public interface IGpuUsageProviderResettable : IGpuUsageProvider
{
    void Reset();
}

/// <summary>
/// Stateful wrapper that turns consecutive provider readings into a per-process
/// share. Kept platform-free so the arithmetic is testable without a GPU.
/// </summary>
public sealed class GpuUsageTracker
{
    private readonly IGpuUsageProvider _provider;
    private readonly Func<long> _clockMilliseconds;

    private long? _previousEngineTicks;
    private IReadOnlyDictionary<string, long>? _previousEngineTicksByInstance;
    private long _previousClock;

    public GpuUsageTracker(IGpuUsageProvider provider, Func<long>? clockMilliseconds = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _clockMilliseconds = clockMilliseconds ?? (() => Environment.TickCount64);
    }

    /// <summary>Read every field for display, plus the derived process share.</summary>
    public GpuUsageSample Sample()
    {
        var raw = _provider.Read();
        long now = _clockMilliseconds();
        double? processShare = MetricsAggregator.ProcessSharePercent(
            _previousEngineTicks,
            raw.ProcessEngineTicks,
            _previousClock,
            now);
        if (raw.ProcessEngineTicksByInstance is { } currentInstances)
        {
            var previousInstances = _previousEngineTicksByInstance;
            if (previousInstances is null || previousInstances.Count != currentInstances.Count
                || currentInstances.Any(pair => !previousInstances.TryGetValue(pair.Key, out long prior)
                    || pair.Value < prior))
            {
                processShare = null;
            }
        }
        _previousEngineTicksByInstance = raw.ProcessEngineTicksByInstance;
        _previousEngineTicks = raw.ProcessEngineTicks;
        _previousClock = now;
        return raw with { ProcessUtilizationPercent = processShare };
    }

    public void Reset()
    {
        _previousEngineTicks = null;
        _previousEngineTicksByInstance = null;
        _previousClock = 0;
        if (_provider is IGpuUsageProviderResettable resettable)
        {
            resettable.Reset();
        }
    }
}