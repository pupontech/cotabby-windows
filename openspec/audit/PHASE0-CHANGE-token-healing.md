# Token healing

**Target phase:** 3. **Upstream provenance:** none. **Risk level:**
Medium — changes decoded text boundaries; must never corrupt valid
output. **Flag:** none (always-on normalizer improvement, covered by
existing tests; `TokenHealingEnabled` escape hatch default `true`).

## Why

The decode forces mid-word continuation at the first step
(`LlamaRuntimeCore.SelectToken` `:248-294`, `forceWordContinuation`,
`MidWordContinuationPolicy.ShouldForceContinuation`), but BPE tokenizers
split words at boundaries the policy can't predict: when the forced
continuation fails or the model starts with a space+partial word, the
surfaced suggestion can begin mid-word, carry a doubled leading space
against `KnownPrecedingText` (`SuggestionSession.cs:33-41`), or split a
surrogate pair at the 1000-char prefix window
(`SuggestionRequestFactory.cs:15-16`, `CompletionPromptBudget` already
guards surrogate splits in the prompt — `CHANGELOG.md:68-69`). The result
is the classic "ghost text starts in the middle of a word" artifact and
the spacing duplication the acceptance path has to re-derive
(`SuggestionSessionReconciler.InsertionChunk`).

## What Changes

- New pure `Cotabby.Core.Support.TokenHealer` operating on the decoded
  string + the prompt prefix:
  - **Boundary repair**: if the suggestion's first bytes form a partial
    word whose letters could complete the prefix's last word, either drop
    the partial prefix (when `ForceWordContinuation` was on and the
    partial matches the prefix tail) or insert the missing separator —
    decided by comparing against `KnownPrecedingText`'s last token,
    mirroring the emoji controller's backspace-eat precedent
    (`EmojiPickerController.cs:130-158`).
  - **Space normalization**: collapse a leading space when the preceding
    text already ends with whitespace; never introduce doubled spaces
    (the reconciler's re-anchor path `:1000-1032` shows why base/spacing
    drift breaks later accepts).
  - **Surrogate safety**: if the decoded text starts with a lone low
    surrogate (prefix window cut a pair), drop it and log a count — never
    emit an invalid UTF-16 unit to the inserter.
- `SuggestionTextNormalizer` (`src/Cotabby.Core/Support/SuggestionTextNormalizer.cs`)
  calls `TokenHealer` between raw decode and the echo/duplication filters,
  so every existing normalizer test composes unchanged.
- The healer reports *what it healed* as an enum (count-safe, no text) so
  the Phase 1 funnel can measure artifact rates and the Phase 4 benchmark
  can quantify the improvement.
- New `AppSettings.TokenHealingEnabled` (default `true` — pure
  improvement; escape hatch for A/B comparison).

## Non-goals

- No re-tokenization or model-level fixes (llama.cpp-side sampling is out
  of scope; this is a post-decode text repair).
- No dictionary-based spelling correction.
- No changes to the prompt pipeline or `MidWordContinuationPolicy` itself.

## Proposal outline (proposal.md)

```markdown
## ADDED Requirements

### Requirement: Token-boundary artifacts in decoded suggestions are repaired

The app MUST repair decoded suggestions that begin mid-word, with a
doubled leading space against preceding text, or with a split surrogate
pair, before presentation. Repairs MUST be deterministic, MUST NOT alter
otherwise-valid suggestions, and MUST report the repair kind without
storing suggestion text.

#### Scenario: Mid-word start is healed to the prefix's word
- **WHEN** the decode begins with a partial continuation of the prefix's
  last word
- **THEN** the partial is merged or dropped so the suggestion reads
  continuously

#### Scenario: Leading space is not doubled
- **WHEN** preceding text ends with whitespace
- **THEN** the suggestion's leading whitespace is collapsed

#### Scenario: Lone low surrogate is dropped
- **WHEN** the decoded text begins with an unpaired surrogate
- **THEN** the invalid unit is dropped and no invalid text reaches the
  inserter

#### Scenario: Valid suggestion is unchanged
- **WHEN** the decoded suggestion has no boundary artifact
- **THEN** it is byte-identical to the pre-healing output
```

## Source files touched (verified)

| File | Change |
| --- | --- |
| `src/Cotabby.Core/Support/TokenHealer.cs` | **new** — pure repair logic |
| `src/Cotabby.Core/Support/SuggestionTextNormalizer.cs` | invoke healer in the normalize pipeline |
| `src/Cotabby.Core/Support/MidWordContinuationPolicy.cs` | share the word-boundary predicate with the healer |
| `src/Cotabby.App/Settings/AppSettings.cs` | add `TokenHealingEnabled` (default `true`) |
| `src/Cotabby.Core/Suggestions/SuggestionCoordinator.cs:479-490` | pass `KnownPrecedingText` into the heal context via the engine request flow |
| `tests/Cotabby.Core.Tests/TokenHealerTests.cs` | **new** |
| `tests/Cotabby.Core.Tests/SuggestionTextNormalizerTests.cs` | extend (compose through pipeline) |

## Risks

- Over-healing corrupts valid suggestions: the healer must be
  conservative — repair only when the artifact signature is exact, and
  the byte-identical-when-clean case is pinned by test.
- `KnownPrecedingText` can lag UIA (`SuggestionSession.cs:33-41`): the
  healer prefers *dropping* a partial over *inserting* a separator when
  the preceding text is uncertain (drop is recoverable — the user retypes
  one char; corruption is not).
- The engine currently returns one final chunk; healing happens before
  normalization, so the chunk contract is unchanged.

## Test plan (unit)

`tests/Cotabby.Core.Tests/TokenHealerTests.cs`:

- `MidWordStart_IsMergedWithPrefixTail`
- `MidWordStartWithForceContinuation_IsDropped`
- `LeadingSpaceWithWhitespacePrefix_IsCollapsed`
- `LeadingSpaceWithNonWhitespacePrefix_IsPreserved`
- `LoneLowSurrogate_IsDropped`
- `LoneHighSurrogate_IsDropped`
- `ValidSurrogatePair_Survives`
- `CleanSuggestion_IsUnchanged`
- `EmojiAtStart_Survives` (surrogate pair + non-ASCII lead)
- `HealKind_IsReportedWithoutText` (enum output only)

## Manual Windows smoke steps

1. Type a partial word (`fibona`) and pause at the caret; confirm the
   ghost text never starts mid-word (heals to `…fibonacci` continuation).
2. Type a trailing space then trigger; confirm the suggestion doesn't
   start with a doubled space.
3. Type an emoji then trigger a suggestion; confirm the surrogate pair
   survives in the suggestion (log hex shows valid pairs, no lone
   surrogates).
4. Run the regression smoke (`tools/Cotabby.Tools.RegressionSmoke`) and
   the full unit suite — normalizer composition must stay green.
5. Toggle `TokenHealingEnabled=false`; confirm the artifact reappears in
   at least one crafted case (proves the healer is what changed behavior).

## Rollback

- `TokenHealingEnabled=false` → normalizer skips the healer; v0.4.1
  output restored bit-for-bit.
- Code revert removes `TokenHealer` and its single call site in the
  normalizer.

## Privacy & security constraints

- Healing operates on decoded suggestion text in memory; no persistence,
  no new text reads (the preceding text is already in the request).
- Credential-field protections unchanged (healing runs inside the engine,
  which never receives credential-field requests).
- No cloud calls; pure local string logic.
