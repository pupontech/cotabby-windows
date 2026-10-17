namespace Cotabby.Core.Support;

public readonly record struct PowerSnapshot(bool? OnAcPower, int? BatteryChargePercent, bool Charging);
public enum InferencePosture { Normal, Throttled, Deferred }
public interface IPowerSnapshotProvider { PowerSnapshot Snapshot { get; } }

/// <summary>Best-effort, opt-in battery policy. Unknown/no-battery means unchanged behavior.</summary>
public static class BatteryPolicy
{
    public static InferencePosture Evaluate(PowerSnapshot power, bool enabled, int threshold,
        InferencePosture previous = InferencePosture.Normal)
    {
        if (!enabled || power.OnAcPower is not false || power.Charging ||
            power.BatteryChargePercent is not int charge || charge is < 0 or > 100)
            return InferencePosture.Normal;
        threshold = Math.Clamp(threshold, 5, 95);
        int resume = previous == InferencePosture.Deferred ? threshold + 5 : threshold;
        return charge < resume ? InferencePosture.Deferred : InferencePosture.Throttled;
    }

    public static int TokenBudget(int tokens, InferencePosture posture, bool multiLine = false)
    {
        if (posture != InferencePosture.Throttled) return tokens;
        bool multiTier = multiLine || tokens > 25;
        int single = multiTier ? tokens / 2 : tokens;
        int lowered = single > 15 ? 15 : single > 9 ? 9 : 5;
        return multiTier ? lowered * 2 : Math.Min(tokens, lowered);
    }

    public static TimeSpan Debounce(TimeSpan baseline, InferencePosture posture) =>
        posture == InferencePosture.Throttled ? baseline + baseline : baseline;
}
