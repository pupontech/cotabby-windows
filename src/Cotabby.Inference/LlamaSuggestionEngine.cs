using System.Runtime.CompilerServices;
using Cotabby.Core.Support;
using Cotabby.Core.Suggestions;
using Microsoft.Extensions.Logging;

namespace Cotabby.Inference;

/// <summary>
/// Spell backend contract for the seam guard. The App layer adapts whatever
/// spell checker is available on the machine; when none is registered the
/// engine keeps every word "known" and the guard degrades to its pure
/// junk-run rule.
/// </summary>
public interface ISpellAssessor
{
    /// <summary>Assess one candidate word for the seam guard.</summary>
    SpellingAssessment Assess(string word);
}

/// <summary>
/// LLamaSharp-backed <see cref="ISuggestionEngine"/>. Mirrors the macOS
/// llama path: render a base-model conditioning prompt (persona / language
/// preface + caret prefix), run one constrained greedy decode on the
/// persistent runtime core, normalize the result once, and surface it as a
/// single final chunk.
/// </summary>
/// <remarks>
/// There is intentionally no token streaming: the upstream app presents the
/// whole normalized suggestion at once, because half the quality problems
/// (prompt echo, chat leaks, trailing dups) are only detectable on the full
/// string. Emitting one final chunk keeps the coordinator's streaming
/// interface intact while matching upstream behavior.
/// </remarks>
public sealed class LlamaSuggestionEngine : ISuggestionEngine
{
    private readonly LlamaRuntimeManager _runtime;
    private readonly ILogger<LlamaSuggestionEngine> _logger;

    public LlamaSuggestionEngine(LlamaRuntimeManager runtime, ILogger<LlamaSuggestionEngine> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    public bool IsReady => _runtime.IsReady;

    public async IAsyncEnumerable<SuggestionChunk> GenerateAsync(
        SuggestionRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var core = _runtime.Core;
        if (core is null)
        {
            _logger.LogWarning("Generate called while runtime not ready (req={ReqId}).", request.RequestId);
            yield return new SuggestionChunk(string.Empty, IsFinal: true);
            yield break;
        }

        var options = new LlamaGenerationOptions
        {
            MaxTokens = request.MaxTokens,
            SingleLine = request.SingleLine,
            ForceWordContinuation =
                MidWordContinuationPolicy.ShouldForceContinuation(request.Prefix, request.Suffix),
        };

        // FIM is used only when the caret is genuinely mid-line (real text
        // follows on the same line) AND the model exposes infill markers.
        // Everything else goes through the base continuation prompt.
        bool useFim = core.SupportsFillInMiddle && HasMeaningfulSuffix(request.Suffix);

        int maxPromptTokens = CompletionPromptBudget.ForContext(core.ContextSizeTokens, request.MaxTokens);
        if (maxPromptTokens < 3)
        {
            yield return new SuggestionChunk(string.Empty, IsFinal: true);
            yield break;
        }
        string prompt;
        LLama.Native.LLamaToken[] promptTokens;
        if (useFim)
        {
            prompt = request.Prefix; // for normalization echo checks
            promptTokens = core.BuildFimTokens(request.Prefix, request.Suffix, maxPromptTokens);
        }
        else
        {
            prompt = CompletionPromptBuilder.Build(request, core.ContextSizeTokens,
                text => core.TokenizeText(text, addBos: core.Vocab.ShouldAddBOS).Length);
            if (prompt.Length == 0)
            {
                yield return new SuggestionChunk(string.Empty, IsFinal: true);
                yield break;
            }
            promptTokens = core.TokenizeText(prompt, addBos: core.Vocab.ShouldAddBOS);
        }

        string raw;
        try
        {
            raw = await core.GenerateAsync(promptTokens, options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }

        if (ct.IsCancellationRequested) yield break;

        var normalized = SuggestionTextNormalizer.Normalize(
            raw, prompt, request.Prefix, request.Suffix, request.SingleLine);

        if (normalized.IsSuppressed)
        {
            if (normalized.Reason != SuppressionReason.EmptyGeneration)
            {
                _logger.LogInformation(
                    "Req {ReqId}: suppressed ({Reason}); raw length={RawLength}.",
                    request.RequestId, normalized.Reason, raw.Length);
            }
            yield return new SuggestionChunk(string.Empty, IsFinal: true);
            yield break;
        }

        // Last line of defense before display: junk punctuation runs and
        // mid-word splices that misspell the word being typed read as glitches,
        // so showing nothing beats showing them. The leading-word check is
        // intentionally fail-open for names and jargon: a typo the spell
        // backend can correct is suppressed, unknown-but-uncorrectable
        // vocabulary still passes. The spell lookup runs at most once per
        // generation, in the two cases the guard actually needs it.
        var seamVerdict = CompletionSeamGuard.Verdict(
            request.Prefix, normalized.Text, _spellingAssessment);
        if (seamVerdict != SeamGuardVerdict.Allow)
        {
            _logger.LogInformation(
                "Req {ReqId}: suppressed (seamGuard:{Reason}); raw length={RawLength}.",
                request.RequestId, seamVerdict, raw.Length);
            yield return new SuggestionChunk(string.Empty, IsFinal: true);
            yield break;
        }

        _logger.LogInformation(
            "Req {ReqId}: suggestion length={Length} (raw {RawLen} chars).",
            request.RequestId, normalized.Text.Length, raw.Length);
        yield return new SuggestionChunk(normalized.Text, IsFinal: false);
        yield return new SuggestionChunk(string.Empty, IsFinal: true);
    }

    /// <summary>
    /// Collapses native typo detection and correction availability into the
    /// seam guard's single spelling contract, mirroring the macOS coordinator's
    /// adapter. Windows has no always-on NSSpellChecker equivalent: a null
    /// provider makes every word "known" and the guard degrades to its pure
    /// junk-run rule. The App layer injects an ISpellAssessor when it is
    /// available on this machine.
    /// </summary>
    internal Func<string, SpellingAssessment> _spellingAssessment { private get; set; } =
        _ => SpellingAssessment.Known;

    /// <summary>Injectable spell backend for the seam guard (null = no spell checking).</summary>
    public ISpellAssessor? SpellAssessor
    {
        get => _spellBackend;
        set
        {
            _spellBackend = value;
            _spellingAssessment = value is null
                ? static _ => SpellingAssessment.Known
                : value.Assess;
        }
    }
    private ISpellAssessor? _spellBackend;

    private static bool HasMeaningfulSuffix(string suffix)
    {
        if (string.IsNullOrEmpty(suffix)) return false;
        for (int i = 0; i < suffix.Length; i++)
        {
            char c = suffix[i];
            if (c == '\n' || c == '\r') return false;
            if (!char.IsWhiteSpace(c)) return true;
        }
        return false;
    }

}
