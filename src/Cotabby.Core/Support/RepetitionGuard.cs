namespace Cotabby.Core.Support;

/// <summary>
/// No-repeat n-gram guard used inside the decode loop. Direct port of the
/// macOS <c>RepetitionGuard</c>: with n = 3, the prefix is the last 2
/// generated tokens; every token that ever followed that same 2-gram earlier
/// in the generation is blocked, so a 3-gram can never repeat verbatim.
/// </summary>
public static class RepetitionGuard
{
    /// <summary>
    /// Post-decode companion to the token guard: a one-to-three-word sequence
    /// repeated three times is a visible loop even if token IDs differ.
    /// Ported from upstream CompletionContentPolicy.isRepetitive. Retains
    /// punctuation and allows two copies (ordinary emphasis).
    /// </summary>
    public static bool HasWordLoop(string completion)
    {
        var words = completion.ToLowerInvariant().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int period = 1; period <= 3; period++)
        {
            if (words.Length < period * 3) continue;
            int run = 0;
            for (int index = period; index < words.Length; index++)
            {
                run = words[index] == words[index - period] ? run + 1 : 0;
                if (run >= period * 2) return true;
            }
        }
        return false;
    }

    public static HashSet<int> BlockedTokens(IReadOnlyList<int> history, int ngramSize = 3)
    {
        var blocked = new HashSet<int>();
        if (ngramSize < 2) return blocked;
        int prefixLen = ngramSize - 1;
        if (history.Count < prefixLen) return blocked;

        // The prefix we are about to extend: the last (n-1) tokens.
        for (int start = 0; start + prefixLen < history.Count; start++)
        {
            bool match = true;
            for (int i = 0; i < prefixLen; i++)
            {
                if (history[start + i] != history[history.Count - prefixLen + i]) { match = false; break; }
            }
            if (match) blocked.Add(history[start + prefixLen]);
        }
        return blocked;
    }
}
