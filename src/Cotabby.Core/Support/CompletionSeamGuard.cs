using System;
using System.Collections.Generic;

namespace Cotabby.Core.Support;

/// <summary>
/// Why a generation or a streamed partial was withheld from the user. Mirrors
/// upstream Cotabby's <c>CompletionSeamGuard.Verdict</c>.
/// </summary>
public enum SeamGuardVerdict
{
    Allow,
    JunkPunctuationRun,
    SeamMisspelling,
    LeadingWordMisspelling,
    AbandonedWord,
}

/// <summary>One explicit spelling result for a candidate word.</summary>
public enum SpellingAssessment
{
    /// <summary>The native spell checker recognizes the word (or it is a known reference word).</summary>
    Known,

    /// <summary>The checker calls it a typo but has no replacement, so it may be jargon or a name.</summary>
    UncorrectableTypo,

    /// <summary>The checker calls it a typo and offers a correction, so presenting it is a visible glitch.</summary>
    CorrectableTypo,
}

/// <summary>
/// Streaming decision for an incomplete leading word. Upstream buffers the first
/// generated word until its boundary is known, then resolves the gate once per
/// generation so the spell checker never runs at token cadence.
/// </summary>
public enum StreamedLeadingVerdict
{
    /// <summary>Keep buffering; not enough of the first word is known yet.</summary>
    Wait,

    /// <summary>The leading word is complete and acceptable.</summary>
    Allow,

    /// <summary>The leading word is a correctable typo — suppress the whole partial.</summary>
    Suppress,
}

/// <summary>
/// Post-generation guard for the two classic visible failures at the caret seam:
/// junk punctuation runs ("....", "$$$$") and mid-word splices that turn the word
/// being typed into a misspelling ("gre" + "atful"). Showing nothing beats showing
/// either, and both checks are pure string work on a single short completion, so
/// the guard costs microseconds once per generation. Windows adaptation of the
/// macOS <c>CompletionSeamGuard</c> (upstream #814, then refined by #832's
/// presentation decision); the Windows spell backend is pluggable, so the guard
/// stays pure and fail-open on anything the backend cannot assess.
/// </summary>
public static class CompletionSeamGuard
{
    /// <summary>Identical punctuation/symbol characters in a row that count as junk when freshly introduced.</summary>
    private const int JunkRunLength = 4;

    /// <summary>Joined seam words shorter than this are too ambiguous to judge ("a" + "t").</summary>
    private const int MinimumSeamWordLength = 4;

    /// <summary>
    /// The spell assessment is injected so the pure rule stays testable and the
    /// caller picks the backend. A single result describes the whole invariant:
    /// mid-word seams reject any typo the backend flags (unknown vocabulary keeps
    /// its conservative ending-only presentation upstream; on Windows there is no
    /// trailing-text presentation, so unknown words simply pass), while newly
    /// generated words reject only correctable typos.
    /// </summary>
    public static SeamGuardVerdict Verdict(
        string precedingText,
        string completion,
        Func<string, SpellingAssessment> spellingAssessment)
    {
        if (IntroducesJunkPunctuationRun(precedingText, completion))
        {
            return SeamGuardVerdict.JunkPunctuationRun;
        }

        if (MisspellingCandidateSeamWord(precedingText, completion) is { } seamWord
            && spellingAssessment(seamWord) != SpellingAssessment.Known)
        {
            return SeamGuardVerdict.SeamMisspelling;
        }

        if (LeadingWordProbe(precedingText, completion).Candidate is { } leadingWord
            && spellingAssessment(leadingWord) == SpellingAssessment.CorrectableTypo)
        {
            return SeamGuardVerdict.LeadingWordMisspelling;
        }

        return SeamGuardVerdict.Allow;
    }

    /// <summary>
    /// Cheap streaming-path junk rule. The separate leading-word streaming verdict
    /// buffers until a complete word exists, then performs and caches exactly one
    /// spelling decision.
    /// </summary>
    public static bool AllowsStreamedPartial(string precedingText, string completion) =>
        !IntroducesJunkPunctuationRun(precedingText, completion);

