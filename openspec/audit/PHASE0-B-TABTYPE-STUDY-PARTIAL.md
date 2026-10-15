# Phase 0-B — TabType study: portable algorithms and C# adaptation notes

(`/root/Projects/TabType`, v2.1.0, `b75ea0e`) to identify which suggestion
algorithms are portable to Cotabby for Windows, what Cotabby already has, and
how each would be shaped in the existing `Cotabby.Core` / `Cotabby.Inference`
architecture. **No C# code is written here** — this is the study document that
later change proposals (`PHASE0-CHANGE-*.md`) and implementations consume.

**Companion documents.** `PHASE0-CHANGE-INDEX.md` (the 16 proposed changes and
their phases), `PHASE0-CHANGE-confidence-gating.md`,
`PHASE0-CHANGE-beam-search.md`, `PHASE0-CHANGE-token-healing.md`,
`PHASE0-CHANGE-prediction-cache.md`, `PHASE0-CHANGE-benchmark-cli.md` already
sketch some of what follows; this document supplies the upstream grounding,
exact constants and the pitfalls those sketches don't cover.

---

## 0. Orientation: what TabType actually is

TabType is a macOS 14+ autocomplete app over **llama.cpp + a base (non-chat)
model**, presenting dimmed inline "ghost text" and accepting it word-by-word on
Tab. Three Swift targets matter:

| Target | Contents | Cotabby analogue |
| --- | --- | --- |
| `Sources/TabTypeKit` | runtime, decoder, prompting, eval, personal index, font fitter — **all platform-free except Accelerate/CoreText/CoreGraphics** | `Cotabby.Core` + `Cotabby.Inference` |
| `Sources/TabType` | macOS app: `Engine` orchestrator, AX bridge, screen capture/OCR, overlay, settings | `Cotabby.App` + `Cotabby.Win32` |
| `Sources/TabTypeEval` | offline benchmark CLI over the *same* Kit code | `tools/Cotabby.Tools.Benchmark` (proposed) |

The pipeline (`README.md:229-237`):

```
KeystrokeMonitor (listen-only tap) + HotKeyCenter (Tab etc. while a ghost is up)
   → ContextReader + local field copy / ScreenContextProvider (OCR above the field)
   → PromptAssembler + ModelTemplate
   → InferenceEngine / LlamaRuntime (llama.cpp, prefix-cached KV, 9-wide beam)
   → CompletionDecoder (token healing, phrase search, confidence gate)
   → SuggestionSession → SuggestionOverlay (+ lookahead)
```

**The single most important architectural fact for the port:** TabType's decoder
operates on **raw logits with true probabilities**, not on sampled text. Every
quality decision (the confidence gate, the extension bar, the hint bonus, the
beam ranking) is a comparison of normalized log-probabilities. Cotabby's
`LlamaRuntimeCore.Generate` is an *unscored* constrained argmax sampler: it
never reads a log-probability, so it cannot reproduce any of the gating. This is
the fork in the road that most of the sections below address.

---

## 1. Token healing

### (a) Algorithm

`Sources/TabTypeKit/Decoding/TokenHealing.swift` — `HealSplit.split(text)`
splits the text-to-continue into a prompt that ends on a **natural token
boundary** plus the bytes the model must regenerate:

- trailing `\n` / `\r` / `\t` → nothing to heal (`HealSplit(prompt: text, heal: [])`).
- last char is `" "` → prompt drops that one space, `heal = [0x20]`.
- mid-word → the trailing run of non-whitespace is `partial`; exactly **one
  preceding space is folded into the heal**, mimicking how BPE tokenizers build
  `" proposal"` as one token:

```swift
if start > text.startIndex, text[text.index(before: start)] == " " {
    let spaceIndex = text.index(before: start)
    return HealSplit(prompt: String(text[..<spaceIndex]), heal: Array(" \(partial)".utf8))
}
return HealSplit(prompt: String(text[..<start]), heal: Array(partial.utf8))
```

- `maxHealCharacters = 24`: longer runs (URLs, hashes) are left in the prompt —
  constraining on them "buys nothing".
- Rationale in the doc comment: a prompt ending in `"… the "` or `"… the propo"`
  ends on a token the model rarely saw in training and predicts badly.

**Constrained decode.** `CompletionDecoder.Session` (Stage A) restricts the
first generated tokens to the heal set via `VocabIndex.tokens(consistentWith:)`
(`Inference/VocabIndex.swift:36-44`):

```swift
public func tokens(consistentWith required: ArraySlice<UInt8>) -> [TokenID] {
    guard let first = required.first else { return [] }
    return byFirstByte[Int(first)].filter { id in
        let piece = pieces[Int(id)]
        return piece.count <= required.count
            ? required.starts(with: piece)
            : piece.starts(with: required)
    }
}
```

`VocabIndex` precomputes `byFirstByte: [[TokenID]]` (256 buckets) so the
constraint only scans tokens that can possibly match. The heal is enforced by
`Session.choose(for:)` (`CompletionDecoder.swift:538-550`): while
`pendingHeal` is non-empty, the only candidate is the top-1 token **within the
consistent set**, and its log-probability is renormalized **over that set**
(`LogitMath.logSumExp(logits, over: allowed)`), not over the whole vocabulary —
that renormalization is what makes `confidence` a meaningful "P(first word |
typed partial word)".

**Fallback:** if no single token can start the heal bytes (exotic scripts),
`complete()` re-splits unhealed and continues the raw text
(`CompletionDecoder.swift:172-176`). Healing never hard-fails.

