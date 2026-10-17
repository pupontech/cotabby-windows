# Confidence gating

**Target phase:** 2 (after the statistics funnel exists to measure it).
**Upstream provenance:** none. **Risk level:** Medium — a bad threshold
silently suppresses good suggestions. **Flag:** `EnableConfidenceGating`
(default `false`) + `ConfidenceThreshold` (default `0.5`, tunable 0–1).

## Why

The engine normalizes once and surfaces whatever survives
(`LlamaSuggestionEngine.GenerateAsync` `:97-116`); quality control is
per-signal — constrained decode stops (`LlamaRuntimeCore` `:184-222`),
prompt-echo strip and trailing-duplication filter
(`SuggestionTextNormalizer`), the pre-insertion degenerate-run firewall
(`SuggestionCoordinator.cs:1297-1303`). None of these produces a
*confidence* estimate: a low-quality suggestion (weak logits, near-tie
candidates, prompt echo barely stripped) is presented exactly like a
strong one. Users perceive this as noise and stop trusting ghost text.
The missing piece is a cheap, deterministic confidence score computed
from signals the engine already has, gated behind an off-by-default flag
and tuned with the Phase 4 `benchmark-cli`.

## What Changes

- New pure `Cotabby.Core.Suggestions.SuggestionConfidenceScorer`:
  combines (a) the selected-token's logit margin over the runner-up
  (exposed from `LlamaRuntimeCore.SelectToken` `:248-294` as an optional
  per-generation aggregate), (b) normalizer outcomes (was echo stripping
  needed? was the duplication filter triggered?), (c) stop reason
  (`LlamaRuntimeCore.cs:184-222` — `no_admissible_token` and early
  `budget` stops score low), into a 0–1 score. Deterministic; no model
  internals beyond the aggregate margin.
- `LlamaSuggestionEngine` attaches the score to the final chunk via a new
  `SuggestionChunk.Confidence` field (default 1.0 when unknown — existing
  callers unaffected).
- `SuggestionCoordinator` gates presentation: when
  `EnableConfidenceGating` is on and score < `ConfidenceThreshold`, the
  overlay is not shown and the funnel records `SuppressedLowConfidence`
  (Phase 1 `suggestion-statistics-funnel`).
- The score is logged per request (count-safe: number only, no text) so
  the threshold can be tuned from real logs.
- `AppSettings.EnableConfidenceGating` (default `false`),
  `ConfidenceThreshold` (default `0.5`), surfaced in the Advanced pane
  with a live "last score" readout.

## Non-goals

- No learned/statistical confidence model (no training, no persistence of
  scores beyond the aggregate counters).
- No per-app thresholds (composes with `per-app-profiles` later).
- No change to the existing normalizer/suppression pipeline — gating is
  an additional, independent gate.

## Proposal outline (proposal.md)

```markdown
## ADDED Requirements

### Requirement: Low-confidence suggestions can be suppressed

The app MAY, when the user enables confidence gating, suppress suggestions
whose computed confidence is below the configured threshold. The score
MUST be deterministic and derived from signals already available to the
engine. Suppressed suggestions MUST NOT reach the overlay and MUST be
counted in the statistics funnel without storing suggestion text.

#### Scenario: Weak suggestion is gated
- **WHEN** gating is enabled and a suggestion scores below the threshold
- **THEN** no ghost text is shown and the funnel counts the suppression

#### Scenario: Strong suggestion is unaffected
- **WHEN** gating is enabled and a suggestion scores at or above threshold
- **THEN** it is presented exactly as without gating

#### Scenario: Gating off changes nothing
- **WHEN** gating is disabled
- **THEN** every surviving suggestion is presented (v0.4.1 behavior)
```

## Source files touched (verified)

| File | Change |
| --- | --- |
| `src/Cotabby.Core/Suggestions/SuggestionConfidenceScorer.cs` | **new** — pure scoring |
| `src/Cotabby.Inference/LlamaRuntimeCore.cs:184-240` | expose logit-margin aggregate from decode |
| `src/Cotabby.Inference/LlamaSuggestionEngine.cs:97-116` | attach score to final chunk |
| `src/Cotabby.Core/Suggestions/SuggestionRequest.cs:39-40` | `SuggestionChunk.Confidence` (default 1.0) |
| `src/Cotabby.Core/Suggestions/SuggestionCoordinator.cs:587-663` | gate overlay show/update on threshold |
| `src/Cotabby.App/Settings/AppSettings.cs` | add `EnableConfidenceGating`, `ConfidenceThreshold` |
| `src/Cotabby.App/Hosting/AppHost.cs:133-169` | push settings to coordinator |
| `src/Cotabby.App/UI/SettingsWindow.xaml.cs:932-970` | Advanced-pane threshold + last score |
| `tests/Cotabby.Core.Tests/SuggestionConfidenceScorerTests.cs` | **new** |

## Risks

- Threshold mis-tuning suppressing legitimate suggestions: default off,
  funnel measures the `Presented` delta, and the Phase 4 benchmark CLI
  produces score histograms per corpus so the threshold is set from data.
- Margin aggregate changes decode hot path: computed inside the existing
  per-token loop from already-selected tokens (no extra logits pass).
- Score must never gate *insertion* safety (the degenerate firewall stays
  independent and always-on).

## Test plan (unit)

`tests/Cotabby.Core.Tests/SuggestionConfidenceScorerTests.cs`:

- `StrongMarginCleanNormalization_ScoresHigh`
- `WeakMargin_ScoresLow`
- `EchoStrippedOutput_ScoresLower`
- `DuplicationFilterTriggered_ScoresLower`
- `NoAdmissibleTokenStop_ScoresLow`
- `SentenceBoundaryStop_ScoresUnaffected`
- `ScoreIsDeterministicForSameInputs`
- `ThresholdComparison_GatesAtConfiguredValue`

Coordinator-level (existing test pattern with fake engine):

- `GatingEnabledLowScore_OverlayNotShown`
- `GatingEnabledHighScore_OverlayShown`
- `GatingDisabled_AllSurvivingSuggestionsShown`

## Manual Windows smoke steps

1. Enable gating at threshold 0.5; type in Notepad across varied contexts;
  confirm weak completions no longer flash and good ones still appear.
2. Watch the Advanced-pane "last score" readout across contexts to sanity
  -check the distribution.
3. Lower threshold to 0.1 (near-off) and raise to 0.9 (near-always-off);
  confirm monotonic behavior.
4. Check the statistics funnel: `Presented` should drop while
  `SuppressedLowConfidence` rises — no text in either counter.
5. Disable → behavior identical to v0.4.1.

## Rollback

- `EnableConfidenceGating=false` → scorer result is ignored entirely;
  `SuggestionChunk.Confidence` defaults to 1.0 and gates short-circuit.
- Code revert removes the scorer and the gate; the decode margin
  aggregate has no other consumers.

## Privacy & security constraints

- Scores are floats derived from already-computed logits/normalizer
  outcomes; no new text reads, no persistence beyond the aggregate
  counters.
- Gating runs *after* the credential-field gate (`TriggerGeneration`
  `:460`): a suppressed suggestion in a credential field is impossible
  because no suggestion is ever created there.
- No cloud calls; scoring is local arithmetic over engine signals.
- Funnel entries remain count-only (Phase 1 contract).