    /// <summary>
    /// Leading-word half of the streaming guard. Incomplete first words remain
    /// buffered; testing a prefix such as "ecr" would create false positives and
    /// repeating the lookup on every token would put the spell backend on the hot
    /// streaming path.
    /// </summary>
    public static StreamedLeadingVerdict StreamedLeadingWord(
        string precedingText,
        string completion,
        Func<string, SpellingAssessment> spellingAssessment)
    {
        var probe = LeadingWordProbe(precedingText, completion);
        if (probe.Candidate is null) return probe.IsApplicable
            ? StreamedLeadingVerdict.Wait
            : StreamedLeadingVerdict.Allow;
        if (!probe.IsComplete) return StreamedLeadingVerdict.Wait;
        return spellingAssessment(probe.Candidate) == SpellingAssessment.CorrectableTypo
            ? StreamedLeadingVerdict.Suppress
            : StreamedLeadingVerdict.Allow;
    }

    // ---- Junk punctuation runs ---------------------------------------------

    private static bool IntroducesJunkPunctuationRun(string precedingText, string completion)
    {
        // Mirror the upstream walk: track the current run as it grows. A run
        // flush against the seam that continues a run of the same character the
        // user already has at the caret is an existing divider being extended,
        // not fresh junk — but only when the preceding run is real (two or
        // more): a sentence that merely ends in "." must not exempt "...." from
        // the completion.
        char? runCharacter = null;
        int runLength = 0;
        bool runStartsAtCompletionStart = false;

        for (int i = 0; i < completion.Length; i++)
        {
            char character = completion[i];
            if (character == runCharacter)
            {
                runLength++;
            }
            else
            {
                runCharacter = character;
                runLength = 1;
                runStartsAtCompletionStart = i == 0;
            }

            if (runLength < JunkRunLength) continue;
            char current = runCharacter!.Value;
            if (!char.IsPunctuation(current) && !char.IsSymbol(current)) continue;

            if (runStartsAtCompletionStart && TrailingRunLength(precedingText, current) >= 2) continue;
            return true;
        }
        return false;
    }

    private static int TrailingRunLength(string text, char character)
    {
        int count = 0;
        for (int i = text.Length - 1; i >= 0 && text[i] == character; i--) count++;
        return count;
    }

    // ---- Mid-word seam ----

    /// <summary>
    /// In the mid-word case — the preceding text ends inside a word and the
    /// completion starts with word characters — the joined word must be known.
    /// Skipped for capitalized words (names and brands are routinely
    /// out-of-dictionary), short joins, words with digits, and CJK text.
    /// </summary>
    private static string? MisspellingCandidateSeamWord(string precedingText, string completion)
    {
        if (completion.Length == 0) return null;
        char last = precedingText.Length > 0 ? precedingText[^1] : '\0';
        if (!char.IsLetter(last)) return null;
        if (!char.IsLetter(completion[0])) return null;

        int end = 0;
        while (end < completion.Length && char.IsLetter(completion[end])) end++;

        string joined = TrailingWord(precedingText) + completion[..end];
        return MisspellingCandidate(joined) ? joined : null;
    }

    /// <summary>The final run of letters in <paramref name="text"/>, or empty when absent.</summary>
    private static string TrailingWord(string text)
    {
        int i = text.Length;
        while (i > 0 && char.IsLetter(text[i - 1])) i--;
        return text[i..];
    }

    private static bool MisspellingCandidate(string word)
    {
        if (word.Length < MinimumSeamWordLength) return false;
        if (!char.IsLower(word[0])) return false;
        for (int i = 1; i < word.Length; i++)
        {
            if (char.IsUpper(word[i])) return false;
        }
        return !ContainsCjk(word) && !ContainsDigit(word);
    }

    // ---- Leading word probe ----

    private readonly record struct Probe(string? Candidate, bool IsComplete, bool IsApplicable);