### (b) macOS-specific vs portable

Entirely portable — the only non-portable bits are Accelerate (`vDSP_maxvi` for
argmax, `LogitMath.logSumExp`). Both have trivial .NET equivalents
(`MathF`, a hand-written max/argmax over `ReadOnlySpan<float>`, and a
log-sum-exp using `MemoryMarshal`/`Vector<float>`).

### (c) Cotabby today

Cotabby has a *weaker, post-hoc* version:

- `Cotabby.Core/Support/MidWordContinuationPolicy.cs` — one line of logic:
  force continuation only when `preceding[^1]` and `trailing[0]` are both
  alphanumeric. It has no notion of the typed partial word's *bytes*, no
  renormalization, and no fold of the preceding space.
- `Cotabby.Inference/LlamaRuntimeCore.SelectToken` `forceWordContinuation`
  (`:290`): the first-step filter rejects tokens whose first byte isn't
  `[a-zA-Z0-9'-]` or `≥0x80`. That's a *necessary* condition to continue a word,
  not a *sufficient* one — it cannot force the model to emit *the specific*
  partial the user typed.
- `BaseCompletionPromptRenderer.TrimTrailingWhitespace` strips trailing
  whitespace from the prefix, so a boundary caret gets a clean word start — the
  same intent as healing a boundary, achieved differently (and it throws away
  the space, so a suggestion at a boundary can never begin with a space).
- `SuggestionTextNormalizer` then *repairs the aftermath* (strip echo prefix,
  collapse doubled leading space) rather than preventing it.

Net effect measured upstream: without healing, **mid-word recall is 6.2%**
(greedy Phase-0 spike) vs **67.2%** for the v2 decoder on `seed-v1`
(`eval/README.md:171-181`). Cotabby's mid-word quality is in the same
un-healed regime.

### (d) C# adaptation sketch

New file `src/Cotabby.Core/Support/HealSplit.cs` — a direct port, no
dependencies:

```csharp
public readonly record struct HealSplit(string Prompt, byte[] Heal);

public static class HealSplitter
{
    public const int MaxHealCharacters = 24;
    public const byte Space = 0x20;

    public static HealSplit Split(string text) { /* same three branches */ }
}
```

New file `src/Cotabby.Inference/Vocab/ConsistentTokenSet.cs` (or extend the
vocab wrapper) holding `pieces: byte[][]`, `blocked: bool[]`,
`byFirstByte: int[][]`, and:

```csharp
public IReadOnlyList<int> TokensConsistentWith(ReadOnlySpan<byte> required);
public bool StartsWithWhitespace(int id);
public bool ContainsNewline(int id);
public bool IsEndOfGeneration(int id);
```

`LlamaRuntimeCore` grows a logits-aware decode path (see §2) — the constraint is
applied *inside* `SelectToken`, not after it. `LlamaGenerationOptions` gains
`ReadOnlyMemory<byte> HealBytes` and `IReadOnlyList<int> AllowedTokenIds`
(the pre-filtered heal set), so the existing `TopK`/`SelectToken` shape is
preserved and the hot path stays allocation-light.

**Interaction with FIM.** Cotabby's `BuildFimTokens`
(`LlamaRuntimeCore.cs:77-94`) is used when a meaningful suffix exists. Healing
and FIM are mutually exclusive at a boundary (FIM needs the suffix to be real
text after the caret; healing assumes the caret ends the text). Upstream TabType
has no FIM at all — its `afterCursor` is only used for the suffix-overlap trim
(`Engine.trimSuffixOverlap`, `:1315-1328`) and for deciding whether wrapping is
allowed. Recommendation: heal only on the non-FIM path (which is what
`useFim == false` already selects).

### (e) Pitfalls

- **Renormalization is the point.** Computing "P(token) over the full vocab"
  and then restricting to the heal set produces a confidence that is
  meaningless (it is P over the wrong measure) and would gate almost
  everything out. Port `LogSumExp` exactly, including the `blocked`
  subtraction.
- **`maxHealCharacters` is a real limit.** Constraining 25+ byte runs (URLs,
  base64, hashes) produces token sets where the model's true top choice is
  filtered out and the heal constraint silently forces a bad token. Upstream
  deliberately opts out above 24.
- **Never heal after `\n`.** Upstream returns no heal for `\n`/`\r`/`\t`
  terminators. Getting this wrong makes the model try to reproduce a newline it
  isn't allowed to emit (it's in `blocked`/newline-filtered), which yields
  "nothing".
- **`expectedLimit` / surrogate safety.** Cotabby cuts the prefix at 1000 chars
  (`SuggestionRequestFactory.MaxPrefixChars`) and already trims surrogate edges
  in the *prompt* (`BaseCompletionPromptRenderer.TrimSurrogateEdges`). Healing
  operates on the *typed tail*, so it is unaffected — but if the healer ever
  touches the prompt boundary, it must not reintroduce a split pair.
- **Determinism with `KnownPrecedingText`.** `SuggestionSession.KnownPrecedingText`
  can lag UIA (`SuggestionSession.cs:33-41`). When deciding whether a decoded
  fragment "matches the partial", prefer *dropping* the fragment over *inserting*
  a separator — dropping costs the user one character, inserting corrupts text.

---

## 2. True probabilities, the confidence gate, and the split bar

### (a) Algorithm

**Logits → probability.** `Sources/TabTypeKit/Decoding/LogitMath.swift`:

