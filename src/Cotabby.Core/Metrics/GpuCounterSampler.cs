namespace Cotabby.Core.Metrics;

/// <summary>
/// One engine row from a counter-category snapshot. Utilization is the paired
/// CounterSample.Calculate result (null on first/stale/missing samples); running
/// time is the raw cumulative 100 ns counter value, not a rate sample.
/// </summary>
public readonly record struct GpuEngineCounterReading(
    string InstanceName,
    double? UtilizationPercent,
    long? RunningTimeTicks);

/// <summary>Turns an injected raw-counter snapshot into the app's real GPU sample.</summary>
public static class GpuCounterSampler
{
    /// <summary>
    /// Builds the displayed sample from one GPU Engine category read and an
    /// independent adapter-memory read. Readable zero memory is preserved.
    /// </summary>
    public static GpuUsageSample CreateSample(
        IEnumerable<GpuEngineCounterReading> engineReadings,
        int processId,
        long? deviceMemoryBytes)
    {
        ArgumentNullException.ThrowIfNull(engineReadings);

        var utilizationInstances = new List<GpuEngineInstance>();
        long processEngineTicks = 0;
        bool hasProcessTicks = false;
        bool tickOverflow = false;
        bool incompleteProcessTicks = false;
        var processTicksByInstance = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var reading in engineReadings)
        {
            GpuEngineIdentity? identity = MetricsAggregator.ParseEngineIdentity(reading.InstanceName);
            if (identity is not { } parsed) continue;

            if (reading.UtilizationPercent is { } percent
                && MetricsAggregator.ParseEngineInstance(reading.InstanceName, percent) is { } utilization)
            {
                utilizationInstances.Add(utilization);
            }

            if (parsed.ProcessId != processId) continue;
            if (reading.RunningTimeTicks is not { } ticks || ticks < 0)
            {
                incompleteProcessTicks = true;
                continue;
            }
            if (!processTicksByInstance.TryAdd(reading.InstanceName, ticks))
            {
                incompleteProcessTicks = true;
                continue;
            }

            try
            {
                processEngineTicks = checked(processEngineTicks + ticks);
                hasProcessTicks = true;
            }
            catch (OverflowException)
            {
                tickOverflow = true;
            }
        }

        return new GpuUsageSample
        {
            DeviceUtilizationPercent = MetricsAggregator.DeviceUtilizationPercent(utilizationInstances),
            ProcessEngineTicks = hasProcessTicks && !tickOverflow && !incompleteProcessTicks ? processEngineTicks : null,
            ProcessEngineTicksByInstance = hasProcessTicks && !tickOverflow && !incompleteProcessTicks
                ? processTicksByInstance : null,
            DeviceMemoryBytes = deviceMemoryBytes is >= 0 ? deviceMemoryBytes : null,
        };
    }
}
