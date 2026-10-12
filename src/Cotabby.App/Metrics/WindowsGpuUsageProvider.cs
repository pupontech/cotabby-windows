using System.Diagnostics;
using System.Globalization;
using Cotabby.Core.Metrics;

namespace Cotabby.App.Metrics;

/// <summary>
/// Windows Performance Counter adapter for <see cref="IGpuUsageProvider"/>.
/// Wraps the "GPU Engine" / "GPU Adapter Memory" sets, which publish
/// <c>pid_&lt;n&gt;_luid_&lt;h&gt;_&lt;l&gt;_phys_&lt;n&gt;_eng_&lt;n&gt;_engtype_&lt;type&gt;</c> instances, and maps
/// them onto the platform-free <see cref="MetricsAggregator"/>. Every failure
/// path (counters disabled, damaged registry, unsupported adapter) degrades to
/// "no data"; this type never throws to its caller.
/// </summary>
public sealed class WindowsGpuUsageProvider : IGpuUsageProviderResettable
{
    private const string EngineCategory = "GPU Engine";
    private const string AdapterMemoryCategory = "GPU Adapter Memory";
    private const string UtilizationCounter = "Utilization Percentage";
    private const string DedicatedMemoryCounter = "Dedicated Usage";
    private const string SharedMemoryCounter = "Shared Usage";
    private const string EngineTimeCounter = "Running Time";

    private readonly int _processId;
    private readonly Dictionary<string, CounterSample> _previousUtilizationSamples =
        new(StringComparer.OrdinalIgnoreCase);

    public WindowsGpuUsageProvider(int? processId = null)
    {
        _processId = processId ?? Environment.ProcessId;
    }

    /// <summary>True when the GPU Engine counter set can be enumerated at all.</summary>
    public static bool IsCounterSetAvailable()
    {
        try { return PerformanceCounterCategory.Exists(EngineCategory); }
        catch (Exception) { return false; }
    }

    public GpuUsageSample Read()
    {
        try
        {
            var engines = ReadEngineCounterSamples();
            long? memoryBytes = ReadAdapterMemoryBytes();
            return GpuCounterSampler.CreateSample(engines, _processId, memoryBytes);
        }
        catch (Exception)
        {
            return GpuUsageSample.Unavailable;
        }
    }

    /// <summary>Drop paired-sample baselines when GPU sampling leaves the pane.</summary>
    public void Reset() => _previousUtilizationSamples.Clear();

    private IReadOnlyCollection<GpuEngineCounterReading> ReadEngineCounterSamples()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists(EngineCategory))
            {
                Reset();
                return Array.Empty<GpuEngineCounterReading>();
            }

            // Read the category once: separate PerformanceCounter instances
            // otherwise restart their rate counters and never produce a pair.
            var counters = new PerformanceCounterCategory(EngineCategory).ReadCategory();
            InstanceDataCollection? utilization = counters.Contains(UtilizationCounter)
                ? counters[UtilizationCounter]
                : null;
            InstanceDataCollection? runningTime = counters.Contains(EngineTimeCounter)
                ? counters[EngineTimeCounter]
                : null;

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddInstanceNames(names, utilization);
            AddInstanceNames(names, runningTime);

            var currentUtilizationSamples = new Dictionary<string, CounterSample>(StringComparer.OrdinalIgnoreCase);
            var results = new List<GpuEngineCounterReading>(names.Count);
            foreach (string name in names)
            {
                double? percent = null;
                long? runningTicks = null;

                if (utilization is not null && utilization.Contains(name))
                {
                    try
                    {
                        CounterSample current = utilization[name].Sample;
                        if (_previousUtilizationSamples.TryGetValue(name, out CounterSample previous)
                            && IsNewerSample(previous, current))
                        {
                            double calculated = CounterSample.Calculate(previous, current);
                            if (double.IsFinite(calculated)) percent = calculated;
                        }
                        currentUtilizationSamples[name] = current;
                    }
                    catch (Exception)
                    {
                        // A broken instance is unknown for this read; other engines remain usable.
                    }
                }

                if (runningTime is not null && runningTime.Contains(name))
                {
                    try
                    {
                        long rawTicks = runningTime[name].Sample.RawValue;
                        if (rawTicks >= 0) runningTicks = rawTicks;
                    }
                    catch (Exception)
                    {
                        // Running Time is independently optional from utilization.
                    }
                }

                if (percent is not null || runningTicks is not null)
                {
                    results.Add(new GpuEngineCounterReading(name, percent, runningTicks));
                }
            }

            _previousUtilizationSamples.Clear();
            foreach (var (name, sample) in currentUtilizationSamples)
            {
                _previousUtilizationSamples[name] = sample;
            }
            return results;
        }
        catch (Exception)
        {
            Reset();
            return Array.Empty<GpuEngineCounterReading>();
        }
    }

    private static void AddInstanceNames(HashSet<string> names, InstanceDataCollection? counter)
    {
        if (counter is null) return;
        foreach (string name in counter.Keys) names.Add(name);
    }

    private static bool IsNewerSample(CounterSample previous, CounterSample current) =>
        current.TimeStamp > previous.TimeStamp
        && current.TimeStamp100nSec > previous.TimeStamp100nSec;

    private static long? ReadAdapterMemoryBytes()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists(AdapterMemoryCategory)) return null;
            var counters = new PerformanceCounterCategory(AdapterMemoryCategory).ReadCategory();
            long total = 0;
            bool any = false;
            foreach (var counterName in new[] { DedicatedMemoryCounter, SharedMemoryCounter })
            {
                if (!counters.Contains(counterName)) continue;
                foreach (InstanceData instance in counters[counterName].Values)
                {
                    long value = instance.RawValue;
                    if (value < 0) continue;
                    total = checked(total + value);
                    any = true;
                }
            }
            return any ? total : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Formats bytes for the Performance pane, or a dash when unknown.</summary>
    public static string FormatBytes(long? bytes) =>
        MetricsAggregator.FormatBytes(bytes);

    /// <summary>Formats a percentage for the Performance pane, or a dash when unknown.</summary>
    public static string FormatPercent(double? percent) =>
        MetricsAggregator.FormatPercent(percent);
}