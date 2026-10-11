using System.Text;

namespace Cotabby.Core.Support;

/// <summary>
/// Renders the prompt for the base-model completion pipeline. Direct port of
/// the macOS <c>BaseCompletionPromptRenderer</c>.
///
/// Design: a *base* model has no instruction-following channel and will
/// happily continue a bare "Task:" line as if it were the document, so an
/// instruction-blob prompt would leak scaffolding into the ghost text. This
/// renderer treats the model as a pure text continuer: persona, style, and
/// language are folded into a short conditioning preface (a base model
/// conditions on description, it does not obey commands), and the caret
/// prefix is the LAST thing in the prompt with trailing whitespace trimmed so
/// generation begins at a clean word boundary.
/// </summary>
public static class BaseCompletionPromptRenderer
{
    /// <summary>Total character budget for the preface plus caret prefix.</summary>
    public const int DefaultContextBudget = 2400;

    public static string Prompt(
        string prefixText,
        string? userName = null,
        IReadOnlyList<string>? customRules = null,
        string? languageInstruction = null,
        string? extendedContext = null,
        int contextBudget = DefaultContextBudget,
        int? tokenBudget = null,
        Func<string, int>? tokenCounter = null)
    {
        if (tokenBudget.HasValue) ArgumentNullException.ThrowIfNull(tokenCounter);
        if (contextBudget <= 0 || tokenBudget is <= 0) return string.Empty;
        var trimmedPrefix = TrimTrailingWhitespace(prefixText);

        var sections = new List<PromptSection>();
        if (PersonaLine(userName) is { } persona)
        {
            sections.Add(ContextSection("persona", persona, priority: 60, maxChars: 200));
        }
        if (StyleLine(customRules) is { } style)
        {
            sections.Add(ContextSection("style", style, priority: 55, maxChars: 300));
        }
        if (NonEmpty(languageInstruction) is { } language)
        {
            sections.Add(ContextSection("language", language, priority: 50, maxChars: 300));
        }
        if (NonEmpty(extendedContext) is { } notes)
        {
            sections.Add(ContextSection(
                "notes", $"Notes the writer keeps in mind: {notes}", priority: 40, maxChars: 600));
        }
        // The caret prefix: top priority so it is never starved, kept by its
        // END (the text nearest the caret), and rendered last with no label so
        // the model continues from where the user stopped.
        sections.Add(new PromptSection(
            "prefix",
            trimmedPrefix,
            Priority: 100,
            MinChars: 1,
            MaxChars: Math.Max(1, trimmedPrefix.Length),
            SectionTruncation.PreserveEnd));

        var kept = PromptSectionBudget.Allocate(sections, contextBudget)
            .Select(s => s with { Content = TrimSurrogateEdges(s.Content) }).ToList();
        if (tokenBudget is { } budget)
        {
            FitTokenBudget(kept, budget, tokenCounter!);
        }
        return Render(kept);
    }

    private static string Render(IReadOnlyList<PromptSection> sections)
    {
        var prefix = sections.FirstOrDefault(s => s.Name == "prefix")?.Content ?? string.Empty;
        if (prefix.Length == 0) return string.Empty;
        var preface = sections.Where(s => s.Name != "prefix" && s.Content.Length > 0)
            .Select(s => s.Content).ToList();
        return preface.Count == 0 ? prefix : string.Join("\n", preface) + "\n\n" + prefix;
    }

    private static void FitTokenBudget(List<PromptSection> sections, int budget, Func<string, int> tokenCounter)
    {
        int Count()
        {
            int count = tokenCounter(Render(sections));
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            return count;
        }

        if (Count() <= budget) return;
        // Remove lower-priority conditioning before touching the caret. Measure
        // the entire serialized prompt, including separators and tokenizer BOS.
        var trimOrder = sections.Select((s, i) => (Section: s, Index: i))
            .OrderBy(t => t.Section.Priority).ThenByDescending(t => t.Index).ToList();
        foreach (var (section, index) in trimOrder)
        {
            sections[index] = section with { Content = string.Empty };
            if (Count() > budget) continue;

            int low = 0;
            int high = section.Content.Length;
            string best = string.Empty;
            while (low <= high)
            {
                int length = low + (high - low) / 2;
                var content = section.Truncation == SectionTruncation.PreserveEnd
                    ? section.Content[^length..] : section.Content[..length];
                content = TrimSurrogateEdges(content);
                sections[index] = section with { Content = content.Trim() };
                if (Count() <= budget)
                {
                    best = sections[index].Content;
                    low = length + 1;
                }
                else
                {
                    high = length - 1;
                }
            }
            // Token counts can be non-monotonic; use only a measured fit. The
            // search may underfill, but must never hand an oversized prompt on.
            sections[index] = section with { Content = best };
            return;
        }
        sections.Clear();
    }

    private static string TrimSurrogateEdges(string text)
    {
        int start = text.Length > 0 && char.IsLowSurrogate(text[0]) ? 1 : 0;
        int end = text.Length;
        if (end > start && char.IsHighSurrogate(text[end - 1])) end--;
        return text[start..end];
    }

    private static PromptSection ContextSection(string name, string content, int priority, int maxChars) =>
        new(name, content, priority, MinChars: 0, maxChars, SectionTruncation.PreserveStart);

    /// <summary>"Written by &lt;name&gt;." or null. Conditions the voice via authorship framing.</summary>
    private static string? PersonaLine(string? userName) =>
        NonEmpty(userName) is { } name ? $"Written by {name}." : null;

    /// <summary>"Writing style: &lt;rules&gt;." or null.</summary>
    private static string? StyleLine(IReadOnlyList<string>? customRules)
    {
        if (customRules is null) return null;
        var rules = customRules
            .Select(r => r.Trim())
            .Where(r => r.Length > 0)
            .ToList();
        return rules.Count == 0 ? null : $"Writing style: {string.Join(", ", rules)}.";
    }

    /// <summary>Description of the writer's languages, phrased as conditioning
    /// context (not an instruction — base models condition, they don't obey).</summary>
    public static string? LanguageInstruction(IReadOnlyList<string>? languages)
    {
        if (languages is null) return null;
        var langs = languages.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (langs.Count == 0) return null;
        return langs.Count == 1
            ? $"The text is written in {langs[0]}."
            : $"The text is written in {string.Join(", ", langs[..^1])} or {langs[^1]}.";
    }

    private static string? NonEmpty(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>Drops trailing spaces, tabs, and newlines so the base-model
    /// prompt ends at a word boundary.</summary>
    public static string TrimTrailingWhitespace(string text)
    {
        int end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        return text[..end];
    }

    /// <summary>
    /// Windows the raw caret prefix the way the macOS factory does for the
    /// llama engine: take the last <paramref name="maxChars"/> characters,
    /// split on whitespace, and keep the last <paramref name="maxWords"/>
    /// words joined by single spaces. Collapsing whitespace keeps the prompt
    /// token count predictable.
    /// </summary>
    public static string WindowPrefix(string precedingText, int maxWords = 50, int maxChars = 1000)
    {
        string window = precedingText.Length <= maxChars
            ? precedingText
            : precedingText[^maxChars..];
        var words = window.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return window;
        var kept = words.Length <= maxWords ? words : words[^maxWords..];
        return string.Join(' ', kept);
    }
}
