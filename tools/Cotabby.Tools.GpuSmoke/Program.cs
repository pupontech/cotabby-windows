using System.Diagnostics;
using System.IO;
using Cotabby.App.Metrics;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("This probe requires Windows; cross-compilation is not runtime evidence.");

// Negative control for the Windows-only persistence regression: an open
// FileShare.None temp handle cannot be renamed. The production store must close
// its temp stream before replacing the destination.
var probeDirectory = Path.Combine(Path.GetTempPath(), "cotabby-metrics-proof-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(probeDirectory);
try
{
    var temporary = Path.Combine(probeDirectory, "open.tmp");
    bool sharingRejected = false;
    using (var held = File.Create(temporary))
    {
        try { File.Move(temporary, Path.Combine(probeDirectory, "renamed.json")); }
        catch (IOException) { sharingRejected = true; }
    }
    if (!sharingRejected) throw new InvalidOperationException("Windows open-temp negative control did not reject rename.");
    var storePath = Path.Combine(probeDirectory, "performance.json");
    var store = new Cotabby.Core.Suggestions.PerformanceMetricsStore(storePath) { TrackingEnabled = true };
    store.Record("synthetic-proof-model", 50);
    if (new Cotabby.Core.Suggestions.PerformanceMetricsStore(storePath).Entries.Count != 1)
        throw new InvalidOperationException("Production metrics persistence did not survive reopen.");
    store.Clear();
    if (new Cotabby.Core.Suggestions.PerformanceMetricsStore(storePath).Entries.Count != 0)
        throw new InvalidOperationException("Production metrics Clear did not persist.");
    Console.WriteLine("WINDOWS_METRICS_OPEN_TEMP_NEGATIVE_CONTROL_AND_PERSISTENCE_PASSED");
}
finally
{
    Directory.Delete(probeDirectory, recursive: true);
}

// Exercise the real Windows native counter calculator, not a portable formula mirror.
var previous = new CounterSample(0, 0, 10_000_000, 10_000_000, 10_000_000, 10_000_000,
    PerformanceCounterType.Timer100Ns);
var current = new CounterSample(5_000_000, 0, 10_000_000, 10_000_000, 20_000_000, 20_000_000,
    PerformanceCounterType.Timer100Ns);
float percent = CounterSample.Calculate(previous, current);
if (!float.IsFinite(percent) || Math.Abs(percent - 50f) > 0.01f)
    throw new InvalidOperationException($"Paired raw counter calculation returned {percent}; expected 50%.");
Console.WriteLine("WINDOWS_GPU_PAIRED_COUNTER_CALCULATION_PASSED=50");

// This hosted runner may expose no GPU categories. Both paths must remain safe;
// optional/unavailable data is legitimate and is not physical-GPU accuracy proof.
var provider = new WindowsGpuUsageProvider();
var first = provider.Read();
await Task.Delay(1100);
var second = provider.Read();
Console.WriteLine($"WINDOWS_GPU_ADAPTER_SAFE_READ_PASSED Device={second.DeviceUtilizationPercent?.ToString() ?? "unknown"} Memory={second.DeviceMemoryBytes?.ToString() ?? "unknown"}");
Console.WriteLine("PHYSICAL_GPU_ACCURACY=Not tested; owner hardware validation required");
return 0;
