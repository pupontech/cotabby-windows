# Beam search decode

**Target phase:** 4 (must land before `alternatives-popup` consumes its
candidates). **Upstream provenance:** none (the shipped decode is the
upstream *greedy* constrained sampler; beam search is a new Windows
capability). **Risk level:** Medium-High (touches the decode core).
**Flag:** `BeamWidth` (default `1` = today's exact greedy path).

## Why

`LlamaRuntimeCore.Generate` (`src/Cotabby.Inference/LlamaRuntimeCore.cs:117-240`)
runs one deterministic constrained decode — top-K argmax per step — and
returns a single string. Greedy is fast and predictable, but it forecloses
two Phase 4 capabilities: the alternatives popup (needs N ranked
candidates) and better quality on hard contexts (the #1 candidate is
sometimes a dead end the model would recover from two steps later).
llama.cpp already supports beam search natively; the constraint machinery
(control-token masking, no-repeat-3-gram, single-line/newline/sentence
stops) is ours and must be preserved per-beam.

## What Changes

- `LlamaRuntimeCore` gains `GenerateWithBeamAsync(promptTokens, options, ct)`
  returning ranked candidates: runs the same constrained selection with
  beam width `options.BeamWidth` (1 = current path, byte-identical);
  beams share the prefix evaluation, then expand under the existing
  constraints (control-token mask, `RepetitionGuard.BlockedTokens`
  `:191`, newline/sentence/budget stops `:201-221`), scored by summed
  per-step log-probs of selected tokens. Returns candidates ordered best
  -first; `GenerateAsync` becomes the width-1 wrapper (existing callers
  and tests unchanged).
- Constraint parity per beam: each beam carries its own `generatedIds`
  for the repetition guard and its own byte accumulator; the single-line
  and sentence-boundary stops apply per beam.
- `LlamaSuggestionEngine` (`:35-117`) exposes a new
  `GenerateCandidatesAsync` on a widened `ISuggestionEngine` (the existing
  `GenerateAsync` stays, returning the best candidate — no caller
  breakage) so the alternatives popup (Phase 4) and benchmark CLI
  (Phase 4) can request N.
- Normalization/suppression applies to **each** candidate before
  ranking is exposed (a suppressed candidate is dropped, not returned
  weak).
- New `AppSettings.BeamWidth` (default `1`; 1–4; values > 1 are opt-in
  because decode cost scales with width) surfaced in the Advanced pane.
- Funnel (Phase 1) counts beam usage (width, candidates produced) —
  numbers only.

## Non-goals

- No sampling/temperature randomness (beam search stays deterministic —
  the shipped decode is deterministic and debugging depends on it).
- No beam search inside FIM prompts in v1 (base continuation only; FIM +
  beams is a follow-up once the popup proves the UX).
- No width > 4 (cost bound).

## Proposal outline (proposal.md)

```markdown
## ADDED Requirements

### Requirement: Constrained decode can return ranked candidate beams

The app MAY, when the user sets a beam width above 1, decode multiple
ranked candidates under the existing constraints and expose them to
callers. Width 1 MUST reproduce the shipped greedy decode exactly.
Candidates MUST be deterministic, constraint-filtered, and normalized
before exposure.

#### Scenario: Width 1 is unchanged
- **WHEN** beam width is 1
- **THEN** the decode output is identical to the previous release

#### Scenario: Width 3 returns ranked candidates
- **WHEN** beam width is 3
- **THEN** up to 3 constraint-satisfying candidates are returned, best
  first, by summed log-prob

#### Scenario: Suppressed candidate is dropped
- **WHEN** a beam's text fails normalization
- **THEN** it is excluded from the returned candidates

#### Scenario: Constraints hold per beam
- **WHEN** multiple beams decode
- **THEN** each beam independently respects the no-repeat guard, stop
  conditions, and single-line clamp
```

## Source files touched (verified)

| File | Change |
| --- | --- |
| `src/Cotabby.Inference/LlamaRuntimeCore.cs:101-240` | `GenerateWithBeamAsync`; width-1 wrapper keeps `GenerateAsync` |
| `src/Cotabby.Inference/LlamaRuntimeCore.cs:10-20` | `LlamaGenerationOptions.BeamWidth` (default 1) |
| `src/Cotabby.Inference/LlamaSuggestionEngine.cs:35-117` | `GenerateCandidatesAsync`; per-candidate normalization |
| `src/Cotabby.Core/Suggestions/ISuggestionEngine.cs` | widen with the candidates method (additive) |
| `src/Cotabby.App/Settings/AppSettings.cs` | add `BeamWidth` (default `1`) |
| `src/Cotabby.App/Hosting/AppHost.cs:133-169` | push width into engine settings |
| `src/Cotabby.App/UI/SettingsWindow.xaml.cs:932-970` | Advanced-pane width control |
| `tests/Cotabby.Core.Tests/BeamDecodeTests.cs` | **new** (needs `Cotabby.Inference` reference — already present, `Cotabby.Core.Tests.csproj`) |

## Risks

- **Determinism regression**: beam expansion must not introduce
  tie-breaking nondeterminism (stable sort by score, then insertion
  order) — pinned by tests with fixed logits.
- **Latency**: width multiplies decode steps; the coordinator's 10 s
  watchdog (`SuggestionCoordinator.cs:533`) covers a width-4 decode on
  slow hardware only marginally — width > 2 emits a settings warning and
  is measured by the benchmark CLI before wider values are allowed.
- Constraint interaction bugs (repetition guard per beam, memory remove
  per beam at `:225-233`): the KV management gets more complex; keep
  beams sharing one sequence with careful `MemorySequenceRemove` scoping,
  or fall back to sequential decodes per beam (simpler, slower — the
  benchmark CLI decides).

## Test plan (unit)

`tests/Cotabby.Core.Tests/BeamDecodeTests.cs` (synthetic logits via the
inference test seams — follow `DegenerationGuardSmokeTests` style):

- `WidthOne_MatchesGreedyOutput`
- `WidthThree_ReturnsUpToThreeCandidates`
- `CandidatesOrderedByScoreDescending`
- `TiesBrokenDeterministically`
- `SuppressedCandidateDroppedBeforeExposure`
- `RepetitionGuardAppliesPerBeam`
- `SingleLineStopAppliesPerBeam`
- `EosStopAppliesPerBeam`
- `WidthAboveMaximum_ClampsToFour`
- `WidthOneWithFim_Unchanged` (FIM path stays greedy)

## Manual Windows smoke steps

1. Width 1 (default): full regression suite + one real suggestion — must
   be indistinguishable from v0.4.1.
2. Width 2–3 in Notepad with a mid-word context: confirm suggestions
   still appear and the log's decode line reports the width.
3. Measure latency on the owner's hardware via the benchmark CLI before
   raising width above 2; confirm the 10 s watchdog never trips.
4. Width 4 on the large model (`tabby-2-pro`): confirm bounded latency
   and no memory growth (GPU/CPU readouts in the Performance pane).
5. Confirm the alternatives popup (next change) consumes the candidates
   end-to-end.

## Rollback

- `BeamWidth=1` → `GenerateWithBeamAsync` is a thin wrapper over the
  shipped greedy path; `GenerateCandidatesAsync` returns one candidate.
- Code revert restores the single-decode core; `ISuggestionEngine`
  widening is additive so old callers compile unchanged mid-rollback.

## Privacy & security constraints

- Candidates are decoded suggestion text only — no field text is ever
  decoded (credential fields produce no requests, `TriggerGeneration`
  `:460`).
- Beam state is in-memory, process-lifetime; nothing persisted (no new
  disk surface).
- Determinism preserved for debugging/log-joinder (`RequestId` correlation
  unchanged).
- No cloud calls; local llama.cpp decode only.
