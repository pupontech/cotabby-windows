using Cotabby.Core.Support;
using Xunit;

namespace Cotabby.Core.Tests;

/// <summary>
/// Ports of the upstream CompletionSeamGuard test surface (upstream #814,
/// refined by #832): junk punctuation runs, mid-word seam misspellings, and
/// correctable leading-word misspellings. The Windows spell backend is
/// injected, so every case pins the pure rule against an explicit assessment
/// function rather than a system provider.
/// </summary>
public class CompletionSeamGuardTests
{
    private static Func<string, SpellingAssessment> Fixed(SpellingAssessment assessment) => _ => assessment;

    private static Func<string, SpellingAssessment> Only(string correctable, string uncorrectable) =>
        word => word == correctable ? SpellingAssessment.CorrectableTypo
            : word == uncorrectable ? SpellingAssessment.UncorrectableTypo
            : SpellingAssessment.Known;

    // ---- Junk punctuation runs ---------------------------------------------

    [Fact]
    public void JunkRun_IsRejected()
    {
        Assert.Equal(SeamGuardVerdict.JunkPunctuationRun,
            CompletionSeamGuard.Verdict("Hello ", "!!!!", Fixed(SpellingAssessment.Known)));
        Assert.Equal(SeamGuardVerdict.JunkPunctuationRun,
            CompletionSeamGuard.Verdict("Hello ", " wait...... ok", Fixed(SpellingAssessment.Known)));
    }

    [Fact]
    public void JunkRun_ExtendingExistingRunIsAllowed()
    {
        // The caret already sits on "----"; continuing it is legitimate.
        Assert.Equal(SeamGuardVerdict.Allow,
            CompletionSeamGuard.Verdict("Heading ----", "----", Fixed(SpellingAssessment.Known)));
    }

    [Fact]
    public void ShortPunctuationRunsPass()
    {
        Assert.Equal(SeamGuardVerdict.Allow,
            CompletionSeamGuard.Verdict("Hi ", "... ok", Fixed(SpellingAssessment.Known)));
    }

    // ---- Mid-word seam ------------------------------------------------------

    [Theory]
    [InlineData("gre", "atful")]
    [InlineData("morn", "ingwax")]
    public void MidWordUnknownJoin_IsSuppressed(string prefix, string completion)
    {
        var r = CompletionSeamGuard.Verdict(prefix, completion, Fixed(SpellingAssessment.UncorrectableTypo));
        Assert.Equal(SeamGuardVerdict.SeamMisspelling, r);
    }

    [Fact]
    public void MidWordKnownJoin_IsAllowed()
    {
        // "car" + "pet" is a real word.
        Assert.Equal(SeamGuardVerdict.Allow,
            CompletionSeamGuard.Verdict("car", "pet", Fixed(SpellingAssessment.Known)));
    }

    [Fact]
    public void MidWordCorrectableJoin_IsSuppressed()
    {
        Assert.Equal(SeamGuardVerdict.SeamMisspelling,
            CompletionSeamGuard.Verdict("gre", "att", w => w == "greatt" ? SpellingAssessment.CorrectableTypo : SpellingAssessment.Known));
    }

    [Fact]
    public void MidWordCapitalizedJoin_IsAllowed()
    {
        // Names and brands are routinely out-of-dictionary.
        Assert.Equal(SeamGuardVerdict.Allow,
            CompletionSeamGuard.Verdict("Call St", "eve", Fixed(SpellingAssessment.UncorrectableTypo)));
    }

    [Fact]
    public void ShortJoin_IsAllowed()
    {
        Assert.Equal(SeamGuardVerdict.Allow,
            CompletionSeamGuard.Verdict("a", "t", Fixed(SpellingAssessment.UncorrectableTypo)));
    }

    [Fact]
    public void DigitJoin_IsAllowed()
    {
        Assert.Equal(SeamGuardVerdict.Allow,
            CompletionSeamGuard.Verdict("v", "2beta", Fixed(SpellingAssessment.UncorrectableTypo)));
    }

    [Fact]
    public void WordBoundaryStart_IsNotASeam()
    {
        // A completion that starts with whitespace can never be a mid-word splice.
        Assert.Equal(SeamGuardVerdict.Allow,
            CompletionSeamGuard.Verdict("gre", " atful", Fixed(SpellingAssessment.UncorrectableTypo)));
    }

