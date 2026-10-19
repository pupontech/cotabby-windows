namespace Cotabby.Core.Insertion;

/// <summary>Bound small Unicode event bursts without separating a valid surrogate pair.</summary>
public static class UnicodeInputBatcher
{
    public static IEnumerable<string> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (int start = 0; start < text.Length;)
        {
            int end = Math.Min(start + 8, text.Length);
            if (end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
                end--;
            yield return text[start..end];
            start = end;
        }
    }
}
