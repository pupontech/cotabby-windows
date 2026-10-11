namespace Cotabby.Core.Support;

/// <summary>Reserve a request's entire decode before allocating prompt sections.</summary>
public static class CompletionPromptBudget
{
    public const int OutputReserveTokens = 50;
    public const int SafetyMarginTokens = 64;

    public static int ForContext(int contextTokens, int maxPredictionTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxPredictionTokens);
        long reserved = Math.Max(OutputReserveTokens, maxPredictionTokens);
        return (int)Math.Max(0L, (long)contextTokens - reserved - SafetyMarginTokens);
    }
}
