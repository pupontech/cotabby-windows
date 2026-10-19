namespace Cotabby.Core.Insertion;

public enum InsertionMethod
{
    Unicode,
    Clipboard,
}

/// <summary>Maps the global insertion preference to a concrete supported path.</summary>
public static class InsertionStrategySelector
{
    /// <summary>
    /// Resolves <paramref name="configuredStrategy"/> for one insertion.
    /// Missing and unknown persisted values retain the historical automatic rule.
    /// Stored Unicode and UIA choices are unsupported and return false rather
    /// than selecting a path that could unexpectedly touch the clipboard.
    /// </summary>
    public static bool TrySelect(string? configuredStrategy, string text, out InsertionMethod method)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (string.Equals(configuredStrategy, "uia", StringComparison.OrdinalIgnoreCase)
            || string.Equals(configuredStrategy, "unicode", StringComparison.OrdinalIgnoreCase))
        {
            method = default;
            return false;
        }

        method = configuredStrategy?.ToLowerInvariant() switch
        {
            "clipboard" => InsertionMethod.Clipboard,
            _ => text.Length <= 24 && !text.Contains('\n')
                ? InsertionMethod.Unicode
                : InsertionMethod.Clipboard,
        };
        return true;
    }
}
