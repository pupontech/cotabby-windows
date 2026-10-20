namespace Cotabby.Core.Focus;

/// <summary>Metadata-only search policy; do not read field values to classify them.</summary>
public static class SearchFieldDetector
{
    private static readonly string[] Keywords = ["search", "look for", "find", "filter", "jump to", "ara", "arama"];

    public static bool IsSearchField(string controlType, IEnumerable<string?> labels, string? automationId)
    {
        if (controlType is not ("Edit" or "ComboBox" or "SearchBox")) return false;
        if (controlType == "SearchBox") return true;
        foreach (var label in labels)
        {
            if (string.IsNullOrEmpty(label)) continue;
            foreach (var word in Keywords)
            {
                int from = 0;
                while (from < label.Length)
                {
                    int at = label.IndexOf(word, from, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) break;
                    int end = at + word.Length;
                    if ((at == 0 || !char.IsLetterOrDigit(label[at - 1]))
                        && (end == label.Length || !char.IsLetterOrDigit(label[end]))) return true;
                    from = end;
                }
            }
        }
        if (string.IsNullOrEmpty(automationId)) return false;
        // Punctuation and camel-case humps start segments; "research" is not "search".
        for (int i = 0; i < automationId.Length; i++)
        {
            bool start = i == 0 || !char.IsLetterOrDigit(automationId[i - 1])
                || (char.IsLower(automationId[i - 1]) && char.IsUpper(automationId[i]));
            if (start && automationId.AsSpan(i).StartsWith("search", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