```swift
public static func logSumExp(_ logits: Logits, blocked: [Bool]?, scratch: inout [Float]) -> Float {
    // max, exp(values - max), sum, subtract blocked mass, return max + log(sum)
}
```

Two variants: over the whole vocabulary (with `blocked` control/EOG tokens
subtracted so their mass lowers every other probability — `:24-28`) and over a
restricted `ids` set (used for heal renormalization). `topK` is an insertion
sort over a fixed-size window (`:43-60`), which for k ≤ 9 on a 150k vocab is far
cheaper than a full sort.

**The gate lives in the engine, not the decoder.**
`Decoding/CompletionDecoder.swift:28-31` is explicit:

> "Recommended minimum first-word confidence … The decoder itself never gates —
> callers compare `CompletionResult.confidence` against this."

`Sources/TabType/Core/Engines/LlamaEngine.swift:74-86` is where it fires:

```swift
let midWord = request.beforeCursor.last.map { $0.isLetter || $0.isNumber } ?? false
var threshold = options.showThreshold * (midWord ? 0.4 : 0.75)
if result.followsHint && hintSupport >= 2 {
    threshold *= AppSettings.shared.personalization.repeatedPhraseBar
}
guard result.confidence >= threshold else { … return nil }
```

The **split bar** is the interesting part. A single threshold was measured to be
wrong in both directions: mid-word completions (finishing a word) are right
**53–65%** of the time when shown, while boundary guesses (the next word after a
space) are right only **21–32%** (`eval/README.md:65-69`). So the effective bars
are **0.08 mid-word** and **0.15 at a boundary** — derived from the catalog's
`showThreshold` (0.20 for base models, 0.25 for Qwen3-0.6B, 0.60–0.75 for
instruct models) by the 0.4 / 0.75 factors. Versus a flat 0.10/0.15 pair:
"same characters saved, fewer wrong ghosts on both their writing (42.0 → 37.4%)
and seed-v1 (30.7 → 28.6%)".

**Catalog thresholds** (`Sources/TabTypeKit/Catalog/models.json`, version 3):

| id | template | showThreshold | extensionThreshold | min RAM GB |
| --- | --- | --- | --- | --- |
| `qwen3-0.6b-base` | base | 0.25 | 0.05 | 8 |
| `qwen3-1.7b-base` | base | 0.20 | 0.05 | 8 |
| `qwen3-4b-base` | base | 0.20 | 0.05 | 16 |
| `qwen3-4b-instruct` | chatml | 0.60 | 0.05 | 16 |
| `gemma-4-e2b-base` | base | 0.20 | 0.05 | 16 |
| `gemma-4-e2b-it` | gemma4 | 0.75 | 0.05 | 16 |
| `gemma-4-e4b-base` | base | 0.20 | 0.05 | 24 |

Per-model thresholds exist because **instruct models are overconfident** (peaky
distributions) and "at equal noise they must be gated much harder and lose ~10
points of recall" (`eval/README.md:146-149`). This is directly relevant to
Cotabby: its catalog is Qwen3.5/Gemma-4 and its decode is a base-model
continuation, so the base-model thresholds are the transferable ones.

**The ungated→gated tuning loop** (`Sources/TabTypeEval/main.swift:139-148`,
`EvalRunner.sweep`): run once *ungated*, record every `confidence`, then
re-score offline at each threshold by blanking the suggestions below it. This
means you tune the gate from **one** model pass, not one pass per candidate
threshold:

```swift
let gated = run.results.map { r -> CaseResult in
    guard let c = r.confidence, c < t else { return r }
    var hidden = r
    hidden.suggestion = ""
    hidden.score = EvalScorer.score(suggestion: "", truth: r.truth)
    return hidden
}
```

### (b) macOS-specific vs portable

Fully portable. The only Accelerate dependency is `vDSP` max/argmax.

### (c) Cotabby today

**No equivalent.** A grep of `src/` and `tests/` for `confidence|Confidence|
logSumExp|probability|beam` returns nothing. `LlamaRuntimeCore.SelectToken` is
a pure argmax over the top-20 raw logits with `RepetitionGuard` and a
control-token mask; it never normalizes, so there is no number to gate on.
Quality control is instead done by **post-hoc text filters**
(`SuggestionTextNormalizer`: echo strip, scaffolding-label strip,
`TrailingDuplicationFilter`, `InsertionSafetyGate`) — necessary, but they reject
*wrong-looking* text, not *unlikely* text. The measured cost upstream is the
**wrong-show rate**: the Phase-0 greedy llama.cpp spike had
**66.7% wrong-show** vs the gated v2 decoder's **25.0%**
(`eval/README.md:171-181`).

Also absent: any per-model threshold. `ModelCatalog.CotabbyModel` has
`ContextLength` and `SupportsFillInMiddle` but no prompt/threshold metadata —
contrast TabType's `Entry.template` / `showThreshold` / `extensionThreshold`
triple, which is what makes `LlamaEngine`'s prompt and gate per-model.

### (d) C# adaptation sketch

1. **`src/Cotabby.Inference/Decoding/LogitMath.cs`** — static class:
   ```csharp
   public static float LogSumExp(ReadOnlySpan<float> logits, bool[]? blocked);
   public static float LogSumExp(ReadOnlySpan<float> logits, ReadOnlySpan<int> ids);
   public static float LogProb(ReadOnlySpan<float> logits, int id, bool[]? blocked);
   ```
   Use `MemoryMarshal.Cast<float, Vector<float>>` lanes + `Vector.Sum`; fall
   back to a scalar loop for the tail. Keep a reusable `float[] scratch` per
   runtime instance (upstream threads `scratch` through to avoid per-call
   allocation — at 150k floats × per-token that is the difference between a
   smooth and a GC-thrashing decode).

