using System.Globalization;
using System.Text.RegularExpressions;

namespace Cotabby.Core.Metrics;

/// <summary>Full adapter/physical-engine identity parsed from a GPU Engine instance.</summary>
public readonly record struct GpuEngineIdentity(
    int ProcessId, string AdapterId, int PhysicalEngineId, int EngineId, string EngineType);

/// <summary>One process contribution to a physical GPU engine.</summary>
public readonly record struct GpuEngineInstance(
    int ProcessId,
    string EngineType,
    double Percent,
    string AdapterId = "",
    int PhysicalEngineId = 0,
    int EngineId = 0);

/// <summary>
/// Platform-free parsing, arithmetic, and display formatting for Windows GPU counters.
/// </summary>
public static class MetricsAggregator
{
    private const double MaxPercent = 100d;
    private static readonly Regex EngineInstancePattern = new(
        @"^pid_(?<pid>[0-9]+)_luid_(?<high>0[xX][0-9a-fA-F]+)_(?<low>0[xX][0-9a-fA-F]+)_phys_(?<physical>[0-9]+)_eng_(?<engine>[0-9]+)_engtype_(?<type>[A-Za-z0-9](?:[A-Za-z0-9_]*[A-Za-z0-9])?)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses the complete Windows instance identity. Incomplete, malformed, or
    /// overflowing pid/engine fields are unknown rather than guessed.
    /// </summary>
    public static GpuEngineIdentity? ParseEngineIdentity(string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName)) return null;

        Match match = EngineInstancePattern.Match(instanceName);
        if (!match.Success
            || !int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int processId)
            || !int.TryParse(match.Groups["physical"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int physicalEngineId)
            || !int.TryParse(match.Groups["engine"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int engineId))
        {
            return null;
        }

        string adapterId = match.Groups["high"].Value.ToUpperInvariant()
            + "_" + match.Groups["low"].Value.ToUpperInvariant();
        return new GpuEngineIdentity(
            processId, adapterId, physicalEngineId, engineId, match.Groups["type"].Value);
    }

    /// <summary>Parses one instance and a finite utilization reading.</summary>
    public static GpuEngineInstance? ParseEngineInstance(string? instanceName, double? percent)
    {
        if (percent is not { } value || !double.IsFinite(value)) return null;
        if (ParseEngineIdentity(instanceName) is not { } identity) return null;

        return new GpuEngineInstance(
            identity.ProcessId,
            identity.EngineType,
            Clamp(value),
            identity.AdapterId,
            identity.PhysicalEngineId,
            identity.EngineId);
    }

    /// <summary>
    /// Whole-machine utilization is explicitly the busiest adapter: sum process
    /// contributions by adapter + physical engine + engine ID, clamp each engine
    /// to 100%, then choose the busiest engine per adapter and the busiest adapter.
    /// Different engines/adapters are never added together.
    /// </summary>
    public static double? DeviceUtilizationPercent(IEnumerable<GpuEngineInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);

        var adapters = new Dictionary<string, Dictionary<(int PhysicalEngineId, int EngineId), double>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var instance in instances)
        {
            if (!IsUsable(instance)) continue;

            if (!adapters.TryGetValue(instance.AdapterId, out var engines))
            {
                engines = new Dictionary<(int, int), double>();
                adapters.Add(instance.AdapterId, engines);
            }

            var engineKey = (instance.PhysicalEngineId, instance.EngineId);
            engines.TryGetValue(engineKey, out double current);
            engines[engineKey] = Math.Min(MaxPercent, current + Clamp(instance.Percent));
        }

        double? busiestAdapter = null;
        foreach (var engines in adapters.Values)
        {
            if (engines.Count == 0) continue;
            double adapterBusy = engines.Values.Max();
            if (busiestAdapter is null || adapterBusy > busiestAdapter.Value)
            {
                busiestAdapter = adapterBusy;
            }
        }
        return busiestAdapter;
    }

    /// <summary>
    /// Sum a process's contributions across every readable engine type (including
    /// Copy and numbered Compute engines), clamped to 100% to bound overlap.
    /// </summary>
    public static double? ProcessUtilizationPercent(
        IEnumerable<GpuEngineInstance> instances, int processId)
    {
        ArgumentNullException.ThrowIfNull(instances);

        double total = 0d;
        bool anyUsableInstance = false;
        foreach (var instance in instances)
        {
            if (!IsUsable(instance)) continue;
            anyUsableInstance = true;
            if (instance.ProcessId == processId)
            {
                total = Math.Min(MaxPercent, total + Clamp(instance.Percent));
            }
        }
        return anyUsableInstance ? total : null;
    }

    /// <summary>
    /// A process's own share between two readings, from cumulative Running Time
    /// raw values in 100 ns units. Missing samples, non-positive elapsed time, or
    /// a counter shrink (released queue) yield unknown; valid growth is clamped
    /// to 100% to guard overlapping engine counters.
    /// </summary>
    public static double? ProcessSharePercent(
        long? previousEngineTicks, long? currentEngineTicks,
        long previousMilliseconds, long currentMilliseconds)
    {
        if (previousEngineTicks is not { } previous || currentEngineTicks is not { } current) return null;
        if (current < previous) return null;

        double elapsedMilliseconds = (double)currentMilliseconds - previousMilliseconds;
        if (elapsedMilliseconds <= 0d) return null;

        double elapsedSeconds = elapsedMilliseconds / 1000d;
        double usedSeconds = ((double)current - previous) / 1e7d;
        double percent = usedSeconds / elapsedSeconds * 100d;
        return double.IsFinite(percent) ? Clamp(percent) : null;
    }

    /// <summary>Formats bytes for the Performance pane, or an em dash when unknown/invalid.</summary>
    public static string FormatBytes(long? bytes)
    {
        if (bytes is not { } value || value < 0) return "—";
        return value >= 1L << 30
            ? (value / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : (value / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>Formats a finite percentage for the Performance pane, or an em dash.</summary>
    public static string FormatPercent(double? percent) =>
        percent is { } value && double.IsFinite(value)
            ? value.ToString("0", CultureInfo.InvariantCulture) + "%"
            : "—";

    private static bool IsUsable(GpuEngineInstance instance) =>
        instance.ProcessId >= 0
        && !string.IsNullOrWhiteSpace(instance.EngineType)
        && instance.AdapterId is not null
        && instance.PhysicalEngineId >= 0
        && instance.EngineId >= 0
        && double.IsFinite(instance.Percent);

    private static double Clamp(double percent) =>
        percent < 0d ? 0d : percent > MaxPercent ? MaxPercent : percent;
}