    [Fact]
    public void CjkJoin_IsAllowed()
    {
        Assert.Equal(SeamGuardVerdict.Allow,
            CompletionSeamGuard.Verdict("\u4f60", "\u597d", Fixed(SpellingAssessment.UncorrectableTypo)));
    }

    // ---- Leading word -------------------------------------------------------

    [Fact]
    public void CorrectableLeadingWord_IsSuppressed()
    {
        Assert.Equal(SeamGuardVerdict.LeadingWordMisspelling,
            CompletionSeamGuard.Verdict("Je veux ", "ecrir plus vite",
                Only("ecrir", uncorrectable: "\u0000NeverMatch")));
    }

    [Fact]
    public void UncorrectableLeadingWord_IsAllowed()
    {
        // Unknown vocabulary without a correction stays visible.
        Assert.Equal(SeamGuardVerdict.Allow,
            CompletionSeamGuard.Verdict("Je veux ", "ecrir plus vite",
                Only(uncorrectable: "ecrir", correctable: "\u0000NeverMatch")));
    }

    [Fact]
    public void KnownLeadingWord_IsAllowed()
    {
        Assert.Equal(SeamGuardVerdict.Allow,
            CompletionSeamGuard.Verdict("Je veux ", "écrire plus vite", Fixed(SpellingAssessment.Known)));
    }

    [Fact]
    public void CapitalizedLeadingWord_IsAllowed()
    {
        Assert.Equal(SeamGuardVerdict.Allow,
            CompletionSeamGuard.Verdict("Hello ", "Zebra crossing",
                Only(correctable: "Zebra", uncorrectable: "\u0000NeverMatch")));
    }

    [Fact]
    public void ShortLeadingWord_IsAllowedWithoutAssessment()
    {
        var called = false;
        var r = CompletionSeamGuard.Verdict("Hi ", "th is fine", _ => { called = true; return SpellingAssessment.CorrectableTypo; });
        Assert.Equal(SeamGuardVerdict.Allow, r);
        Assert.False(called, "the spell backend must not run for a leading token too short to judge");
    }

    [Fact]
    public void TokenWithDigit_IsAllowedWithoutAssessment()
    {
        var called = false;
        var r = CompletionSeamGuard.Verdict("Use ", "v2beta soon", _ => { called = true; return SpellingAssessment.CorrectableTypo; });
        Assert.Equal(SeamGuardVerdict.Allow, r);
        Assert.False(called, "code/version tokens never reach the spell backend");
    }

    [Fact]
    public void LeadingWordAfterPunctuation_IsAssessed()
    {
        Assert.Equal(SeamGuardVerdict.LeadingWordMisspelling,
            CompletionSeamGuard.Verdict("Ready. ", "ecrir now",
                Only("ecrir", uncorrectable: "\u0000NeverMatch")));
    }

    [Fact]
    public void ContractionLeadingWord_IsAssessedAsOneWord()
    {
        // "doesn" + "'t think so" — the apostrophe binds into one token.
        Assert.Equal(SeamGuardVerdict.LeadingWordMisspelling,
            CompletionSeamGuard.Verdict("He ", "doesntx think so",
                Only("doesntx", uncorrectable: "\u0000NeverMatch")));
    }

    [Fact]
    public void ConnectorContinuationOfCaretWord_IsNotALeadingWord()
    {
        // "don" + "'t" continues the caret word. The connector prefix keeps the
        // leading-word rule out (the probe returns not-applicable), so nothing
        // reaches the spell backend and the completion passes.
        var assessed = new System.Collections.Generic.HashSet<string>();
        var r = CompletionSeamGuard.Verdict("don", "'t worry",
            w => { assessed.Add(w); return SpellingAssessment.CorrectableTypo; });
        Assert.Equal(SeamGuardVerdict.Allow, r);
        Assert.Empty(assessed);
        Assert.Equal(StreamedLeadingVerdict.Allow,
            CompletionSeamGuard.StreamedLeadingWord("don", "'t",
                _ => { assessed.Add("*"); return SpellingAssessment.CorrectableTypo; }));
        Assert.Empty(assessed);
    }