2. **`src/Cotabby.Inference/Decoding/CompletionResult.cs`** — record carrying
   `Text`, `Confidence` (first-word probability), `Words` (`IReadOnlyList<WordScore>`),
   `PromptTokens`, `GeneratedTokens`, `FollowsHint`.

3. **`src/Cotabby.Inference/Decoding/ConstrainedDecoder.cs`** — the new
   candidate-aware decode path; returns `CompletionResult?`. `LlamaRuntimeCore`
   keeps `Generate` for the existing single-shot path (so nothing regresses) and
   the new path is opt-in per model.

4. **`src/Cotabby.Core/Suggestions/ConfidenceGate.cs`** — pure, testable:
   ```csharp
   public static class ConfidenceGate
   {
       public const double MidWordFactor = 0.4;
       public const double BoundaryFactor = 0.75;

       public static double EffectiveThreshold(
           double catalogShowThreshold, bool isMidWord,
           bool followsHint, int hintSupport, double repeatedPhraseBar);
   }
   ```
   Called from `LlamaSuggestionEngine.GenerateAsync` between
   `core.Generate*` and `SuggestionTextNormalizer.Normalize` — gate **before**
   normalization, because normalization is pure cleanup and the gate is a
   quality decision on model output.

5. **`ModelCatalog.CotabbyModel`** gains `ShowThreshold` (`double?`, null = no
   gate, preserving current behavior) and later `ExtensionThreshold`. Additive
   with a safe default, per the cross-change constraint in
   `PHASE0-CHANGE-INDEX.md:74-77`.

6. **Settings**: `AppSettings.ConfidenceGatingEnabled` (default `false`),
   `ConfidenceGateOverride` (`double?`) for A/B. Off by default so v0.4.1
   behavior is the fallback.

7. **`ISuggestionEngine`/`SuggestionChunk`** — the chunk contract should gain an
   optional confidence so the benchmark tool can record it. Note Cotabby's
   engine yields *two* chunks (`Text` then final-empty, `LlamaSuggestionEngine.cs:115-116`);
   carrying confidence on the non-final chunk is enough.

### (e) Pitfalls

- **Blocked-token mass.** `LogSumExp` with `blocked: nil` includes EOS mass and
  lowers every probability; with `blocked` it doesn't. TabType uses
  `blocked: nil` for full-vocab normalizers (honest absolute probability) and
  the restricted variant for heal sets. Mixing them up silently shifts the gate
  by a constant factor that looks like a tuning problem.
- **Don't gate on `P(word)` when you healed.** The confidence is conditional on
  reproducing the typed partial. It is only comparable across cases because the
  renormalization is consistent. Report it as-is; do not "correct" it.
- **Speculative/lookahead requests must not gate.** Upstream's `quiet` flag
  (`LlamaEngine.swift:80-87`) skips both the gate and the statistics for
  `request.speculative`. If Cotabby later adds lookahead (§7), gating those
  wastes a comparison and pollutes the funnel.
- **Instruct/chat models need much higher bars.** If Cotabby ever adds an
  instruct-format model, the base 0.20 is wrong by ~3×; that is why the catalog
  carries it per model.
- **`confidence` is only meaningful when the decode is deterministic.** Cotabby's
  argmax is deterministic, so this holds. If anyone later adds temperature or
  sampling, confidences become incomparable run-to-run and `sweep`-style offline
  retuning stops working.

---

## 3. Phrase beam search and the extension bar

### (a) Algorithm

`Decoding/CompletionDecoder.swift` — `DecoderOptions` (all defaults):