    /// <summary>
    /// Finds the first lexical word after boundary whitespace or punctuation.
    /// Apostrophes and hyphens between letters remain part of the word
    /// ("doesn't", "state-of-the-art") so the spell checker sees the same
    /// natural-language token the user sees.
    /// </summary>
    private static Probe LeadingWordProbe(string precedingText, string completion)
    {
        if (completion.Length == 0) return new(null, false, IsApplicable: true);

        int wordStart = -1;
        for (int i = 0; i < completion.Length; i++)
        {
            if (char.IsLetter(completion[i])) { wordStart = i; break; }
        }
        if (wordStart < 0)
        {
            // Whitespace and opening punctuation may arrive before the first
            // streamed word. Digits make the token code/version-like, so the
            // conservative spelling rule does not apply.
            bool onlyNeutral = true;
            foreach (char c in completion)
            {
                if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c))
                {
                    onlyNeutral = false;
                    break;
                }
            }
            return new(null, false, IsApplicable: onlyNeutral);
        }

        // A digit anywhere in the same whitespace-delimited token makes it
        // code/version-like. Scan the whole token before extracting its leading
        // letter run so "ecrir2" is not misread as the correctable natural
        // language word "ecrir".
        int tokenEnd = wordStart;
        while (tokenEnd < completion.Length && !char.IsWhiteSpace(completion[tokenEnd])) tokenEnd++;
        for (int i = wordStart; i < tokenEnd; i++)
        {
            if (char.IsNumber(completion[i])) return new(null, false, IsApplicable: false);
        }
        for (int i = 0; i < wordStart; i++)
        {
            if (char.IsNumber(completion[i])) return new(null, false, IsApplicable: false);
        }

        // A bare apostrophe or hyphen can continue the word before the caret
        // ("don" + "'t"). Any other prefix character establishes a real boundary
        // before the generated word.
        bool precedingEndsInLetter = precedingText.Length > 0
            && char.IsLetter(precedingText[^1]);
        if (precedingEndsInLetter)
        {
            bool allConnectors = true;
            for (int i = 0; i < wordStart; i++)
            {
                if (!IsWordConnector(completion[i])) { allConnectors = false; break; }
            }
            if (allConnectors) return new(null, false, IsApplicable: false);
        }

        int wordEnd = wordStart;
        bool endsInDanglingConnector = false;
        while (wordEnd < completion.Length)
        {
            char c = completion[wordEnd];
            if (char.IsLetter(c)) { wordEnd++; continue; }
            int next = wordEnd + 1;
            if (IsWordConnector(c) && next < completion.Length && char.IsLetter(completion[next]))
            {
                wordEnd = next;
                continue;
            }
            endsInDanglingConnector = IsWordConnector(c) && next == completion.Length;
            break;
        }
        if (wordEnd >= completion.Length)
        {
            // The scan consumed the whole completion; a trailing connector at
            // the very end keeps the word incomplete.
            endsInDanglingConnector = IsWordConnector(completion[^1]);
        }

        string word = completion[wordStart..wordEnd];
        if (!MisspellingCandidate(word))
        {
            return wordEnd < completion.Length && !endsInDanglingConnector
                ? new(null, false, IsApplicable: false)
                : new(null, false, IsApplicable: true);
        }
        bool complete = wordEnd < completion.Length && !endsInDanglingConnector;
        return new(word, complete, IsApplicable: true);
    }

    /// <summary>Apostrophes and hyphens bind adjacent letter runs into one natural-language token.</summary>
    private static bool IsWordConnector(char c) => c is '\'' or '\u2019' or '-';

    private static bool ContainsCjk(string s)
    {
        foreach (char c in s)
        {
            if (c >= '\u4E00' && c <= '\u9FFF') return true;
            if (c >= '\u3400' && c <= '\u4DBF') return true;
            if (c >= '\u3040' && c <= '\u30FF') return true;
            if (c >= '\uAC00' && c <= '\uD7AF') return true;
        }
        return false;
    }

    private static bool ContainsDigit(string s)
    {
        foreach (char c in s)
        {
            if (char.IsNumber(c)) return true;
        }
        return false;
    }
}