    [Fact]
    public void FinalVerdictSuppressesCorrectableLeadingWordWithoutTrailingBoundary()
    {
        // No trailing space after the word: still suppressed at the boundary.
        Assert.Equal(SeamGuardVerdict.LeadingWordMisspelling,
            CompletionSeamGuard.Verdict("Je veux ", "ecrir",
                Only("ecrir", uncorrectable: "\u0000NeverMatch")));
    }

    // ---- Streaming leading-word gate ---------------------------------------

    [Fact]
    public void StreamedLeadingWordWaitsUntilItsBoundaryArrives()
    {
        Assert.Equal(StreamedLeadingVerdict.Wait,
            CompletionSeamGuard.StreamedLeadingWord("Je veux ", "ec",
                Only("ecrir", uncorrectable: "\u0000NeverMatch")));
        Assert.Equal(StreamedLeadingVerdict.Wait,
            CompletionSeamGuard.StreamedLeadingWord("Je veux ", "ecrir",
                Only("ecrir", uncorrectable: "\u0000NeverMatch")));
    }

    [Fact]
    public void StreamedCorrectableLeadingWordIsSuppressedAtItsBoundary()
    {
        Assert.Equal(StreamedLeadingVerdict.Suppress,
            CompletionSeamGuard.StreamedLeadingWord("Je veux ", "ecrir ",
                Only("ecrir", uncorrectable: "\u0000NeverMatch")));
        Assert.Equal(StreamedLeadingVerdict.Suppress,
            CompletionSeamGuard.StreamedLeadingWord("Je veux ", "ecrir plus",
                Only("ecrir", uncorrectable: "\u0000NeverMatch")));
    }

    [Fact]
    public void StreamedKnownLeadingWordIsAllowedAtItsBoundary()
    {
        Assert.Equal(StreamedLeadingVerdict.Allow,
            CompletionSeamGuard.StreamedLeadingWord("Je veux ", "écrire ",
                Fixed(SpellingAssessment.Known)));
    }

    [Fact]
    public void StreamedTokenWithDigitIsAllowedWithoutAssessment()
    {
        var called = false;
        var v = CompletionSeamGuard.StreamedLeadingWord("Use ", "v2 ", _ => { called = true; return SpellingAssessment.CorrectableTypo; });
        Assert.Equal(StreamedLeadingVerdict.Allow, v);
        Assert.False(called);
    }

    [Fact]
    public void StreamedCapitalizedLeadingWordIsAllowedWithoutAssessment()
    {
        var called = false;
        var v = CompletionSeamGuard.StreamedLeadingWord("Hello ", "Zebra ", _ => { called = true; return SpellingAssessment.CorrectableTypo; });
        Assert.Equal(StreamedLeadingVerdict.Allow, v);
        Assert.False(called);
    }

    [Fact]
    public void StreamedLeadingPunctuationOnlyBuffers()
    {
        // Whitespace/punctuation prefix has not yet reached a word: keep waiting.
        Assert.Equal(StreamedLeadingVerdict.Wait,
            CompletionSeamGuard.StreamedLeadingWord("Hi ", " ", Fixed(SpellingAssessment.Known)));
        Assert.Equal(StreamedLeadingVerdict.Wait,
            CompletionSeamGuard.StreamedLeadingWord("Hi ", "\"", Fixed(SpellingAssessment.Known)));
    }

    [Fact]
    public void StreamedConnectorContinuationAllowsImmediately()
    {
        // "don" + "'t" continues the caret word; never buffer it.
        Assert.Equal(StreamedLeadingVerdict.Allow,
            CompletionSeamGuard.StreamedLeadingWord("don", "'t",
                Only(correctable: "\u0000NeverMatch", uncorrectable: "don't")));
    }

    [Fact]
    public void StreamedEmptyCompletionWaits()
    {
        Assert.Equal(StreamedLeadingVerdict.Wait,
            CompletionSeamGuard.StreamedLeadingWord("Hi ", "", Fixed(SpellingAssessment.Known)));
    }

    [Fact]
    public void StreamedPunctuationOnlyCompletionIsNotApplicable()
    {
        // A completed token with no letters at all cannot be a misspelling.
        Assert.Equal(StreamedLeadingVerdict.Allow,
            CompletionSeamGuard.StreamedLeadingWord("Hi ", "5+", Fixed(SpellingAssessment.CorrectableTypo)));
    }
}