| Field | Default | Meaning |
| --- | --- | --- |
| `candidates` | 4 | distinct first tokens explored in parallel |
| `minFirstTokenProbability` | 0.02 | first tokens below this are not explored |
| `extensionThreshold` | **0.05** | keep a trailing word while its conditional P ≥ this |
| `maxWords` | 4 | hard cap on words including the first |
| `beamWidth` | **9** | live phrases (Cotypist's number) |
| `beamBranching` | 3 | next tokens considered per phrase per step |
| `beamFromBestFirstWord` | `true` | search only from the model's best first word |
| `showThreshold` | 0.2 | recommended gate (caller-enforced) |
| `maxTokensPerWord` | 8 | runaway-token guard per word |
| `maxAlternatives` | 3 | runner-up first words reported |
| `hint` / `hintBonus` | `[]` / `log(3.0)` | personalization (§4) |

**Stage A — first word, candidates in parallel** (`:257-331`). Fan out `fanOut
= min(candidates, maxSequences - 1)` KV sequences, each forked from sequence 0
(`model.fork(to: seq)`, unified KV cache so forking is cell-sharing not copying),
filter out newline openers, drop first tokens below `minFirstTokenProbability`,
then decode each candidate greedily to its word boundary. At the boundary,
`shouldEndWord` (`:575-583`) records `nextChoices = wordStarts(...)` — the top
`beamBranching` **space-led** tokens (plus newlines, which end the phrase) — and
whether a `stopToken` exists.

**Candidate merging** (`:333-348`): candidates rendering identical visible text
(different tokenizations of one word) are merged by **summing log-probability
mass** with a numerically-stable `logAddExp` (`:621-624`). Ranked with the hint
bonus; the *reported* probability is the true (un-bonused) one.

**Stage B — beam over phrases** (`:405-522`). Each live phrase carries its
sequence id, position, cumulative log-probability, per-word bytes and per-word
log-probabilities, the word in progress, and its proposals. Per step:

1. Expand every phrase × its proposals. A proposal that is a newline, hits the
   word cap, or exceeds `maxTokensPerWord` **commits and finishes** the phrase.
2. **Exact pruning** (`:448-453`) — the key latency trick:
   ```swift
   // A phrase's probability only falls as it grows, so nothing scoring
   // below the best finished phrase can still win — drop it (exact).
   expansions.removeAll { $0.score + (live[$0.parent].followsHint ? bonus : 0) <= bestDone }
   ```
   Upstream measured this as "cut latency from 253 to 147 ms".
3. Keep the top `beamWidth` expansions; release KV for phrases nothing grows
   from (`model.drop(sequence:)`).
4. Branch: the first child **reuses** the parent's sequence; siblings
   `model.copy(sequence:to:)` **before anything is fed** (copy-then-feed
   ordering is load-bearing — see pitfalls).
5. Feed the whole expansion set as **one batch** (`model.decode(entries)`) so
   the beam costs one decode per step, not one per phrase.

**The extension bar** is applied at *reporting* time, not during search
(`:364-369`):

```swift
for (bytes, lp) in zip(phrase.words.dropFirst(), phrase.wordLogProbabilities.dropFirst()) {
    guard exp(lp) >= options.extensionThreshold else { break }
    words.append(WordScore(...))
}
```

So the beam always searches the full phrase; the bar only decides how much is
*shown*. Crucially, `lp` is each word's **conditional** probability given the
words before it, not the joint.

**Measured behaviour** (`eval/README.md:99-110`, author's 867-case private set):

| extension bar | words shown | multi-word | chars/case | fully right | wrong words/case |
| --- | --- | --- | --- | --- | --- |
| 0.2 (old default) | 1.37 | 22% | 1.37 | 27.9% | 0.98 |
| 0.1 | 1.79 | 41% | 1.42 | 20.5% | 1.39 |
| **0.05 (default)** | **2.20** | **57%** | **1.46** | **15.2%** | **1.79** |
| 0 (uncapped) | 2.47 | 69% | 1.47 | 11.5% | 2.06 |

The first word is identical at every bar. The reasoning is that Tab accepts one
word at a time, "so a wrong trailing word only costs a glance" — and 0.5 "made
almost every suggestion a single word" because the next word rarely clears 0.5.

**Beam topology** (`eval/README.md:112-123`, 4 words · bar 0.2, seed-v1):

| strategy | chars/case | recall | fully right | wrong words/case | p50 |
| --- | --- | --- | --- | --- | --- |
| greedy extension of best first word | 3.62 | 58.3% | 39.2% | 0.95 | 90 ms |
| beam of 9 over **all** first words | 3.24 | 54.7% | 51.4% | 0.64 | 232 ms |
| **beam of 9 from the best first word** | **3.42** | **58.3%** | **48.4%** | **0.72** | **147 ms** |

Letting the beam also replace the first word cost **3.6 points of next-word
recall** — hence `beamFromBestFirstWord = true`. The first word is what the
confidence gate judges, so it must stay the model's argmax.

### (b) macOS-specific vs portable

The search is portable. The **KV-sequence primitives it depends on are the
porting risk**: `fork(to:)`, `copy(sequence:to:)`, `drop(sequence:)`,
`decode(_ entries:)` with per-entry `(token, position, sequence)`, and
`n_seq_max > 1` on the context. See (e).

### (c) Cotabby today

No beam, no probabilities, no per-word scores. `LlamaRuntimeCore.Generate` is a
single-sequence constrained argmax loop:

```csharp
var token = SelectToken(logits, options.TopK, blocked,
    forceWordContinuation: options.ForceWordContinuation && step == 0, scratch);
```

stopping on EOS / newline / `SentenceBoundaryClassifier.EndsSentence` /
token budget. `TopK = 20` is a *sampling pool*, not a beam width. Result
handling is whole-string: `SuggestionTextNormalizer.Normalize` runs once, one
non-final chunk is emitted, and the coordinator's streaming machinery is
effectively unused (`LlamaSuggestionEngine.cs:16-21` says so explicitly).

Consequence: Cotabby's "words" only exist as a *consequence of decoding*, split
at acceptance time by `SuggestionSessionReconciler.NextAcceptanceChunk` /
`NextAcceptancePhrase`. There is no per-word probability, so no extension bar is
possible.

Also note: Cotabby's completion-length presets
(`AppSettings.CompletionLengthPreset` → `(5,9,15,25)` token budgets) are a
**token** budget, whereas TabType's `maxWords` is a **word** budget with a
separate `maxTokensPerWord`. Those are different knobs and both are needed.

### (d) C# adaptation sketch

New file `src/Cotabby.Inference/Decoding/PhraseBeamSearch.cs`:

```csharp
public sealed record DecoderOptions
{
    public int Candidates { get; init; } = 4;
    public double MinFirstTokenProbability { get; init; } = 0.02;
    public double ExtensionThreshold { get; init; } = 0.05;
    public int MaxWords { get; init; } = 4;
    public int BeamWidth { get; init; } = 9;
    public int BeamBranching { get; init; } = 3;
    public bool BeamFromBestFirstWord { get; init; } = true;
    public double ShowThreshold { get; init; } = 0.2;
    public int MaxTokensPerWord { get; init; } = 8;
    public int MaxAlternatives { get; init; } = 3;
    public ReadOnlyMemory<byte> Hint { get; init; }
    public double HintBonus { get; init; } = Math.Log(3.0);
}
```

with internal `Candidate`, `Phrase` and `Choice` structs mirroring the Swift
ones, driven through a new abstraction over the runtime:

```csharp
public interface ISequenceModel          // Cotabby.Inference/Decoding
{
    VocabIndex Vocab { get; }
    int MaxSequences { get; }
    int[] Tokenize(string text, bool addSpecial);
    void EvaluatePrompt(ReadOnlySpan<int> tokens);      // seq 0, logits after last
    void Fork(int sequence);
    void Copy(int source, int target);
    IReadOnlyList<ReadOnlySpan<float>> Decode(IReadOnlyList<BatchEntry> entries);
    void Drop(int sequence);
}
```

`LlamaRuntimeCore` implements it on top of the LLamaSharp `SafeLlamaContextHandle`
(`MemorySequenceCopy`/`MemorySequenceRemove`, `Decode(LLamaBatch)` with per-token
`seq_id`). This also gives the unit tests a fake (`FakeModel` exists upstream as
`Tests/TabTypeKitTests/FakeModel.swift` and is how the decoder's 22 tests run
without a GGUF) — a `ScriptedSequenceModel` in
`tests/Cotabby.Core.Tests` would let `PhraseBeamSearchTests` run with no model.

Placement per the cross-change constraint: the search itself is
`Cotabby.Inference` (it needs logits), but the *policy* around it
(`ExtensionThreshold`, `MaxWords`, gating) is `Cotabby.Core` so it's testable.

### (e) Pitfalls

- **`n_seq_max` and LLamaSharp.** `beamWidth = 9` plus candidates means
  `maxSequences ≈ 20` (upstream `LlamaRuntime.Configuration.maxSequences = 20`,
  chosen as "prompt + twice the decoder's beam"). With `kv_unified = true`
  forking is cell-sharing. **If LLamaSharp's context is created with the default
  `n_seq_max = 1`, every `fork`/`copy` silently does nothing useful** and the
  beam produces garbage. Verify `SafeLlamaContextParams` exposes
  `seq_max`/`n_seq_max` before committing to this port; this is the single
  biggest feasibility risk in the whole document.
- **Copy-before-feed ordering.** `CompletionDecoder.swift:461-473` claims all
  needed free sequences and copies *before* feeding any token. Feeding first and
  copying after would copy a *diverged* state.
- **Exact pruning depends on monotone log-probability.** Phrases only lose
  probability as they grow, so pruning against `bestDone` is safe. Add the hint
  bonus to *both* sides of the comparison (upstream does:
  `live[$0.parent].followsHint ? bonus : 0`) or a hint-following phrase gets
  wrongly pruned.
- **Per-entry logits retrieval.** `decode(_:)` returns one `Logits` per entry
  via `llama_get_logits_ith(context, i)`. Cotabby currently decodes one token at
  a time in the generation loop and keeps `lastLogitsIndex` — batching N tokens
  and indexing per entry is a behaviour change that must be tested against the
  real runtime (LLamaSharp's `LLamaBatch` + `GetLogitsIth`).
- **Cancellation.** Upstream polls `isCancelled()` between beam steps and throws
  `CancellationError`, and the `InferenceEngine` supplies
  `{ !gate.isCurrent(requestID) }` (`InferenceEngine.swift:124-135`). Cotabby's
  equivalent must poll `ct` inside the beam loop, not only at the batch
  boundary, or a superseded request keeps burning GPU for the full beam.
- **`maxWords` vs token budget.** Don't map the beam onto
  `AppSettings.CompletionLengthPreset`'s token numbers. Add a separate word cap;
  the two compose (word cap bounds shape, token cap bounds cost).
- **Latency reality check.** 147 ms p50 for a 4-word beam on a 2.5 GB Qwen3-4B
  with Metal. Cotabby runs Vulkan/CPU on consumer Windows GPUs — expect the beam
  to cost more per step here, which makes the exact pruning and the single-batch
  feeding non-negotiable rather than optimizations.

---

## 4. Personalization: the suffix index and the hint

### (a) Algorithm

**Storage.** `Sources/TabType/Core/WritingStore.swift` — opt-in; documents
(messages keyed by UUID, fields keyed by `bundleId|windowTitle`) sanitized
through `SecretSanitizer`, minimum length 20 chars, capped at 1,000,000
characters, **AES-GCM encrypted** with a 32-byte key file at mode `0600`
(`:166-177`). Rebuilt/merged for iCloud sync by `updated` timestamp.

**Retrieval.** `Sources/TabTypeKit/Personalization/SuffixIndex.swift` — a
suffix array over the author's own writing:

- Documents joined with separator byte `0x01`; two parallel byte arrays, `text`
  (normalized, original casing) and `lower` (ASCII-lowercased).
- Suffixes sorted with `memcmp` over at most `compareLength = 64` bytes.
- `continuation(after typed, minimumMatch: 10, maxWords: 4)`:
  - Normalize + lowercase the typed tail; candidate tail lengths from
    `count - minimumMatch` down to 0, each starting right after a space, capped
    at 60 bytes of pattern.
  - Two binary searches (`matches(pattern)`, `:88-104`) for the suffix range.
  - For each match, read up to `maxWords` of continuation **in the original
    casing** (`continuationText`, `:106-121`).
  - Tally by `firstWordKey` (leading-space-preserving lowercased first word) and
    return the **most common** continuation, with `support` = how many past
    occurrences agree.
- Normalization (`:129-145`): all whitespace → single `0x20`; **trailing space
  preserved** so `"take a look "` continues with the next word.

**Use as a decode hint.** `DecoderOptions.hint: [UInt8]` +
`hintBonus = log(3.0)`. In Stage A, the hint token is added to the candidate
fan-out, replacing the weakest when full (`:271-282`). During decoding,
`forcedChoice` (`:554-561`) picks the top-1 token **within
`tokens(consistentWith: forced)`** but scores it with the **true full-vocab
log-probability** — so the hint steers without inflating confidence:

```swift
/// Follow the author's past phrasing, scored with TRUE probabilities so the
/// confidence stays honest.
mutating func forcedChoice(_ forced: ArraySlice<UInt8>, _ logits: Logits) -> Choice?
```

`completionResult.followsHint` reports whether the winner matched, and in the
beam the hint adds `bonus` to ranking only (`:519-521`).

**Gate interaction.** `LlamaEngine.swift:78`: `if result.followsHint &&
hintSupport >= 2 { threshold *= repeatedPhraseBar }` — a phrase the author has
used repeatedly earns a *lower* bar. `AppSettings.personalization` maps
`personalizationStrength` to `(bonus, repeatedPhraseBar)`:

| strength | bonus | repeatedPhraseBar |
| --- | --- | --- |
| gentle | `log(1.5)` | 0.75 |
| balanced (default) | `log(3.0)` | 0.5 |
| strong | `log(6.0)` | 0.35 |

In the eval backend the same idea appears as a *global* factor rather than a
threshold multiply: `hintFactor = 0.5` with `support >= 2`
(`TabTypeEval/Backends.swift:131`).

**Measured effect** (`eval/README.md:71`): "learning from your writing helps
(+5% characters with only 177 messages)"; README:150 says screen context and
learning are "the levers that move it". The eval harness reports
`hints offered / followed` counts (`main.swift:128-130`).

### (b) macOS-specific vs portable

`SuffixIndex` is **fully portable** (byte arrays, memcmp, binary search — .NET
`Span<T>` and `MemoryExtensions` map 1:1). `WritingStore`'s AES-GCM is portable
via `System.Security.Cryptography.AesGcm`; the macOS-specific part is only the
key-file protection (`completeFileProtection` → DPAPI on Windows).

### (c) Cotabby today

**None, and deliberately so.** `UPSTREAM_PORTING_PLAN.md:37` records the
upstream equivalent (#841 / `c3d17c1`) as **not ported**: "It would create the
first durable on-device store of user text; needs an explicit privacy decision
(encryption/key custody, retention, delete-all, Cotypist import provenance)
first." That is a governance position, not a technical gap, and this study does
not propose to override it. What *is* portable without it:

- **The in-memory conversational buffer.** Upstream `Engine.recentInputs`
  (`Engine.swift:125-132`) keeps the last **3 committed inputs per app,
  in-memory only, sanitized**, because chat fields empty on send and "without
  this the model never sees what the user has been saying". That is a
  *session-scoped* memory with no durable store — a much smaller privacy step.
  Cotabby has no equivalent: `SuggestionRequestFactory.Build` reads only the
  live field text.
- The hint mechanism itself is inert without a store, so there is nothing to
  port in isolation.

### (d) C# adaptation sketch

**Tier 1 (no durable store) — `src/Cotabby.Core/Support/RecentInputMemory.cs`:**

```csharp
public sealed class RecentInputMemory
{
    private readonly Dictionary<string, List<string>> _byProcess = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxPerProcess = 3;
    private const int MaxCharsPerEntry = 300;

    public void Commit(string processName, string text);   // dedupes last, sanitizes
    public IReadOnlyList<string> Recent(string processName);
    public void Clear();
}
```

Wired from the coordinator where it already detects "field emptied" /
focus-left (the `CancelSession("focus changed…")` and
`FieldIdentityProvesOwnership` paths in `SuggestionCoordinator.cs`), and fed
into `SuggestionRequest` as a new optional `RecentInputs` field that
`BaseCompletionPromptRenderer` renders as a `notes`-style context section
(priority 40, maxChars 600 — upstream's `notes` budget). Sanitization first:
a `SecretSanitizer` port is Phase 1 in the existing plan precisely so that new
prompt/log sinks are safe.

**Tier 2 (durable) — only after an owner privacy decision.** Then:
`src/Cotabby.Core/Personalization/SuffixIndex.cs` (direct port of the 148-line
Swift), `WritingStore` with `AesGcm` + DPAPI-protected key,
`PersonalizationSettings` mirroring gentle/balanced/strong, and the
`hint`/`hintBonus` plumbing into the decoder from §2/§3.

### (e) Pitfalls

- **`support >= 2` for the repeated-phrase bar, `>= 1` for the hint itself.**
  A single occurrence is weak evidence; upstream needs two agreements before
  lowering the gate.
- **Never let the hint change `confidence`.** `forcedChoice` deliberately
  normalizes over the full vocabulary while constraining the *choice*. If you
  instead renormalize over the hint-consistent set, every hint-following
  suggestion gets an inflated confidence and the gate becomes meaningless.
- **Case-insensitive match, original-case output.** The index lowercases for
  matching but returns the stored bytes. Matching on the lowercased copy and
  returning it would suggest "I'Ll" style casing damage.
- **Trailing space is load-bearing** (`SuffixIndex.normalize` `:142-143`). Drop
  it and `"take a look "` returns a continuation that starts mid-word or with a
  doubled space.
- **SecretSanitize before storage *and* before prompt use.** Upstream sanitizes
  at `record()` (`WritingStore.swift:48`) and again at read time in
  `LlamaEngine.context(for:)` (`:121-122`). The store is the durable leak.
- **`minimumMatch = 10`** means the index only fires on reasonably long tails —
  short prefixes produce noise matches. Don't lower it to "make it fire more".

---

## 5. Prompt assembly and model templates

### (a) Algorithm

**Templates** (`Sources/TabTypeKit/Prompting/ModelTemplate.swift`). A template
is `(kind, userPrefix, userSuffix, assistantPrefix, reservedMarkers)`. Kinds:
`base` (continue a document) and `chat` (one user turn + an assistant turn
**already opened with the typed text**, so the model continues the author's own
words instead of replying to them). Built-ins: `base`, `chatML`,
`chatMLNoThink` (Qwen3 hybrid thinking: `\n/no_think` + an empty
`` block), `gemma`, `gemma4`, `llama3`, `phi`.
`detect(modelName:chatTemplate:)` (`:93-108`) picks from the filename tokens
first ("base"/"pt"/"pretrain" → base; else the embedded chat template's markers;
`enable_thinking` → chatMLNoThink) and **falls back to `.base`** for unknown
formats because "plain continuation works acceptably even on chat models".

`reservedMarkers` are stripped from all untrusted content (`PromptAssembler.strip`,
`:258-260`) — otherwise a control-token string in OCR'd or pasted text becomes a
*real* control token, because the base prompt is tokenized with
`parseSpecial: true`.

**Budgets** (`PromptBudgets`): typed 2000 chars, screen 1500, clipboard 400,
custom instructions 600; the sectioned variant adds token budgets
prompt 1024 / typed 512 / screen 384 / clipboard 96 / notes 128.

**Layout** (`PromptAssembler`, `:62-114`). Section order is
**stable → volatile** (instructions/app first, screen next, typed text last) so
consecutive keystrokes reuse the cached prompt prefix and a screen refresh only
recomputes the tail — that ordering is a *KV-cache* decision, not a stylistic
one. Truncation keeps the **end** of typed text and the **newest lines** of
screen text, starting the typed window at a word boundary.

**Conversation framing** (`:106-109`, `:176-181`): the author's reply becomes
the next line of the transcript — but **only when `isConversation` AND
`looksLikeTranscript(screen)`** (≥ 50% of non-empty lines match
`^[^:\n]{1,32}: \S`). The comment records the measurement: framing as a
conversation "helped labelled transcripts but hurt OCR text (31.8 → 33.4% chat
recall without it)".

**Sectioned layout is off by default** (`:69-72`): "with our own delimiters it
measured worse on seed-v1 (recall 58.3 → 55.7%); kept for experiments
(`tabtype-eval --sections`)". A valuable negative result — do not port the
`<situation>/<writer>/<clipboard>/<screen>/<text>` XML layout as the default.

**Capture-noise robustness** (`eval/README.md:124-128`): labelling sections and
closing them with a separator before the typed text "did not help reliably
(docs +8 pts recall, email −7 pts), so the plain layout stays". A window's worth
of unrelated text in the context costs ~3 points of precision (73.2 → 70.3%,
wrong-show 21.4 → 24.0%).

**Chat models** (`:185-205`): instruction in the user turn — "I'm typing a text
in <situation>. Continue it exactly where it stops, as I would write it: same
language, tone and formatting. Write only the continuation of my text — never
answer it, comment on it, or address me." — then `assistantPrefix + typedText`.
Note `ModelTemplate.chatMLNoThink.userSuffix = "\n/no_think"`: without it a
hybrid-thinking model spends its budget thinking and returns nothing usable.

### (b) macOS-specific vs portable

Fully portable and the highest-value port in this document, because Cotabby's
`BaseCompletionPromptRenderer` is a *simplified* version of the same idea.

### (c) Cotabby today

`src/Cotabby.Core/Support/BaseCompletionPromptRenderer.cs` is a genuinely good
port of the *base-model* half and its doc comment states the same design
principle: "a base model has no instruction-following channel … an
instruction-blob prompt would leak scaffolding into the ghost text. This
renderer treats the model as a pure text continuer."

What it has: priority-ordered sections (persona 60 / style 55 / language 50 /
notes 40 / prefix 100), per-section char caps, `PreserveStart` vs `PreserveEnd`
truncation, `WindowPrefix(maxWords: 50, maxChars: 1000)`, and
`FitTokenBudget` with a **binary search** per section measured on the whole
serialized prompt including separators and BOS
(`:84-129` — "Token counts can be non-monotonic; use only a measured fit").

What it lacks versus TabType:

| TabType | Cotabby |
| --- | --- |
| Per-model `template` + `showThreshold` + `extensionThreshold` in the catalog | none; one renderer for all models |
| Chat/chatML/gemma/llama3 templates with assistant-turn prefill | none — base only |
| `reservedMarkers` stripped from untrusted content | strips scaffolding labels (`<|im_start|>`-shaped control-token strings) via `SuggestionTextNormalizer`'s ScaffoldingStrip |

*(Recovered content: sections 0–5. The remainder of this study — the eval
harness, the personalization plumbing details, FIM/persistence notes and the
remaining adaptation sketches — was lost when the authoring task was
interrupted mid-write; a follow-up task appends sections 6+ to this file.)*
